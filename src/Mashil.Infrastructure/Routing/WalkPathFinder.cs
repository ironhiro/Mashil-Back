namespace Mashil.Infrastructure.Routing;

/// <summary>위도·경도 한 점.</summary>
public readonly record struct GeoPoint(double Lat, double Lng);

/// <summary>탐색에 쓰는 간선 하나 (DB 의 walk_edges 한 행). 좌표는 Source → Target 순서.</summary>
public sealed record RouteEdge(long Id, long Source, long Target, double LengthM, float CostFactor, bool Steps, GeoPoint[] Points);

/// <summary>찾은 길: 선(스냅한 출발점 → 스냅한 도착점), 실제 길이, 걷는 시간 어림.</summary>
public sealed record WalkPath(IReadOnlyList<GeoPoint> Points, double DistanceM, int Minutes, int StepsEdges);

public enum RouteFailure
{
    /// <summary>출발점이나 도착점 근처(스냅 거리 안)에 걸을 수 있는 길이 없다.</summary>
    FarFromRoad,

    /// <summary>불러온 범위 안에서 이어지는 길이 없다.</summary>
    NoRoute,

    /// <summary>탐색 상한(노드 수)을 넘었다.</summary>
    TooComplex,
}

public sealed class RouteException(RouteFailure failure) : Exception(failure.ToString())
{
    public RouteFailure Failure { get; } = failure;
}

/// <summary>
/// 도보 경로 탐색 (DB 없음, 단위 테스트 대상). 출발·도착을 가장 가까운 간선 위 점으로 스냅해 가상 노드로 끼워 넣고,
/// A* 로 비용(길이 × 가중치)이 가장 작은 길을 찾는다. 휴리스틱은 직선 거리 (가중치 ≥ 1 이라 허용 가능).
/// </summary>
public static class WalkPathFinder
{
    /// <summary>걷는 속도 (m/분). 웹 방향 안내와 같은 4 km/h</summary>
    public const double WalkMetersPerMinute = 4_000.0 / 60;

    /// <summary>계단은 같은 길이라도 이만큼 더 걸린다고 본다</summary>
    public const double StepsTimeFactor = 1.6;

    private const long StartNode = -1;
    private const long EndNode = -2;

    /// <summary>
    /// <paramref name="snapMaxM"/>: 출발·도착에서 간선까지 이보다 멀면 실패.
    /// <paramref name="maxExpanded"/>: A* 가 꺼내 볼 노드 수 상한 (계산량 상한).
    /// <paramref name="snapCandidates"/>: 출발·도착마다 시도해 볼 가까운 간선 수 (가장 가까운 간선이 끊어진 조각일 때 대비).
    /// </summary>
    public static WalkPath Find(
        IReadOnlyList<RouteEdge> edges, GeoPoint from, GeoPoint to,
        double snapMaxM = 150, int maxExpanded = 400_000, int snapCandidates = 3)
    {
        if (edges.Count == 0) throw new RouteException(RouteFailure.FarFromRoad);
        var proj = new LocalProjection((from.Lat + to.Lat) / 2);
        var starts = Snap(edges, from, proj, snapMaxM, snapCandidates);
        var ends = Snap(edges, to, proj, snapMaxM, snapCandidates);
        if (starts.Count == 0 || ends.Count == 0) throw new RouteException(RouteFailure.FarFromRoad);

        var graph = BuildGraph(edges, proj);
        // 스냅 후보 쌍을 가까운 것부터 시도한다 (보통 첫 쌍에서 끝난다)
        var pairs = starts.SelectMany(s => ends.Select(e => (s, e))).OrderBy(p => p.s.DistM + p.e.DistM);
        var tooComplex = false;
        foreach (var (s, e) in pairs)
        {
            var result = Search(graph, edges, proj, s, e, maxExpanded, out var hitLimit);
            tooComplex |= hitLimit;
            if (result is not null) return result;
        }
        throw new RouteException(tooComplex ? RouteFailure.TooComplex : RouteFailure.NoRoute);
    }

    // ── 스냅 ───────────────────────────────────────────────────────────────

    /// <summary>점에서 간선 위 가장 가까운 곳. Pos 는 간선 시작(Source)에서 그 점까지 따라간 거리 (m).</summary>
    private sealed record SnapPoint(int EdgeIndex, double Pos, double DistM, GeoPoint At);

