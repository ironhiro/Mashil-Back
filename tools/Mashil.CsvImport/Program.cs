using Mashil.Domain;
using Mashil.Import;
using Mashil.Import.Geocoding;
using Mashil.Import.LocalData;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// 사용법:
//   dotnet run --project tools/Mashil.CsvImport -- <CSV 경로> [옵션]     파일로 임포트 (관리자 수동 업로드와 같은 경로)
//   dotnet run --project tools/Mashil.CsvImport -- --fetch [옵션]         LOCALDATA 에서 내려받아 임포트 (일일 작업과 같은 경로)
//   dotnet run --project tools/Mashil.CsvImport -- --approve-removals=<runId>   보류된 대량 삭제 승인
// 옵션:
//   --source=<코드>  기본 LOCALDATA_PUBLIC_RESTROOM
//   --dry-run        DB 없이 인코딩 판별, 헤더 검증, 행 수, 관리번호 중복만 확인
//   --force          직전 성공과 같은 파일이어도 다시 처리
//   --no-validate    크기·행 수 비교 검증을 건너뜀 (원본 형식이 정상적으로 크게 바뀐 경우)
// 스키마는 만들지 않는다. 먼저 `dotnet ef database update` 로 마이그레이션을 적용할 것.

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddUserSecrets("mashil-api-3f7e82a4-e2c8-4f6e-9d1a-1b9c54a6f8a2") // Mashil.Api 와 공유 (Kakao:RestApiKey)
    .AddEnvironmentVariables()
    .Build();

