using Mashil.Domain;
using Mashil.Import;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// 사용법:
//   dotnet run --project tools/Mashil.CsvImport -- <CSV 경로> [--source=LOCALDATA_PUBLIC_RESTROOM] [--force] [--dry-run]
//   --dry-run : DB 없이 인코딩 판별, 헤더 검증, 행 수, 관리번호 중복만 확인
//   --force   : 직전 성공과 같은 파일이어도 다시 처리
// 스키마는 만들지 않는다. 먼저 `dotnet ef database update` 로 마이그레이션을 적용할 것 (G10).

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
var csvPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? throw new ArgumentException("CSV 경로가 필요합니다.");
var sourceCode = flags.FirstOrDefault(f => f.StartsWith("--source=", StringComparison.OrdinalIgnoreCase))?["--source=".Length..]
    ?? DataSourceCodes.LocalDataPublicRestroom;
var dryRun = flags.Contains("--dry-run");
var force = flags.Contains("--force");

var file = await SourceFile.ReadAsync(csvPath);
Console.WriteLine($"""
    파일     : {file.FileName} ({file.Bytes.LongLength:N0} bytes)
    SHA-256  : {file.Sha256}
    인코딩   : {file.Encoding.ToLabel()} (내용으로 판별)
    소스     : {sourceCode}
    """);

if (dryRun)
{
    var adapter = ImportRunner.AdapterFor(sourceCode);
    var skipped = new List<string>();
    var keys = new HashSet<string>(StringComparer.Ordinal);
    int total = 0, dup = 0;
    string? firstHash = null;
    try
    {
        using var reader = file.OpenText();
        foreach (var row in adapter.Read(reader, (line, why) => skipped.Add($"{line}: {why}")))
        {
            total++;
            firstHash ??= row.ComputeHash();
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
        첫 행 해시     : {firstHash}
        """);
    foreach (var s in skipped.Take(10)) Console.WriteLine("  skip " + s);
    return 0;
}

var connStr = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres 가 필요합니다.");
var options = new DbContextOptionsBuilder<MashilDbContext>()
    .UseNpgsql(connStr, npg => npg.UseNetTopologySuite())
    .Options;

await using var db = new MashilDbContext(options);

var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
if (pending.Count > 0)
{
    Console.Error.WriteLine($"[중단] 적용되지 않은 마이그레이션 {pending.Count}개: {string.Join(", ", pending)}");
    Console.Error.WriteLine("        dotnet ef database update --project src/Mashil.Infrastructure --startup-project src/Mashil.Api");
    return 3;
}

var runner = new ImportRunner(db);
var progress = new Progress<ImportRun>(r => Console.Write($"\r  처리 {r.TotalRows:N0} 행 (신규 {r.InsertedRows:N0} / 변경 {r.UpdatedRows:N0} / 동일 {r.UnchangedRows:N0})"));
var run = await runner.RunAsync(sourceCode, file, ImportTrigger.Manual, force, progress);
Console.WriteLine();

Console.WriteLine($"""
    --- 임포트 결과 (run #{run.Id}) ---
    상태      : {run.Status}{(run.Message is null ? "" : " — " + run.Message)}
    전체 행   : {run.TotalRows:N0}
    신규      : {run.InsertedRows:N0}
    변경      : {run.UpdatedRows:N0}
    변경 없음 : {run.UnchangedRows:N0}
    건너뜀    : {run.SkippedRows:N0}
    소요      : {(run.FinishedAt - run.StartedAt)?.TotalSeconds:N1}s
    """);

return run.Status is ImportRunStatus.Succeeded or ImportRunStatus.Skipped ? 0 : 1;