    private static List<SnapPoint> Snap(IReadOnlyList<RouteEdge> edges, GeoPoint p, LocalProjection proj, double maxM, int take)
    {
        var (px, py) = proj.ToXY(p);
        var best = new List<SnapPoint>();
        for (var i = 0; i < edges.Count; i++)
        {
            var pts = edges[i].Points;
            double along = 0, bestDist = double.MaxValue, bestPos = 0;
            GeoPoint bestAt = default;
            for (var k = 0; k + 1 < pts.Length; k++)
            {
                var (ax, ay) = proj.ToXY(pts[k]);
                var (bx, by) = proj.ToXY(pts[k + 1]);
                double dx = bx - ax, dy = by - ay;
                var segLen = Math.Sqrt(dx * dx + dy * dy);
                var t = segLen == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (segLen * segLen), 0, 1);
                double qx = ax + t * dx, qy = ay + t * dy;
                var d = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestPos = along + t * segLen;
                    bestAt = proj.ToGeo(qx, qy);
                }
                along += segLen;
            }
            if (bestDist > maxM) continue;
            // 저장된 길이와 투영 길이의 비율을 맞춘다 (긴 간선에서 투영 오차가 쌓이지 않게)
            var scale = along > 0 ? edges[i].LengthM / along : 1;
            best.Add(new SnapPoint(i, bestPos * scale, bestDist, bestAt));
        }
        return best.OrderBy(s => s.DistM).Take(take).ToList();
    }

    // ── 그래프 ─────────────────────────────────────────────────────────────

    private sealed class Graph
    {
        public readonly Dictionary<long, List<int>> Adjacent = new(); // 노드 → 간선 번호들
        public readonly Dictionary<long, (double X, double Y)> NodeXY = new();
    }

    private static Graph BuildGraph(IReadOnlyList<RouteEdge> edges, LocalProjection proj)
    {
        var g = new Graph();
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            Add(g, e.Source, i, proj.ToXY(e.Points[0]));
            Add(g, e.Target, i, proj.ToXY(e.Points[^1]));
        }
        return g;

        static void Add(Graph g, long node, int edge, (double, double) xy)
        {
            if (!g.Adjacent.TryGetValue(node, out var list)) g.Adjacent[node] = list = new List<int>(4);
            list.Add(edge);
            g.NodeXY.TryAdd(node, xy);
        }
    }

    // ── A* ─────────────────────────────────────────────────────────────────

    /// <summary>노드에 들어온 길: 이전 노드, 탄 간선, 간선 위 시작·끝 위치 (m, Source 기준).</summary>
    private readonly record struct Came(long From, int Edge, double PosFrom, double PosTo);

    private static WalkPath? Search(
        Graph g, IReadOnlyList<RouteEdge> edges, LocalProjection proj, SnapPoint s, SnapPoint e, int maxExpanded, out bool hitLimit)
    {
        hitLimit = false;
        var endXY = proj.ToXY(e.At);
        var cost = new Dictionary<long, double> { [StartNode] = 0 };
        var came = new Dictionary<long, Came>();
        var open = new PriorityQueue<long, double>();
        open.Enqueue(StartNode, 0);
        var closed = new HashSet<long>();

        // 출발과 도착이 같은 간선 위면 그대로 따라가는 길도 후보
        if (s.EdgeIndex == e.EdgeIndex)
        {
            var len = Math.Abs(e.Pos - s.Pos);
            cost[EndNode] = len * edges[s.EdgeIndex].CostFactor;
            came[EndNode] = new Came(StartNode, s.EdgeIndex, s.Pos, e.Pos);
            open.Enqueue(EndNode, cost[EndNode]);
        }

        var expanded = 0;
        while (open.TryDequeue(out var node, out _))
        {
            if (node == EndNode) return Build(edges, came, s, e);
            if (!closed.Add(node)) continue;
            if (++expanded > maxExpanded)
            {
                hitLimit = true;
                return null;
            }

            foreach (var (next, edge, posFrom, posTo) in Neighbours(g, edges, node, s))
            {
                var stepCost = Math.Abs(posTo - posFrom) * edges[edge].CostFactor;
                Relax(next, edge, posFrom, posTo, stepCost);
                // 이 간선이 도착 간선이면 도착점으로 바로 갈 수 있다
                if (edge == e.EdgeIndex && next != StartNode)
                {
                    var toEnd = Math.Abs(e.Pos - posFrom) * edges[edge].CostFactor;
                    Relax(EndNode, edge, posFrom, e.Pos, toEnd);
                }
            }

            void Relax(long next, int edge, double posFrom, double posTo, double stepCost)
            {
                if (closed.Contains(next)) return;
                var c = cost[node] + stepCost;
                if (cost.TryGetValue(next, out var old) && old <= c) return;
                cost[next] = c;
                came[next] = new Came(node, edge, posFrom, posTo);
                var xy = next == EndNode ? endXY : g.NodeXY[next];
                open.Enqueue(next, c + Dist(xy, endXY));
            }
        }
        return null;
    }

    /// <summary>노드에서 갈 수 있는 곳들. 출발 가상 노드는 스냅한 간선의 양 끝으로만 간다.</summary>
    private static IEnumerable<(long Next, int Edge, double PosFrom, double PosTo)> Neighbours(
        Graph g, IReadOnlyList<RouteEdge> edges, long node, SnapPoint s)
    {
        if (node == StartNode)
        {
            var se = edges[s.EdgeIndex];
            yield return (se.Source, s.EdgeIndex, s.Pos, 0);
            yield return (se.Target, s.EdgeIndex, s.Pos, se.LengthM);
            yield break;
        }
        foreach (var i in g.Adjacent[node])
        {
            var edge = edges[i];
            if (edge.Source == node) yield return (edge.Target, i, 0, edge.LengthM);
            if (edge.Target == node) yield return (edge.Source, i, edge.LengthM, 0);
        }
    }

    private static WalkPath Build(IReadOnlyList<RouteEdge> edges, Dictionary<long, Came> came, SnapPoint s, SnapPoint e)
    {
        var legs = new List<Came>();
        for (var n = EndNode; n != StartNode; n = came[n].From) legs.Add(came[n]);
        legs.Reverse();

        var points = new List<GeoPoint> { s.At };
        double distance = 0, minutes = 0;
        var stepsEdges = 0;
        foreach (var leg in legs)
        {
            var edge = edges[leg.Edge];
            var len = Math.Abs(leg.PosTo - leg.PosFrom);
            distance += len;
            minutes += len * (edge.Steps ? StepsTimeFactor : 1) / WalkMetersPerMinute;
            if (edge.Steps && len > 0) stepsEdges++;
            foreach (var p in Slice(edge, leg.PosFrom, leg.PosTo))
                if (points[^1] != p) points.Add(p);
        }
        if (points[^1] != e.At) points.Add(e.At);
        return new WalkPath(points, Math.Round(distance, 1), Math.Max(1, (int)Math.Ceiling(minutes)), stepsEdges);
    }

    /// <summary>간선 위 a 에서 b 까지의 꺾은선 (a &gt; b 면 거꾸로). 위치는 Source 에서 따라간 거리 (m).</summary>
    public static List<GeoPoint> Slice(RouteEdge edge, double a, double b)
    {
        var pts = edge.Points;
        var cum = new double[pts.Length];
        for (var k = 1; k < pts.Length; k++) cum[k] = cum[k - 1] + Haversine(pts[k - 1], pts[k]);
        var scale = cum[^1] > 0 ? edge.LengthM / cum[^1] : 1;
        for (var k = 0; k < cum.Length; k++) cum[k] *= scale;

        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        var result = new List<GeoPoint> { At(lo) };
        for (var k = 0; k < pts.Length; k++)
            if (cum[k] > lo && cum[k] < hi) result.Add(pts[k]);
        result.Add(At(hi));
        if (a > b) result.Reverse();
        return result;

        GeoPoint At(double pos)
        {
            for (var k = 0; k + 1 < pts.Length; k++)
            {
                if (pos > cum[k + 1] && k + 2 < pts.Length) continue;
                var seg = cum[k + 1] - cum[k];
                var t = seg <= 0 ? 0 : Math.Clamp((pos - cum[k]) / seg, 0, 1);
                return new GeoPoint(pts[k].Lat + t * (pts[k + 1].Lat - pts[k].Lat), pts[k].Lng + t * (pts[k + 1].Lng - pts[k].Lng));
            }
            return pts[0];
        }
    }

    // ── 거리 ───────────────────────────────────────────────────────────────

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double Haversine(GeoPoint a, GeoPoint b)
    {
        const double r = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(b.Lat - a.Lat);
        var dLng = Rad(b.Lng - a.Lng);
        var x = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(x)));
    }

    /// <summary>좁은 범위(수 km)용 평면 근사 (m). 위도 기준선에서의 등장방형 투영.</summary>
    private sealed class LocalProjection(double lat0)
    {
        private readonly double _kx = Math.Cos(lat0 * Math.PI / 180) * 111_320;
        private const double Ky = 110_574;

        public (double X, double Y) ToXY(GeoPoint p) => (p.Lng * _kx, p.Lat * Ky);
        public GeoPoint ToGeo(double x, double y) => new(y / Ky, x / _kx);
    }
}
