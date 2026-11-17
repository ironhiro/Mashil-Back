namespace Mashil.OsmImport;

/// <summary>간선 끝점 하나 (노드 id 와 경위도).</summary>
public readonly record struct StitchNode(long Id, double Lng, double Lat);

/// <summary>두 망을 잇는 짧은 간선 하나.</summary>
public readonly record struct StitchLink(StitchNode Osm, StitchNode Seoul, double LengthM);

/// <summary>
/// 서울 도보 네트워크와 OSM 망 잇기. 두 망은 노드 id 가 따로라(서울 id 에 오프셋) 그냥 두면 경계에서 끊겨,
/// 서울 경계를 넘거나 서울 링크가 없는 칸(공원·단지 안 길)을 지나는 경로가 no_route 가 된다.
/// 서울 링크가 있는 칸 가까이의 OSM 끝점마다, <see cref="MaxGapM"/> 안의 가장 가까운 서울 노드로 짧은 간선을 하나 넣는다.
/// <para>
/// 거리를 짧게 두는 까닭: 길게 잡으면 차도 건너편 보도로 건너뛰는 간선이 생긴다. OSM 보행로가 서울 보도에 닿는 자리는 보통 몇 m 안이다.
/// </para>
/// </summary>
public static class NetworkStitcher
{
    public const double MaxGapM = 12;

    // 이웃 칸만 보면 되게 칸을 MaxGapM 보다 크게 (0.0002° ≈ 위아래 22m × 옆 18m)
    private const double CellDeg = 0.0002;

    /// <param name="osm">이을 후보 OSM 끝점 (서울 칸 가까이)</param>
    /// <param name="seoul">서울 노드</param>
    public static List<StitchLink> Stitch(IEnumerable<StitchNode> osm, IEnumerable<StitchNode> seoul, double maxGapM = MaxGapM)
    {
        var grid = new Dictionary<(int, int), List<StitchNode>>();
        foreach (var s in seoul)
        {
            var key = Key(s.Lng, s.Lat);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(s);
        }

        var links = new List<StitchLink>();
        var seen = new HashSet<long>();
        foreach (var o in osm)
        {
            if (!seen.Add(o.Id)) continue; // OSM 노드 하나에 간선 하나
            var (cx, cy) = Key(o.Lng, o.Lat);
            StitchNode? best = null;
            var bestM = maxGapM;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                    foreach (var s in list)
                    {
                        var m = Meters(o.Lng, o.Lat, s.Lng, s.Lat);
                        if (m <= bestM) { bestM = m; best = s; }
                    }
                }
            if (best is { } b) links.Add(new StitchLink(o, b, Math.Max(0.5, Math.Round(bestM, 2))));
        }
        return links;
    }

    private static (int, int) Key(double lng, double lat) => ((int)Math.Floor(lng / CellDeg), (int)Math.Floor(lat / CellDeg));

    public static double Meters(double lng1, double lat1, double lng2, double lat2)
    {
        const double r = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var x = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(x)));
    }
}
