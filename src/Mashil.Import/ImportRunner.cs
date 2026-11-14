using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import;

/// <summary>
/// 원본 파일 1개를 임포트한다 (P1: DATA-02·03·04·11, G1·G11·G12).
/// <list type="number">
/// <item>직전 성공과 파일 해시가 같으면 Skipped (force 제외)</item>
/// <item>헤더 검증 실패 → Rejected, DB 의 화장실 데이터는 건드리지 않음</item>
/// <item>행마다 원본(raw) 해시를 비교해 바뀐 행만 원본 갱신 → 매핑 → 오버라이드 적용</item>
/// <item>배치(기본 500행) 단위로 한 트랜잭션 — 화장실 행과 원본 행이 함께 커밋됨</item>
/// </list>
/// 누락 레코드 처리(Missing/Removed)와 사전 검증의 크기·행 수 비교는 P1.5.
/// </summary>
public sealed class ImportRunner(MashilDbContext db, TimeProvider? clock = null)
{
    /// <summary>원본 → 표준 변환 로직 버전. 매핑 규칙을 바꾸면 올린다.</summary>
    public const string NormalizerVersion = "0.1.0";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public int BatchSize { get; init; } = 500;

    public static ISourceAdapter AdapterFor(string sourceCode) => sourceCode switch
    {
        DataSourceCodes.LocalDataPublicRestroom => new LocalDataPublicRestroomAdapter(),
        _ => throw new NotSupportedException($"알 수 없는 데이터 소스: {sourceCode}"),
    };

    public async Task<ImportRun> RunAsync(
        string sourceCode,
        SourceFile file,
        ImportTrigger trigger,
        bool force = false,
        IProgress<ImportRun>? progress = null,
        CancellationToken ct = default)
    {
        var source = await db.DataSources.SingleAsync(s => s.Code == sourceCode, ct);
        var adapter = AdapterFor(sourceCode);
        var now = Now();

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

        if (!force)
        {
            var sameFile = await db.ImportRuns.AnyAsync(r =>
                r.SourceId == source.Id && r.Status == ImportRunStatus.Succeeded
                && r.FileSha256 == file.Sha256 && r.NormalizerVersion == NormalizerVersion, ct);
            if (sameFile)
            {
                run.Status = ImportRunStatus.Skipped;
                run.Message = "직전 성공과 같은 파일";
                run.FinishedAt = now;
                db.ImportRuns.Add(run);
                await db.SaveChangesAsync(ct);
                return run;
            }
        }

        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            await ProcessAsync(source, adapter, file, run, progress, ct);
            run.Status = ImportRunStatus.Succeeded;
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

    private async Task ProcessAsync(DataSource source, ISourceAdapter adapter, SourceFile file, ImportRun run, IProgress<ImportRun>? progress, CancellationToken ct)
    {
        using var reader = file.OpenText();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<SourceRow>(BatchSize);

        // 헤더 검증은 첫 MoveNext 에서 일어난다 → 실패하면 아무것도 쓰지 않은 채 Rejected
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
            if (toilet.Status == ToiletStatus.Missing) toilet.Status = ToiletStatus.Active; // 다시 나타남
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
