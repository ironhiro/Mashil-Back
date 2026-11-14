using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import;

/// <summary>
/// 원본 파일 1개를 임포트한다.
/// <list type="number">
/// <item>소스별 advisory lock — 이미 실행 중이면 <see cref="ImportBusyException"/> (DATA-22)</item>
/// <item>락을 잡은 상태에서 남아 있는 Running 실행은 죽은 것이므로 Abandoned 로 정리</item>
/// <item>직전 성공과 파일 해시가 같으면 Skipped (Force 제외)</item>
/// <item>사전 검증(헤더, 행 수·크기·손상률) 실패 → Rejected, 화장실 데이터는 건드리지 않음 (DATA-20)</item>
/// <item>행마다 원본(raw) 해시를 비교해 바뀐 행만 원본 갱신 → 매핑 → 오버라이드 적용 (DATA-03·11)</item>
/// <item>배치(기본 500행) 단위로 한 트랜잭션 — 화장실 행과 원본 행이 함께 커밋</item>
/// <item>성공하면 누락 처리 (DATA-15). 삭제 대상이 많으면 PendingApproval</item>
/// </list>
/// 중간에 끊겨도 다시 실행하면 같은 결과가 된다 (해시 비교 + 관리번호 upsert).
/// </summary>
public sealed class ImportRunner(MashilDbContext db, ImportPolicy? policy = null, TimeProvider? clock = null)
{
    /// <summary>원본 → 표준 변환 로직 버전. 매핑 규칙을 바꾸면 올린다.</summary>
    public const string NormalizerVersion = "0.1.0";

    private readonly ImportPolicy _policy = policy ?? ImportPolicy.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public int BatchSize { get; init; } = 500;

    public static ISourceAdapter AdapterFor(string sourceCode) => sourceCode switch
    {
        DataSourceCodes.LocalDataPublicRestroom => new LocalDataPublicRestroomAdapter(),
        _ => throw new NotSupportedException($"알 수 없는 데이터 소스: {sourceCode}"),
    };

    /// <summary>직전에 반영까지 끝난 실행 (Succeeded 또는 PendingApproval). 사전 검증의 비교 기준.</summary>
    public static Task<ImportRun?> LastAppliedAsync(MashilDbContext db, int sourceId, CancellationToken ct) =>
        db.ImportRuns.AsNoTracking()
            .Where(r => r.SourceId == sourceId && (r.Status == ImportRunStatus.Succeeded || r.Status == ImportRunStatus.PendingApproval))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<ImportRun> RunAsync(
        string sourceCode,
        SourceFile file,
        ImportTrigger trigger,
        ImportOptions? options = null,
        IProgress<ImportRun>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ImportOptions();
        var source = await db.DataSources.AsNoTracking().SingleAsync(s => s.Code == sourceCode, ct);
        var adapter = AdapterFor(sourceCode);

        await using var importLock = await ImportLock.TryAcquireAsync(db, source.Id, ct)
            ?? throw new ImportBusyException(sourceCode);

        var now = Now();
        await db.ImportRuns
            .Where(r => r.SourceId == source.Id && r.Status == ImportRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ImportRunStatus.Abandoned)
                .SetProperty(r => r.Message, "프로세스 중단으로 정리됨 (다음 실행에서 감지)")
                .SetProperty(r => r.FinishedAt, now), ct);

        var run = new ImportRun
        {
            SourceId = source.Id,
            Trigger = trigger,
            FileName = file.FileName,
            FileSha256 = file.Sha256,
            FileSizeBytes = file.Bytes.LongLength,
            DetectedEncoding = file.Encoding.ToLabel(),
            NormalizerVersion = NormalizerVersion,
            StartedAt = now,
            HeartbeatAt = now,
        };

        var last = await LastAppliedAsync(db, source.Id, ct);

        if (!options.Force && last is not null && last.FileSha256 == file.Sha256 && last.NormalizerVersion == NormalizerVersion)
            return await FinishEarlyAsync(run, ImportRunStatus.Skipped, "직전 성공과 같은 파일", ct);

