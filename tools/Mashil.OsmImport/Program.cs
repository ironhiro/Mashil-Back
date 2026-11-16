// 도보 도로망 임포트: OpenStreetMap PBF → walk_edges (도보 경로, POST /api/routes/walk)
//
//   dotnet run --project tools/Mashil.OsmImport -- --pbf data/osm/south-korea-latest.osm.pbf [--bbox 126.76,37.41,127.19,37.72] [--dry-run]
//
// 1) 길(way)을 읽어 걸을 수 있는 것만 남기고, 노드가 몇 개의 길에 쓰이는지 센다 (2개 이상이면 교차점)
// 2) 노드를 읽어 필요한 노드의 좌표만 정렬 배열에 담는다
// 3) 길을 교차점마다 끊어 간선을 만들고 (--bbox 가 있으면 그 안만), 한 트랜잭션에서 기존 행을 지우고 COPY 로 넣는다.
//    읽는 쪽(API)은 커밋 전까지 예전 도로망을 그대로 본다.
//
// 보안: 같은 폴더에 .md5 가 있으면 대조하고 다르면 멈춘다 (받은 파일이 깨졌거나 바뀐 경우). 네트워크는 쓰지 않는다.
// 출처: © OpenStreetMap contributors (ODbL 1.0). 경로를 보여 주는 화면에 표시한다.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Mashil.OsmImport;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using OsmSharp;
using OsmSharp.Streams;

var argsMap = ParseArgs(args);
if (!argsMap.TryGetValue("pbf", out var pbfPath))
{
    Console.Error.WriteLine("사용법: --pbf <파일.osm.pbf> [--bbox minLng,minLat,maxLng,maxLat] [--dry-run]");
    return 2;
}
pbfPath = Path.GetFullPath(pbfPath);
if (!File.Exists(pbfPath) || !pbfPath.EndsWith(".osm.pbf", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"PBF 파일이 아닙니다: {pbfPath}");
    return 2;
}
var bbox = argsMap.TryGetValue("bbox", out var bboxText) ? ParseBbox(bboxText) : null;
var dryRun = argsMap.ContainsKey("dry-run");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();
var connStr = config.GetConnectionString("Postgres");
if (!dryRun && string.IsNullOrWhiteSpace(connStr))
{
    Console.Error.WriteLine("ConnectionStrings:Postgres 가 필요합니다 (환경 변수 ConnectionStrings__Postgres).");
    return 2;
}

var total = Stopwatch.StartNew();

// ── 0. 무결성 ──────────────────────────────────────────────────────────────
var md5Path = pbfPath + ".md5";
if (File.Exists(md5Path))
{
    var expected = File.ReadAllText(md5Path).Split(' ', '\t', '\n')[0].Trim().ToLowerInvariant();
    await using var f = File.OpenRead(pbfPath);
    var actual = Convert.ToHexStringLower(await MD5.HashDataAsync(f));
    if (actual != expected)
    {
        Console.Error.WriteLine($"MD5 가 다릅니다 (기대 {expected}, 실제 {actual}). 파일을 다시 받으세요.");
        return 3;
    }
    Console.WriteLine($"MD5 확인 {actual}");
}
else
{
    Console.WriteLine("경고: .md5 파일이 없어 무결성을 확인하지 않았습니다.");
}

// ── 1. 길 ──────────────────────────────────────────────────────────────────
var ways = new List<(long[] Nodes, WayClass Class)>();
var refs = new List<long>();
await using (var stream = File.OpenRead(pbfPath))
{
    foreach (var geo in new PBFOsmStreamSource(stream))
    {
        if (geo is not Way { Nodes.Length: >= 2 } way) continue;
        var cls = WalkableWays.Classify(way.Tags);
        if (!cls.Walkable) continue;
        ways.Add((way.Nodes, cls));
        refs.AddRange(way.Nodes);
    }
}
Console.WriteLine($"1) 걸을 수 있는 길 {ways.Count:N0}개, 노드 참조 {refs.Count:N0}개 ({total.Elapsed:mm\\:ss})");

// 정렬해서 고유 id 와 쓰인 횟수 (2번 이상 = 교차점)
refs.Sort();
var ids = new List<long>(refs.Count / 2);
var junction = new List<bool>(refs.Count / 2);
for (var i = 0; i < refs.Count;)
{
    var j = i;
    while (j < refs.Count && refs[j] == refs[i]) j++;
    ids.Add(refs[i]);
    junction.Add(j - i >= 2);
    i = j;
}
refs = null!;
var idArr = ids.ToArray();
var isJunction = junction.ToArray();
ids = null!;
junction = null!;
GC.Collect();

// ── 2. 노드 좌표 ───────────────────────────────────────────────────────────
var lat = new double[idArr.Length];
var lng = new double[idArr.Length];
var found = new bool[idArr.Length];
var foundCount = 0;
await using (var stream = File.OpenRead(pbfPath))
{
    foreach (var geo in new PBFOsmStreamSource(stream))
    {
        if (geo is Way or Relation) break; // PBF 는 노드 → 길 → 관계 순서
        if (geo is not Node { Id: { } id, Latitude: { } la, Longitude: { } lo }) continue;
        var k = Array.BinarySearch(idArr, id);
        if (k < 0) continue;
        lat[k] = la;
        lng[k] = lo;
        found[k] = true;
        foundCount++;
    }
}
Console.WriteLine($"2) 노드 좌표 {foundCount:N0}/{idArr.Length:N0} ({total.Elapsed:mm\\:ss})");

