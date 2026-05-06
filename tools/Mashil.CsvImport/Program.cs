using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Mashil.CsvImport;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var csvPath = config["CsvPath"]
    ?? args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? throw new ArgumentException("CSV path required (positional arg or --CsvPath=...).");

var connStr = config.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=mashil;Username=mashil;Password=mashil_dev";

var dbOptions = new DbContextOptionsBuilder<MashilDbContext>()
    .UseNpgsql(connStr, npg => npg.UseNetTopologySuite())
    .Options;

await using var db = new MashilDbContext(dbOptions);
await db.Database.EnsureCreatedAsync();

await using var stream = File.OpenRead(csvPath);
// 한국 공공데이터 CSV는 EUC-KR 표기여도 실제로는 CP949(MS949 / KS_C_5601-1987) 슈퍼셋이 흔함.
using var reader = new StreamReader(stream, Encoding.GetEncoding(949));
using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
{
    HasHeaderRecord = true,
    BadDataFound = null,
    MissingFieldFound = null,
    ReadingExceptionOccurred = _ => false, // 손상 행 발생 시 throw 대신 false 반환 → 수동 스킵
});

csv.Context.RegisterClassMap<ToiletCsvRowMap>();

var stats = new ImportStats();
var batch = new List<Toilet>(capacity: 500);

await csv.ReadAsync();
csv.ReadHeader();

while (await csv.ReadAsync())
{
    ToiletCsvRow row;
    try
    {
        row = csv.GetRecord<ToiletCsvRow>();
    }
    catch (Exception ex) when (ex is CsvHelper.TypeConversion.TypeConverterException
                                  or CsvHelper.MissingFieldException
                                  or CsvHelper.BadDataException
                                  or FormatException)
    {
        stats.Skipped++;
        Console.Error.WriteLine($"[skip] row {csv.Parser.Row}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
        continue;
    }

    var toilet = MapRow(row, stats);
    batch.Add(toilet);

    if (batch.Count >= 500)
    {
        await UpsertBatchAsync(db, batch);
        batch.Clear();
    }
}
if (batch.Count > 0)
{
    await UpsertBatchAsync(db, batch);
}

Console.WriteLine($"""
    --- 임포트 완료 ---
    총 행 수            : {stats.Total}
    좌표 + 주소 모두 존재 : {stats.WithCoordsAndAddress}
    좌표만 존재          : {stats.CoordOnly}        (주소 reverse-geocode 큐 대상)
    주소만 존재          : {stats.AddressOnly}      (좌표 geocode 큐 대상)
    둘 다 없음           : {stats.Neither}          (검색 전용)
    스킵 (손상 행)       : {stats.Skipped}
    """);

return;

static Toilet MapRow(ToiletCsvRow r, ImportStats stats)
{
    stats.Total++;

    var hasCoord = r.Latitude.HasValue && r.Longitude.HasValue;
    var hasAddress = !string.IsNullOrWhiteSpace(r.RoadAddress) || !string.IsNullOrWhiteSpace(r.LotAddress);

    if (hasCoord && hasAddress) stats.WithCoordsAndAddress++;
    else if (hasCoord) stats.CoordOnly++;
    else if (hasAddress) stats.AddressOnly++;
    else stats.Neither++;

    Point? location = hasCoord ? new Point(r.Longitude!.Value, r.Latitude!.Value) { SRID = 4326 } : null;

    var addressSource = !string.IsNullOrWhiteSpace(r.RoadAddress) ? AddressSource.CsvRoad
        : !string.IsNullOrWhiteSpace(r.LotAddress) ? AddressSource.CsvLot
        : AddressSource.Unknown;

    return new Toilet
    {
        ManagementNo = r.ManagementNo,
        LocalGovCode = r.LocalGovCode,
        Kind = r.Kind ?? string.Empty,
        Name = r.Name ?? string.Empty,
        RoadAddress = NullIfBlank(r.RoadAddress),
        LotAddress = NullIfBlank(r.LotAddress),
        AddressSource = addressSource,
        Latitude = r.Latitude,
        Longitude = r.Longitude,
        Location = location,
        CoordinateSource = hasCoord ? CoordinateSource.Csv : CoordinateSource.Unknown,
        MaleStallCount = r.MaleStallCount ?? 0,
        MaleUrinalCount = r.MaleUrinalCount ?? 0,
        MaleAccessibleStallCount = r.MaleAccessibleStallCount ?? 0,
        MaleAccessibleUrinalCount = r.MaleAccessibleUrinalCount ?? 0,
        MaleChildStallCount = r.MaleChildStallCount ?? 0,
        MaleChildUrinalCount = r.MaleChildUrinalCount ?? 0,
        FemaleStallCount = r.FemaleStallCount ?? 0,
        FemaleAccessibleStallCount = r.FemaleAccessibleStallCount ?? 0,
        FemaleChildStallCount = r.FemaleChildStallCount ?? 0,
        ManagementOrg = NullIfBlank(r.ManagementOrg),
        Phone = NullIfBlank(r.Phone),
        OpenHours = NullIfBlank(r.OpenHours),
        OpenHoursDetail = NullIfBlank(r.OpenHoursDetail),
        InstalledYearMonth = NullIfBlank(r.InstalledYearMonth),
        RemodeledYearMonth = NullIfBlank(r.RemodeledYearMonth),
        OwnershipKind = NullIfBlank(r.OwnershipKind),
        WasteType = NullIfBlank(r.WasteType),
        SafetyFacilityRequired = ParseYn(r.SafetyFacilityRequired),
        EmergencyBellInstalled = ParseYn(r.EmergencyBellInstalled),
        EmergencyBellLocation = NullIfBlank(r.EmergencyBellLocation),
        EntranceCctv = ParseYn(r.EntranceCctv),
        DiaperChangerAvailable = ParseYn(r.DiaperChangerAvailable),
        DiaperChangerLocation = NullIfBlank(r.DiaperChangerLocation),
        DataReferenceDate = ParseDateOnly(r.DataReferenceDate),
        DataUpdateKind = NullIfBlank(r.DataUpdateKind),
        DataUpdatedAt = ParseDateTime(r.DataUpdatedAt),
        LastModifiedAt = ParseDateTime(r.LastModifiedAt),
    };
}

static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

static bool? ParseYn(string? s) => s switch
{
    "Y" or "y" => true,
    "N" or "n" => false,
    _ => null,
};

static DateOnly? ParseDateOnly(string? s) =>
    DateOnly.TryParse(s, out var d) ? d : null;

static DateTime? ParseDateTime(string? s) =>
    DateTime.TryParse(s, out var dt) ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;

static async Task UpsertBatchAsync(MashilDbContext db, List<Toilet> batch)
{
    var keys = batch.Select(t => t.ManagementNo).ToList();
    var existing = await db.Toilets
        .Where(t => keys.Contains(t.ManagementNo))
        .ToDictionaryAsync(t => t.ManagementNo);

    foreach (var incoming in batch)
    {
        if (existing.TryGetValue(incoming.ManagementNo, out var current))
        {
            // SetValues 가 incoming 의 Guid.Empty Id / 새 CreatedAt 으로 덮어쓰지 않도록 미리 align
            incoming.Id = current.Id;
            incoming.CreatedAt = current.CreatedAt;
            db.Entry(current).CurrentValues.SetValues(incoming);
            current.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.Toilets.Add(incoming);
        }
    }
    await db.SaveChangesAsync();
}

internal sealed class ImportStats
{
    public int Total;
    public int WithCoordsAndAddress;
    public int CoordOnly;
    public int AddressOnly;
    public int Neither;
    public int Skipped;
}