var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
string? Flag(string name) => flags.FirstOrDefault(f => f.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
bool Has(string name) => flags.Contains(name, StringComparer.OrdinalIgnoreCase);

var sourceCode = Flag("--source") ?? DataSourceCodes.LocalDataPublicRestroom;
var csvPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var approveRunId = long.TryParse(Flag("--approve-removals"), out var rid) ? rid : (long?)null;
var policy = config.GetSection("Import:Policy").Get<ImportPolicy>() ?? ImportPolicy.Default;

// --- 개방시간 파서 커버리지 리포트: DB 없이 ---
if (Has("--hours-report"))
{
    var file = await LoadFileAsync(null);
    using var reader = file.OpenText();
    var rows = ImportRunner.AdapterFor(sourceCode).Read(reader).ToList();
    var outcomes = rows.Select(r => (Key: OpeningHoursParser.MappingKey(r["개방시간"], r["개방시간상세"]),
        Outcome: OpeningHoursParser.Parse(r["개방시간"], r["개방시간상세"]))).ToList();

    Console.WriteLine($"--- 개방시간 파싱 결과 ({rows.Count:N0}행, 조합 {outcomes.Select(o => o.Key).Distinct().Count():N0}종) ---");
    foreach (var g in outcomes.GroupBy(o => o.Outcome.Status).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key,-16} {g.Count(),7:N0}  {g.Count() * 100.0 / rows.Count,5:N1}%");
    Console.WriteLine("--- 이슈 ---");
    foreach (var g in outcomes.Where(o => o.Outcome.Issue is not null).GroupBy(o => o.Outcome.Issue).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key,-16} {g.Count(),7:N0}  {g.Count() * 100.0 / rows.Count,5:N1}%");
    var top = int.TryParse(Flag("--top"), out var n) ? n : 30;
    Console.WriteLine($"--- 판정 불가(Unparsed) 상위 {top} ---");
    foreach (var g in outcomes.Where(o => o.Outcome.Issue == IssueType.Unparsed).GroupBy(o => o.Key).OrderByDescending(g => g.Count()).Take(top))
        Console.WriteLine($"  {g.Count(),5:N0}  {g.Key}");
    return 0;
}

// --- dry-run: DB 없이 ---
if (Has("--dry-run"))
{
    var file = await LoadFileAsync(null);
    var adapter = ImportRunner.AdapterFor(sourceCode);
    var skipped = new List<string>();
    var keys = new HashSet<string>(StringComparer.Ordinal);
    int total = 0, dup = 0;
    try
    {
        using var reader = file.OpenText();
        foreach (var row in adapter.Read(reader, (line, why) => skipped.Add($"{line}: {why}")))
        {
            total++;
            if (!keys.Add(row.SourceKey)) dup++;
        }
    }
    catch (SourceSchemaException ex)
    {
        Console.Error.WriteLine($"[거부] {ex.Message}");
        return 2;
    }
    Console.WriteLine($"""
        --- dry-run ---
        행 수          : {total:N0}
        관리번호 중복  : {dup:N0}
        건너뛴 행      : {skipped.Count:N0}
        """);
    foreach (var s in skipped.Take(10)) Console.WriteLine("  skip " + s);
    return 0;
}

var connStr = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres 가 필요합니다.");
await using var db = new MashilDbContext(new DbContextOptionsBuilder<MashilDbContext>()
    .UseNpgsql(connStr, npg => npg.UseNetTopologySuite())
    .Options);

var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
if (pending.Count > 0)
{
    Console.Error.WriteLine($"[중단] 적용되지 않은 마이그레이션 {pending.Count}개: {string.Join(", ", pending)}");
    Console.Error.WriteLine("        dotnet ef database update --project src/Mashil.Infrastructure --startup-project src/Mashil.Api");
    return 3;
}

// --- 보류된 삭제 승인 ---
if (approveRunId is { } runId)
{
    var removed = await MissingRecordProcessor.ApprovePendingRemovalsAsync(db, runId, policy, CancellationToken.None);
    Console.WriteLine($"run #{runId}: {removed:N0}건 Removed 처리");
    return 0;
}

// --- 기존 좌표를 오버라이드로 옮기기 (한 번만 하면 됨, 다시 해도 중복 없음) ---
if (Has("--adopt-coordinates"))
{
    var adopted = await GeocodingService.AdoptExistingCoordinatesAsync(db, CancellationToken.None);
    Console.WriteLine($"기존 좌표 {adopted:N0}건을 오버라이드로 옮김");
    return 0;
}

// --- 지오코딩 N건 (검증용. 상시 처리는 Mashil.Worker 의 GeocodeWorker) ---
if (Flag("--geocode") is { } geocodeArg)
{
    var key = config["Kakao:RestApiKey"];
    if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("[중단] Kakao:RestApiKey 가 없습니다 (user-secrets)."); return 6; }
    var limit = int.TryParse(geocodeArg, out var gl) ? gl : 50;
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var service = new GeocodingService(db, new KakaoAddressGeocoder(http, key),
        new GeocodingOptions { BatchSize = Math.Min(limit, 50), DailyBudget = int.TryParse(config["Geocode:DailyBudget"], out var b) ? b : 80_000 });
    Console.WriteLine($"지오코딩 대상 {await service.Pending().CountAsync():N0}건 중 최대 {limit:N0}건 처리");
    int done = 0, ok = 0, fail = 0, calls = 0, hits = 0;
    while (done < limit)
    {
        var r = await service.RunBatchAsync(Math.Min(50, limit - done));
        done += r.Processed; ok += r.Matched; fail += r.NoMatch; calls += r.ApiCalls; hits += r.CacheHits;
        if (r.Processed == 0 || r.BudgetExhausted || r.GeocoderUnavailable)
        {
            if (r.BudgetExhausted) Console.WriteLine("일일 예산 소진으로 중단");
            if (r.GeocoderUnavailable) Console.WriteLine("Kakao 호출 불가(429/401)로 중단");
            break;
        }
    }
    Console.WriteLine($"처리 {done:N0} · 성공 {ok:N0} · 실패 {fail:N0} · API {calls:N0}회 · 캐시 {hits:N0}회 · 성공률 {(done == 0 ? 0 : ok * 100.0 / done):N1}%");
    return 0;
}