// ── 3. 간선 ────────────────────────────────────────────────────────────────
var factory = new GeometryFactory(new PrecisionModel(), 4326);
var edges = new List<(long Source, long Target, LineString Geom, double LengthM, float Factor, bool Steps, string Highway)>();
long skippedMissing = 0, skippedOutside = 0;
foreach (var (nodes, cls) in ways)
{
    var start = 0;
    for (var k = 1; k < nodes.Length; k++)
    {
        var last = k == nodes.Length - 1;
        if (!last && !isJunction[Array.BinarySearch(idArr, nodes[k])]) continue;
        AddEdge(nodes, start, k, cls);
        start = k;
    }
}
ways = null!;
Console.WriteLine($"3) 간선 {edges.Count:N0}개 (좌표 없는 노드로 건너뜀 {skippedMissing:N0}, 범위 밖 {skippedOutside:N0}) ({total.Elapsed:mm\\:ss})");

if (dryRun)
{
    Console.WriteLine("--dry-run: DB 에 쓰지 않았습니다.");
    return 0;
}

// ── 4. DB ──────────────────────────────────────────────────────────────────
var dsb = new NpgsqlDataSourceBuilder(connStr);
dsb.UseNetTopologySuite();
await using var dataSource = dsb.Build();
await using (var conn = await dataSource.OpenConnectionAsync())
{
    await using var tx = await conn.BeginTransactionAsync();
    // TRUNCATE 는 읽기까지 막으므로 DELETE (MVCC: API 는 커밋 전까지 예전 행을 본다)
    await using (var del = new NpgsqlCommand("DELETE FROM walk_edges", conn, tx)) await del.ExecuteNonQueryAsync();
    await using (var writer = await conn.BeginBinaryImportAsync(
        "COPY walk_edges (\"Source\", \"Target\", \"Geom\", \"LengthM\", \"CostFactor\", \"Steps\", \"Highway\") FROM STDIN (FORMAT BINARY)"))
    {
        foreach (var e in edges)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(e.Source, NpgsqlDbType.Bigint);
            await writer.WriteAsync(e.Target, NpgsqlDbType.Bigint);
            await writer.WriteAsync(e.Geom, NpgsqlDbType.Geometry);
            await writer.WriteAsync(e.LengthM, NpgsqlDbType.Double);
            await writer.WriteAsync(e.Factor, NpgsqlDbType.Real);
            await writer.WriteAsync(e.Steps, NpgsqlDbType.Boolean);
            await writer.WriteAsync(e.Highway.Length > 32 ? e.Highway[..32] : e.Highway, NpgsqlDbType.Varchar);
        }
        await writer.CompleteAsync();
    }
    await tx.CommitAsync();
    await using (var analyze = new NpgsqlCommand("ANALYZE walk_edges", conn)) await analyze.ExecuteNonQueryAsync();
}
Console.WriteLine($"4) walk_edges 에 {edges.Count:N0}행 반영 ({total.Elapsed:mm\\:ss})");
return 0;

// ── 도우미 ─────────────────────────────────────────────────────────────────

void AddEdge(long[] nodes, int from, int to, WayClass cls)
{
    var coords = new Coordinate[to - from + 1];
    var inside = bbox is null;
    for (var k = from; k <= to; k++)
    {
        var idx = Array.BinarySearch(idArr, nodes[k]);
        if (idx < 0 || !found[idx])
        {
            skippedMissing++;
            return;
        }
        coords[k - from] = new Coordinate(lng[idx], lat[idx]);
        if (!inside && bbox is { } b && lng[idx] >= b.MinLng && lng[idx] <= b.MaxLng && lat[idx] >= b.MinLat && lat[idx] <= b.MaxLat)
            inside = true;
    }
    if (!inside)
    {
        skippedOutside++;
        return;
    }
    double length = 0;
    for (var k = 1; k < coords.Length; k++) length += Haversine(coords[k - 1], coords[k]);
    if (length <= 0) return;
    edges.Add((nodes[from], nodes[to], factory.CreateLineString(coords), Math.Round(length, 2), cls.CostFactor, cls.Steps, cls.Highway));
}

static double Haversine(Coordinate a, Coordinate b)
{
    const double r = 6_371_000;
    static double Rad(double d) => d * Math.PI / 180;
    var dLat = Rad(b.Y - a.Y);
    var dLng = Rad(b.X - a.X);
    var x = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(a.Y)) * Math.Cos(Rad(b.Y)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
    return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(x)));
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var map = new Dictionary<string, string>();
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        map[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "";
    }
    return map;
}

static (double MinLng, double MinLat, double MaxLng, double MaxLat)? ParseBbox(string text)
{
    var p = text.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
    if (p.Length != 4 || p[0] >= p[2] || p[1] >= p[3]) throw new ArgumentException("--bbox 는 minLng,minLat,maxLng,maxLat");
    return (p[0], p[1], p[2], p[3]);
}
