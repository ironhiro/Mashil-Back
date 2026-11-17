using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Import.Normalization;
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
    /// <summary>
    /// 원본 → 표준 변환 로직 버전. 매핑 규칙을 바꾸면 올린다.
    /// 화장실 행의 버전이 이보다 낮으면 원본이 그대로여도 다시 정규화한다.
    /// 0.2.0: 개방시간 구조화, 구분 코드, 전화번호 표기, 이상치 이슈 (P2)
    /// </summary>
    public const string NormalizerVersion = "0.2.3"; // 0.2.1: 일요일 정보가 없으면 공휴일 규칙을 따름. 0.2.2: 평일 시간만 있으면 주말 휴무. 0.2.3: 공휴일도 휴무

    private NormalizationContext _ctx = NormalizationContext.Empty;

    private readonly ImportPolicy _policy = policy ?? ImportPolicy.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public int BatchSize { get; init; } = 500;

    public static ISourceAdapter AdapterFor(string sourceCode) => sourceCode switch
    {
        DataSourceCodes.LocalDataPublicRestroom => new LocalDataPublicRestroomAdapter(),
        _ => throw new NotSupportedException($"알 수 없는 데이터 소스: {sourceCode}"),
    };

    /// <summary>
    /// 직전에 원본 파일을 읽어 반영까지 끝난 실행 (Succeeded 또는 PendingApproval). 사전 검증·같은 파일 건너뛰기의 비교 기준.
    /// 재정규화(Renormalize)는 파일 없이 저장된 원본 일부만 다시 계산하므로 크기·행 수가 원본 파일과 무관하다.
    /// 이를 기준으로 삼으면 3행짜리 파일도 통과해 전체가 Missing 이 되므로 제외한다 (DATA-20).
    /// </summary>
    public static Task<ImportRun?> LastAppliedAsync(MashilDbContext db, int sourceId, CancellationToken ct) =>
        LastAppliedQuery(db.ImportRuns.AsNoTracking(), sourceId).FirstOrDefaultAsync(ct);

    /// <summary><see cref="LastAppliedAsync"/> 의 조건. 최신순 정렬 — 첫 행이 기준.</summary>
    public static IQueryable<ImportRun> LastAppliedQuery(IQueryable<ImportRun> runs, int sourceId) =>
        runs.Where(r => r.SourceId == sourceId
                     && (r.Status == ImportRunStatus.Succeeded || r.Status == ImportRunStatus.PendingApproval)
                     && r.Trigger != ImportTrigger.Renormalize
                     && r.FileSizeBytes > 0)
            .OrderByDescending(r => r.Id);

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
        await AbandonStaleRunsAsync(source.Id, now, ct);

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
            _ctx = await LoadContextAsync(ct);
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

        var toilets = await WithNormalizationGraph(db.Toilets)
            .Where(t => keys.Contains(t.ManagementNo))
            .ToDictionaryAsync(t => t.ManagementNo, StringComparer.Ordinal, ct);

        foreach (var row in rows)
        {
            var hash = row.ComputeHash();
            raws.TryGetValue(row.SourceKey, out var raw);
            toilets.TryGetValue(row.SourceKey, out var existing);

            if (raw is not null && raw.RowHash == hash && raw.ToiletId is not null
                && existing?.NormalizerVersion == NormalizerVersion)
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

            run.NewIssues += NormalizeRow(row, toilet, _ctx, now);
            raw.Toilet = toilet;

            if (isNew) run.InsertedRows++;
            else run.UpdatedRows++;
        }

        run.HeartbeatAt = now;
        await db.SaveChangesAsync(ct); // 배치 = 한 트랜잭션
        db.ChangeTracker.Clear();
        db.ImportRuns.Attach(run);
    }

    /// <summary>
    /// 원본 → 화장실 매핑 + 정규화 + 오버라이드, 그리고 자동 이슈 맞추기 (<see cref="IssueSync"/>).
    /// 임포트·재정규화·관리자 직접 수정이 같은 규칙을 쓴다. 화장실은 <see cref="WithNormalizationGraph"/> 로 불러온 것이어야 한다.
    /// </summary>
    /// <returns>새로 생긴 이슈 수 (DATA-23).</returns>
    public static int NormalizeRow(SourceRow row, Toilet toilet, NormalizationContext ctx, DateTime now)
    {
        var today = DateOnly.FromDateTime(now.AddHours(9));
        var (roadBefore, lotBefore, nameBefore) = (toilet.RoadAddress, toilet.LotAddress, toilet.Name);
        var drafts = LocalDataToiletMapper.Apply(row, toilet, ctx, today);

        // 정규화가 다시 계산하는 Open 이슈만 맞춘다 (지오코딩 이슈는 GeocodingService 가 관리)
        var added = IssueSync.Replace(toilet, drafts, i => i.FieldName != IssueFields.Geocode, now);

        // 오버라이드 적용 + 주소 변경 시 지오코딩 무효화 + 개방시간 오버라이드 재파싱
        ToiletRecompute.AfterOverrides(toilet, roadBefore, lotBefore, ctx, now, nameBefore);

        toilet.NormalizerVersion = NormalizerVersion;
        toilet.UpdatedAt = now;
        return added;
    }

    /// <summary>정규화에 필요한 연관 데이터. 이슈는 상태와 상관없이 불러온다 (해결·무시한 이슈를 다시 만들지 않으려고).</summary>
    public static IQueryable<Toilet> WithNormalizationGraph(IQueryable<Toilet> q) => q
        .Include(t => t.Overrides.Where(o => o.IsActive))
        .Include(t => t.Schedules)
        .Include(t => t.Issues)
        .AsSplitQuery();
    // 주: Overrides 는 활성만 로드한다. 주소 변경 시 비활성화하는 대상도 활성 중에서만 고르므로 충분하다.

    /// <summary>저장된 원본 행(raw payload) → <see cref="SourceRow"/>.</summary>
    public static SourceRow RowOf(RawToiletRecord raw) =>
        new(raw.SourceKey, System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(raw.PayloadJson) ?? [], 0);

    private Task<NormalizationContext> LoadContextAsync(CancellationToken ct) => LoadContextAsync(db, ct);

    /// <summary>활성 매핑 사전.</summary>
    public static async Task<NormalizationContext> LoadContextAsync(MashilDbContext db, CancellationToken ct) =>
        NormalizationContext.FromMappings(await db.ValueMappings.AsNoTracking().Where(m => m.IsActive).ToListAsync(ct));

    /// <summary>
    /// 파일 없이 저장된 원본(raw)으로 다시 정규화한다 (DATA-14). 정규화 규칙·매핑 사전이 바뀐 뒤 사용.
    /// 누락 처리는 하지 않는다 (새 원본 정보가 없으므로).
    /// <paramref name="toiletIds"/> 를 주면 그 화장실만 다시 정규화한다 (매핑 사전을 바꾼 직후, DATA-13).
    /// 그중 원본이 없는 화장실(제보로 추가된 곳)은 저장된 개방시간 문장만 새 매핑으로 다시 해석한다.
    /// </summary>
    public async Task<ImportRun> RenormalizeAsync(string sourceCode, bool onlyOutdated = false, IProgress<ImportRun>? progress = null,
        CancellationToken ct = default, IReadOnlyCollection<long>? toiletIds = null, string? label = null)
    {
        var source = await db.DataSources.AsNoTracking().SingleAsync(s => s.Code == sourceCode, ct);
        await using var importLock = await ImportLock.TryAcquireAsync(db, source.Id, ct)
            ?? throw new ImportBusyException(sourceCode);

        var now = Now();
        await AbandonStaleRunsAsync(source.Id, now, ct);
        var run = new ImportRun
        {
            SourceId = source.Id, Trigger = ImportTrigger.Renormalize, FileSha256 = "", FileName = label ?? "(raw)",
            NormalizerVersion = NormalizerVersion, StartedAt = now, HeartbeatAt = now,
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            _ctx = await LoadContextAsync(ct);
            var scope = toiletIds?.Distinct().ToList();
            var covered = new HashSet<long>();
            long lastId = 0;
            while (true)
            {
                var rawQuery = db.RawToiletRecords.Where(r => r.SourceId == source.Id && r.Id > lastId && r.ToiletId != null);
                if (scope is not null) rawQuery = rawQuery.Where(r => scope.Contains(r.ToiletId!.Value));
                var raws = await rawQuery.OrderBy(r => r.Id).Take(BatchSize).ToListAsync(ct);
                if (raws.Count == 0) break;
                lastId = raws[^1].Id;

                var ids = raws.Select(r => r.ToiletId!.Value).ToList();
                var query = WithNormalizationGraph(db.Toilets).Where(t => ids.Contains(t.Id));
                if (onlyOutdated) query = query.Where(t => t.NormalizerVersion != NormalizerVersion);
                var toilets = await query.ToDictionaryAsync(t => t.Id, ct);

                var batchNow = Now();
                foreach (var raw in raws)
                {
                    run.TotalRows++;
                    covered.Add(raw.ToiletId!.Value);
                    if (!toilets.TryGetValue(raw.ToiletId!.Value, out var toilet)) { run.UnchangedRows++; continue; }
                    run.NewIssues += NormalizeRow(RowOf(raw), toilet, _ctx, batchNow);
                    run.UpdatedRows++;
                }

                run.HeartbeatAt = batchNow;
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                db.ImportRuns.Attach(run);
                progress?.Report(run);
            }

            if (scope is not null)
                await ReparseHoursWithoutRawAsync(scope.Where(id => !covered.Contains(id)).ToList(), run, ct);
            run.Status = ImportRunStatus.Succeeded;
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

    /// <summary>락을 잡은 상태에서 남아 있는 Running 실행은 죽은 프로세스의 것이므로 Abandoned 로 정리한다.</summary>
    private Task<int> AbandonStaleRunsAsync(int sourceId, DateTime now, CancellationToken ct) =>
        db.ImportRuns
            .Where(r => r.SourceId == sourceId && r.Status == ImportRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ImportRunStatus.Abandoned)
                .SetProperty(r => r.Message, "프로세스 중단으로 정리됨 (다음 실행에서 감지)")
                .SetProperty(r => r.FinishedAt, now), ct);

    /// <summary>원본이 없는 화장실(제보로 추가)은 저장된 개방시간 문장을 새 매핑 사전으로 다시 해석한다.</summary>
    private async Task ReparseHoursWithoutRawAsync(List<long> ids, ImportRun run, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var now = Now();
            var toilets = await WithNormalizationGraph(db.Toilets).Where(t => chunk.Contains(t.Id)).ToListAsync(ct);
            foreach (var t in toilets)
            {
                run.TotalRows++;
                var drafts = new List<IssueDraft>();
                LocalDataToiletMapper.ApplyHours(t, OpeningHoursParser.Parse(t.OpenHours, t.OpenHoursDetail, _ctx.For(IssueFields.OpeningHours)), drafts);
                run.NewIssues += IssueSync.Replace(t, drafts, i => i.FieldName == IssueFields.OpeningHours, now);
                t.NormalizerVersion = NormalizerVersion;
                t.UpdatedAt = now;
                run.UpdatedRows++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            db.ImportRuns.Attach(run);
        }
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