// --- 재정규화: 파일 없이 저장된 원본으로 (DATA-14) ---
if (Has("--renormalize"))
{
    IProgress<ImportRun>? rp = Console.IsOutputRedirected ? null : new Progress<ImportRun>(r => Console.Write($"\r  재정규화 {r.UpdatedRows:N0} 행   "));
    var rr = await new ImportRunner(db, policy).RenormalizeAsync(sourceCode, onlyOutdated: Has("--only-outdated"), progress: rp);
    Console.WriteLine();
    Console.WriteLine($"재정규화 run #{rr.Id}: {rr.Status}{(rr.Message is null ? "" : " — " + rr.Message)} · 처리 {rr.UpdatedRows:N0} / 원본 {rr.TotalRows:N0} · {(rr.FinishedAt - rr.StartedAt)?.TotalSeconds:N1}s");
    return rr.Status == ImportRunStatus.Succeeded ? 0 : 1;
}

var source = await db.DataSources.AsNoTracking().SingleAsync(s => s.Code == sourceCode);
SourceFile input;
try
{
    input = await LoadFileAsync(source);
}
catch (LocalDataDownloadException ex)
{
    Console.Error.WriteLine($"[다운로드 실패] {ex.Message}");
    Console.Error.WriteLine("  기존 데이터는 그대로입니다. 브라우저로 받은 파일을 경로로 넘겨 임포트할 수 있습니다.");
    return 5;
}

// 파이프·리다이렉트로 출력이 넘어가면 \r 덮어쓰기가 한 줄로 쌓이므로 진행률은 터미널에서만
IProgress<ImportRun>? progress = Console.IsOutputRedirected ? null
    : new Progress<ImportRun>(r => Console.Write($"\r  처리 {r.TotalRows:N0} 행 (신규 {r.InsertedRows:N0} / 변경 {r.UpdatedRows:N0} / 동일 {r.UnchangedRows:N0})   "));
ImportRun run;
try
{
    run = await new ImportRunner(db, policy).RunAsync(
        sourceCode, input,
        Has("--fetch") ? ImportTrigger.Manual : ImportTrigger.Upload,
        new ImportOptions { Force = Has("--force"), SkipValidation = Has("--no-validate") },
        progress);
}
catch (ImportBusyException ex)
{
    Console.Error.WriteLine($"[중단] {ex.Message}");
    return 4;
}
Console.WriteLine();

Console.WriteLine($"""
    --- 임포트 결과 (run #{run.Id}) ---
    상태      : {run.Status}{(run.Message is null ? "" : " — " + run.Message)}
    전체 행   : {run.TotalRows:N0}
    신규      : {run.InsertedRows:N0}
    변경      : {run.UpdatedRows:N0}
    변경 없음 : {run.UnchangedRows:N0}
    건너뜀    : {run.SkippedRows:N0}
    누락 표시 : {run.MissingRows:N0}
    복귀      : {run.RestoredRows:N0}
    삭제      : {run.RemovedRows:N0} (대상 {run.RemovalCandidates:N0})
    소요      : {(run.FinishedAt - run.StartedAt)?.TotalSeconds:N1}s
    """);
if (run.Status == ImportRunStatus.PendingApproval)
    Console.WriteLine($"    → 확인 후 승인: --approve-removals={run.Id}");

return run.Status is ImportRunStatus.Succeeded or ImportRunStatus.Skipped or ImportRunStatus.PendingApproval ? 0 : 1;

async Task<SourceFile> LoadFileAsync(DataSource? src)
{
    SourceFile file;
    if (Has("--fetch"))
    {
        if (src is null) throw new InvalidOperationException("--fetch 는 DB 의 데이터 소스 정보(URL)가 필요해서 --dry-run 과 함께 쓸 수 없습니다.");
        var opts = config.GetSection("Import:LocalData").Get<LocalDataFetchOptions>() ?? new LocalDataFetchOptions();
        Console.WriteLine($"다운로드: {src.DownloadUrl}");
        file = await new LocalDataFileFetcher(opts).FetchAsync(src, CancellationToken.None);
    }
    else
    {
        file = await SourceFile.ReadAsync(csvPath ?? throw new ArgumentException("CSV 경로 또는 --fetch 가 필요합니다."));
    }

    Console.WriteLine($"""
        파일     : {file.FileName} ({file.Bytes.LongLength:N0} bytes)
        SHA-256  : {file.Sha256}
        인코딩   : {file.Encoding.ToLabel()} (내용으로 판별)
        소스     : {sourceCode}
        """);
    return file;
}
