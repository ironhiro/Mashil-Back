// 도보 도로망 임포트: OpenStreetMap PBF → walk_edges (도보 경로, POST /api/routes/walk)
//
//   dotnet run --project tools/Mashil.OsmImport -- --pbf data/osm/south-korea-latest.osm.pbf [--seoul] [--bbox 126.76,37.41,127.19,37.72] [--dry-run] [--force]
//   dotnet run --project tools/Mashil.OsmImport -- --download [https://…/south-korea-latest.osm.pbf]   (Azure Job: 받아서 바로)
//
// --download: PBF 와 .md5 를 임시 폴더에 받아 쓴다 (기본 Geofabrik 한국, https 만). 끝나면 지운다.
// --force:    새 간선 수가 지금의 80% 아래여도 바꾼다. 기본은 멈춘다 (받은 파일이 잘렸거나 분류가 깨진 경우).
//
// --seoul: 서울 안은 서울시 도보 네트워크(Open API, 횡단보도·육교·지하철 출입구가 이어진 보행자용 노드·링크)로 바꾼다.
//          서울 링크가 있는 약 200m 격자 칸의 OSM 간선은 빼고 서울 링크를 넣는다 (두 망을 섞지 않는다).
//          두 망은 노드 id 가 따로라, 서울 칸 가까이의 OSM 끝점을 12m 안의 서울 노드와 짧은 간선(stitch)으로 잇는다 (NetworkStitcher).
//          인증키는 사용자 비밀 SeoulOpenData:ApiKey (Mashil.Api 와 공유) 또는 환경 변수 SeoulOpenData__ApiKey.
//
// 1) 길(way)을 읽어 걸을 수 있는 것만 남기고, 노드가 몇 개의 길에 쓰이는지 센다 (2개 이상이면 교차점)
// 2) 노드를 읽어 필요한 노드의 좌표만 정렬 배열에 담는다
// 3) 길을 교차점마다 끊어 간선을 만든다 (--bbox 가 있으면 그 안만).
// 4) 새 테이블 walk_edges_new 에 인덱스 없이 COPY → PK·GIST 인덱스 → ANALYZE → 건수 확인(WalkSwap) → 짧은 트랜잭션에서
//    예전 테이블을 지우고 이름을 바꾼다. 읽는 쪽(API)은 바꾸는 순간까지 예전 도로망을 그대로 본다.
//    한 테이블에 DELETE + COPY 를 하면 GIST 를 행마다 고치고 죽은 행이 테이블 크기만큼 쌓여 같은 서버의 운영 DB 까지 느려진다.
//
// 보안: .md5 가 있으면 대조하고 다르면 멈춘다 (받은 파일이 깨졌거나 바뀐 경우). --download 가 없으면 네트워크를 쓰지 않는다.
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
var bbox = argsMap.TryGetValue("bbox", out var bboxText) ? ParseBbox(bboxText) : null;
var dryRun = argsMap.ContainsKey("dry-run");
var withSeoul = argsMap.ContainsKey("seoul");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets("mashil-api-3f7e82a4-e2c8-4f6e-9d1a-1b9c54a6f8a2") // Mashil.Api 와 공유 (SeoulOpenData:ApiKey)
    .AddEnvironmentVariables()
    .Build();
var connStr = config.GetConnectionString("Postgres");
if (!dryRun)
{
    if (string.IsNullOrWhiteSpace(connStr))
    {
        Console.Error.WriteLine("ConnectionStrings:Postgres 가 필요합니다 (환경 변수 ConnectionStrings__Postgres).");
        return 2;
    }
    // 받고 읽는 데 1분 넘게 쓰기 전에 DB 부터 확인한다 (연결, walk_edges 있음 = 마이그레이션 끝남)
    try
    {
        await using var probe = new NpgsqlConnection(connStr);
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass('walk_edges') IS NOT NULL", probe);
        if (await cmd.ExecuteScalarAsync() is not true)
        {
            Console.Error.WriteLine("walk_edges 테이블이 없습니다. 마이그레이션을 먼저 돌리세요.");
            return 2;
        }
    }
    catch (NpgsqlException e)
    {
        Console.Error.WriteLine($"DB 에 연결하지 못했습니다: {e.Message}");
        return 2;
    }
}