        var check = ImportPreValidator.Validate(file, adapter, last, _policy, skipComparison: options.SkipValidation);
        if (!check.Accepted)
        {
            run.TotalRows = check.Rows;
            run.SkippedRows = check.Skipped;
            return await FinishEarlyAsync(run, ImportRunStatus.Rejected, check.Reason, ct);
        }

        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            await ProcessAsync(source, adapter, file, run, progress, ct);
            await MissingRecordProcessor.ProcessAsync(db, source.Id, run, _policy, ct);
            if (run.Status == ImportRunStatus.Running) run.Status = ImportRunStatus.Succeeded;
        }
        catch (SourceSchemaException ex)
        {
            run.Status = ImportRunStatus.Rejected;
            run.Message = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = ImportRunStatus.Failed;
            run.Message = ex.GetType().Name + ": " + ex.Message;
        }

        run.FinishedAt = Now();
        db.ChangeTracker.Clear();
        db.ImportRuns.Update(run);
        await db.SaveChangesAsync(CancellationToken.None);
        return run;
    }

    private async Task<ImportRun> FinishEarlyAsync(ImportRun run, ImportRunStatus status, string? message, CancellationToken ct)
    {
        run.Status = status;
        run.Message = message;
        run.FinishedAt = run.StartedAt;
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task ProcessAsync(DataSource source, ISourceAdapter adapter, SourceFile file, ImportRun run, IProgress<ImportRun>? progress, CancellationToken ct)
    {
        using var reader = file.OpenText();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<SourceRow>(BatchSize);

        foreach (var row in adapter.Read(reader, (_, _) => run.SkippedRows++))
        {
            run.TotalRows++;
            if (!seen.Add(row.SourceKey))
            {
                run.SkippedRows++; // 같은 파일 안 중복 관리번호
                continue;
            }

            batch.Add(row);
            if (batch.Count >= BatchSize)
            {
                await FlushAsync(source, batch, run, ct);
                batch.Clear();
                progress?.Report(run);
            }
        }

        if (batch.Count > 0) await FlushAsync(source, batch, run, ct);
        progress?.Report(run);
    }

    private async Task FlushAsync(DataSource source, List<SourceRow> rows, ImportRun run, CancellationToken ct)
    {
        var now = Now();
        var keys = rows.Select(r => r.SourceKey).ToList();

        var raws = await db.RawToiletRecords
            .Where(r => r.SourceId == source.Id && keys.Contains(r.SourceKey))
            .ToDictionaryAsync(r => r.SourceKey, StringComparer.Ordinal, ct);

        var toilets = await db.Toilets
            .Include(t => t.Overrides.Where(o => o.IsActive))
            .Where(t => keys.Contains(t.ManagementNo))
            .ToDictionaryAsync(t => t.ManagementNo, StringComparer.Ordinal, ct);

        foreach (var row in rows)
        {
            var hash = row.ComputeHash();
            raws.TryGetValue(row.SourceKey, out var raw);

            if (raw is not null && raw.RowHash == hash && raw.ToiletId is not null)
            {
                raw.LastSeenAt = now;
                raw.LastImportRunId = run.Id;
                raw.ConsecutiveMissing = 0;
                run.UnchangedRows++;
                continue;
            }

            if (raw is null)
            {
                raw = new RawToiletRecord
                {
                    SourceId = source.Id,
                    SourceKey = row.SourceKey,
                    PayloadJson = row.ToPayloadJson(),
                    RowHash = hash,
                    LastImportRunId = run.Id,
                    FirstSeenAt = now,
                };
                db.RawToiletRecords.Add(raw);
            }
            else
            {
                raw.PayloadJson = row.ToPayloadJson();
                raw.RowHash = hash;
            }
            raw.LastSeenAt = now;
            raw.LastImportRunId = run.Id;
            raw.ConsecutiveMissing = 0;

            var isNew = !toilets.TryGetValue(row.SourceKey, out var toilet);
            if (toilet is null)
            {
                toilet = new Toilet
                {
                    ManagementNo = row.SourceKey,
                    Kind = string.Empty,
                    Name = string.Empty,
                    Origin = ToiletOrigin.PublicData,
                    Status = ToiletStatus.Active,
                    CreatedAt = now,
                };
                db.Toilets.Add(toilet);
                toilets[row.SourceKey] = toilet;
            }

            LocalDataToiletMapper.Apply(row, toilet);
            OverrideApplier.Apply(toilet, toilet.Overrides);
            toilet.UpdatedAt = now;
            raw.Toilet = toilet;

            if (isNew) run.InsertedRows++;
            else run.UpdatedRows++;
        }

        run.HeartbeatAt = now;
        await db.SaveChangesAsync(ct); // 배치 = 한 트랜잭션
        db.ChangeTracker.Clear();
        db.ImportRuns.Attach(run);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