string pbfPath;
if (argsMap.TryGetValue("download", out var downloadUrl))
{
    var dir = Directory.CreateTempSubdirectory("mashil-osm-").FullName;
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } };
    try
    {
        var url = new Uri(string.IsNullOrWhiteSpace(downloadUrl) ? PbfDownload.DefaultUrl : downloadUrl);
        pbfPath = await PbfDownload.FetchAsync(url, dir, Console.WriteLine);
    }
    catch (Exception e) when (e is HttpRequestException or ArgumentException or UriFormatException or IOException or TaskCanceledException)
    {
        Console.Error.WriteLine($"받지 못했습니다: {e.Message}");
        return 4;
    }
}
else if (argsMap.TryGetValue("pbf", out var given))
{
    pbfPath = Path.GetFullPath(given);
}
else
{
    Console.Error.WriteLine("사용법: --pbf <파일.osm.pbf> | --download [url]  [--seoul] [--bbox minLng,minLat,maxLng,maxLat] [--dry-run] [--force]");
    return 2;
}
if (!File.Exists(pbfPath) || !pbfPath.EndsWith(".osm.pbf", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"PBF 파일이 아닙니다: {pbfPath}");
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

// ── 3b. 서울 ───────────────────────────────────────────────────────────────
if (withSeoul)
{
    var key = config["SeoulOpenData:ApiKey"] ?? "";
    var seoul = await SeoulWalkNetwork.FetchAsync(key, msg => Console.WriteLine(msg));
    // 서울 링크가 지나는 격자 칸 (경위도 0.002° ≈ 위아래 220m × 옆 180m)
    var covered = new HashSet<(int, int)>();
    foreach (var l in seoul)
        foreach (var c in l.Geom.Coordinates)
            covered.Add(Cell(c.X, c.Y));
    var before = edges.Count;
    edges.RemoveAll(e =>
    {
        var mid = e.Geom.Coordinates[e.Geom.NumPoints / 2];
        return covered.Contains(Cell(mid.X, mid.Y));
    });
    var removed = before - edges.Count;
    // 서울 노드 id 는 OSM id 와 겹치지 않게 큰 수를 더한다 (OSM 노드 id 는 아직 10^11 아래)
    const long seoulOffset = 9_000_000_000_000_000;
    var added = 0;
    var osmEdgeCount = edges.Count; // 여기까지가 OSM 간선
    foreach (var l in seoul)
    {
        if (bbox is { } b && !l.Geom.Coordinates.Any(c => c.X >= b.MinLng && c.X <= b.MaxLng && c.Y >= b.MinLat && c.Y <= b.MaxLat)) continue;
        double length = 0;
        var cs = l.Geom.Coordinates;
        for (var k = 1; k < cs.Length; k++) length += Haversine(cs[k - 1], cs[k]);
        if (length <= 0) continue;
        var kind = l.Crosswalk ? "seoul:crosswalk" : l.Overpass ? "seoul:overpass" : l.Tunnel ? "seoul:tunnel" : l.Subway ? "seoul:subway" : "seoul";
        edges.Add((seoulOffset + l.From, seoulOffset + l.To, l.Geom, Math.Round(length, 2), 1.0f, false, kind));
        added++;
    }
    Console.WriteLine($"3b) 서울: OSM 간선 {removed:N0}개를 빼고 서울 링크 {added:N0}개 (횡단보도 {seoul.Count(l => l.Crosswalk):N0}) ({total.Elapsed:mm\\:ss})");

    // 3c) 두 망 잇기: 서울 칸과 그 이웃 칸에 있는 OSM 끝점 → 가까운 서울 노드 (NetworkStitcher)
    bool NearSeoul(Coordinate c)
    {
        var (x, y) = Cell(c.X, c.Y);
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (covered.Contains((x + dx, y + dy))) return true;
        return false;
    }
    var osmEnds = new List<StitchNode>();
    for (var k = 0; k < osmEdgeCount; k++)
    {
        var e = edges[k];
        var a = e.Geom.Coordinates[0];
        var z = e.Geom.Coordinates[^1];
        if (NearSeoul(a)) osmEnds.Add(new StitchNode(e.Source, a.X, a.Y));
        if (NearSeoul(z)) osmEnds.Add(new StitchNode(e.Target, z.X, z.Y));
    }
    var seoulEnds = new List<StitchNode>();
    for (var k = osmEdgeCount; k < edges.Count; k++)
    {
        var e = edges[k];
        seoulEnds.Add(new StitchNode(e.Source, e.Geom.Coordinates[0].X, e.Geom.Coordinates[0].Y));
        seoulEnds.Add(new StitchNode(e.Target, e.Geom.Coordinates[^1].X, e.Geom.Coordinates[^1].Y));
    }
    var stitches = NetworkStitcher.Stitch(osmEnds, seoulEnds);
    foreach (var s in stitches)
    {
        var line = factory.CreateLineString([new Coordinate(s.Osm.Lng, s.Osm.Lat), new Coordinate(s.Seoul.Lng, s.Seoul.Lat)]);
        edges.Add((s.Osm.Id, s.Seoul.Id, line, s.LengthM, 1.0f, false, "stitch"));
    }
    Console.WriteLine($"3c) 서울·OSM 연결 {stitches.Count:N0}개 (후보 OSM 끝점 {osmEnds.Select(n => n.Id).Distinct().Count():N0}) ({total.Elapsed:mm\\:ss})");
}

static (int, int) Cell(double lng, double lat) => ((int)Math.Floor(lng / 0.002), (int)Math.Floor(lat / 0.002));

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
    // 4a) 새 테이블. 인덱스 없이 넣고 나중에 한 번에 만든다. 지난번 실패로 남은 것은 지운다
    await Exec(conn, """
        DROP TABLE IF EXISTS walk_edges_new;
        CREATE TABLE walk_edges_new (LIKE walk_edges INCLUDING DEFAULTS INCLUDING IDENTITY);
        """);
    await using (var writer = await conn.BeginBinaryImportAsync(
        "COPY walk_edges_new (\"Source\", \"Target\", \"Geom\", \"LengthM\", \"CostFactor\", \"Steps\", \"Highway\") FROM STDIN (FORMAT BINARY)"))
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
    Console.WriteLine($"4a) walk_edges_new 에 {edges.Count:N0}행 ({total.Elapsed:mm\\:ss})");

    // 4b) 인덱스. B1ms(메모리 2GB)라 maintenance_work_mem 은 이 세션에서만 조금 올린다
    await Exec(conn, """
        SET maintenance_work_mem = '256MB';
        ALTER TABLE walk_edges_new ADD CONSTRAINT "PK_walk_edges_new" PRIMARY KEY ("Id");
        CREATE INDEX "IX_walk_edges_new_Geom" ON walk_edges_new USING gist ("Geom");
        ANALYZE walk_edges_new;
        """);
    Console.WriteLine($"4b) 인덱스 ({total.Elapsed:mm\\:ss})");

    // 4c) 지금보다 크게 줄었으면 바꾸지 않는다
    var current = await Scalar<long>(conn, "SELECT count(*) FROM walk_edges");
    if (WalkSwap.Check(current, edges.Count, argsMap.ContainsKey("force")) is { } refusal)
    {
        await Exec(conn, "DROP TABLE walk_edges_new");
        Console.Error.WriteLine($"바꾸지 않았습니다: {refusal}");
        return 5;
    }

    // 4d) 바꾸기. 경로 요청이 읽는 중이면 끝날 때까지 기다리되 30초를 넘기면 그만둔다 (예전 도로망은 그대로)
    await using (var tx = await conn.BeginTransactionAsync())
    {
        await Exec(conn, """
            SET LOCAL lock_timeout = '30s';
            DROP TABLE walk_edges;
            ALTER TABLE walk_edges_new RENAME TO walk_edges;
            ALTER TABLE walk_edges RENAME CONSTRAINT "PK_walk_edges_new" TO "PK_walk_edges";
            ALTER INDEX "IX_walk_edges_new_Geom" RENAME TO "IX_walk_edges_Geom";
            """, tx);
        await tx.CommitAsync();
    }
    Console.WriteLine($"4) walk_edges 교체 {current:N0} → {edges.Count:N0}행 ({total.Elapsed:mm\\:ss})");
}
return 0;

static async Task Exec(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null)
{
    await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 1800 };
    await cmd.ExecuteNonQueryAsync();
}

static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
    return (T)(await cmd.ExecuteScalarAsync())!;
}

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
