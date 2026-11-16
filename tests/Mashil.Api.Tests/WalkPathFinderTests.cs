using Mashil.Infrastructure.Routing;

namespace Mashil.Api.Tests;

public class WalkPathFinderTests
{
    // 광화문 근처 격자. 위도 0.001 ≈ 111m, 경도 0.001 ≈ 88m
    private const double Lat0 = 37.570, Lng0 = 126.976;

    private static GeoPoint P(double dLat, double dLng) => new(Lat0 + dLat, Lng0 + dLng);

    private static RouteEdge E(long id, long a, long b, GeoPoint pa, GeoPoint pb, float factor = 1, bool steps = false) =>
        new(id, a, b, WalkPathFinder.Haversine(pa, pb), factor, steps, [pa, pb]);

    /// <summary>
    ///   1 ── 2 ── 3      위 길(1-2-3)은 보행로, 아래 길(1-4-3)은 큰 차도(가중치 1.5)
    ///   │         │
    ///   4 ─────── 3'
    /// </summary>
    private static List<RouteEdge> Square(float bottomFactor)
    {
        var n1 = P(0.001, 0); var n2 = P(0.001, 0.001); var n3 = P(0.001, 0.002); var n4 = P(0, 0); var n5 = P(0, 0.002);
        return
        [
            E(1, 1, 2, n1, n2), E(2, 2, 3, n2, n3),
            E(3, 1, 4, n1, n4, bottomFactor), E(4, 4, 5, n4, n5, bottomFactor), E(5, 5, 3, n5, n3, bottomFactor),
        ];
    }

    [Fact]
    public void Picks_the_cheaper_way_not_just_the_shorter_one()
    {
        // 출발은 1 바로 옆, 도착은 3 바로 옆. 위 길이 짧고 보행로라 위로 간다
        var path = WalkPathFinder.Find(Square(1.5f), P(0.00101, 0), P(0.00101, 0.002));
        Assert.All(path.Points, p => Assert.True(p.Lat > Lat0 + 0.0009, "아래 차도로 내려가지 않는다"));
        Assert.InRange(path.DistanceM, 170, 185); // 88m × 2
        Assert.Equal(3, path.Minutes); // 176m / 66.7 m/분 → 올림 3분
    }

    [Fact]
    public void Starts_and_ends_on_the_snapped_points_in_the_middle_of_edges()
    {
        var edges = Square(1.5f);
        var from = P(0.00105, 0.0005); // 1-2 가운데에서 5m 위
        var to = P(0.00095, 0.0015);   // 2-3 가운데에서 5m 아래
        var path = WalkPathFinder.Find(edges, from, to);
        Assert.Equal(Lat0 + 0.001, path.Points[0].Lat, 6);
        Assert.Equal(Lng0 + 0.0005, path.Points[0].Lng, 6);
        Assert.Equal(Lng0 + 0.0015, path.Points[^1].Lng, 6);
        Assert.InRange(path.DistanceM, 85, 92); // 노드 2 를 지나 반씩
    }

    [Fact]
    public void Same_edge_goes_straight_along_it()
    {
        var path = WalkPathFinder.Find(Square(1.5f), P(0.001, 0.0002), P(0.001, 0.0008));
        Assert.Equal(2, path.Points.Count);
        Assert.InRange(path.DistanceM, 50, 56);
    }

    [Fact]
    public void Far_from_any_way_fails_with_far_from_road()
    {
        var ex = Assert.Throws<RouteException>(() => WalkPathFinder.Find(Square(1f), P(0.01, 0), P(0.001, 0.002)));
        Assert.Equal(RouteFailure.FarFromRoad, ex.Failure);
    }

    [Fact]
    public void Disconnected_pieces_fail_with_no_route()
    {
        var a1 = P(0, 0); var a2 = P(0, 0.001);
        var b1 = P(0, 0.003); var b2 = P(0, 0.004);
        List<RouteEdge> edges = [E(1, 1, 2, a1, a2), E(2, 3, 4, b1, b2)];
        var ex = Assert.Throws<RouteException>(() => WalkPathFinder.Find(edges, P(0, 0.0005), P(0, 0.0035), snapMaxM: 30));
        Assert.Equal(RouteFailure.NoRoute, ex.Failure);
    }

    [Fact]
    public void Tries_the_next_nearest_way_when_the_nearest_is_a_dead_piece()
    {
        // 출발점 바로 옆에 끊어진 짧은 조각(10), 조금 더 떨어진 곳에 이어진 길(1-2)
        var n1 = P(0, 0); var n2 = P(0, 0.002);
        var island1 = P(0.0003, 0.0009); var island2 = P(0.0003, 0.0011);
        List<RouteEdge> edges = [E(1, 1, 2, n1, n2), E(10, 10, 11, island1, island2)];
        var path = WalkPathFinder.Find(edges, P(0.00028, 0.001), P(0, 0.0019));
        Assert.Equal(Lat0, path.Points[0].Lat, 6); // 섬이 아니라 이어진 길에 붙었다
    }

    [Fact]
    public void Reports_when_the_path_uses_seoul_links_for_the_credit()
    {
        var a = P(0, 0); var m = P(0, 0.001); var b = P(0, 0.002);
        List<RouteEdge> osmOnly = [E(1, 1, 2, a, m), E(2, 2, 3, m, b)];
        List<RouteEdge> mixed = [E(1, 1, 2, a, m), E(2, 2, 3, m, b) with { Seoul = true }];
        Assert.False(WalkPathFinder.Find(osmOnly, a, b).UsesSeoul);
        Assert.True(WalkPathFinder.Find(mixed, a, b).UsesSeoul);
    }

    [Fact]
    public void Steps_take_longer()
    {
        var a = P(0, 0); var b = P(0, 0.003);
        var flat = WalkPathFinder.Find([E(1, 1, 2, a, b)], a, b);
        var stairs = WalkPathFinder.Find([E(1, 1, 2, a, b, steps: true)], a, b);
        Assert.True(stairs.Minutes > flat.Minutes);
        Assert.Equal(1, stairs.StepsEdges);
    }

    [Fact]
    public void Expansion_limit_reports_too_complex()
    {
        // 길게 이어진 사슬. 상한 3 이면 끝까지 못 간다
        var edges = Enumerable.Range(0, 20)
            .Select(i => E(i, i, i + 1, P(0, i * 0.0005), P(0, (i + 1) * 0.0005))).ToList();
        var ex = Assert.Throws<RouteException>(() => WalkPathFinder.Find(edges, P(0, 0.0001), P(0, 0.0099), maxExpanded: 3));
        Assert.Equal(RouteFailure.TooComplex, ex.Failure);
    }

    [Fact]
    public void Slice_reverses_when_walking_target_to_source()
    {
        var a = P(0, 0); var m = P(0, 0.001); var b = P(0, 0.002);
        var edge = new RouteEdge(1, 1, 2, WalkPathFinder.Haversine(a, m) + WalkPathFinder.Haversine(m, b), 1, false, [a, m, b]);
        var back = WalkPathFinder.Slice(edge, edge.LengthM, 0);
        Assert.Equal(b, back[0]);
        Assert.Equal(m, back[1]);
        Assert.Equal(a, back[^1]);
    }

    [Theory]
    [InlineData(37.57, 126.98, true)]
    [InlineData(33.5, 126.5, true)]   // 제주
    [InlineData(35.68, 139.76, false)] // 도쿄
    [InlineData(double.NaN, 126.98, false)]
    [InlineData(37.57, double.PositiveInfinity, false)]
    public void Service_area_is_korea_only(double lat, double lng, bool expected) =>
        Assert.Equal(expected, WalkRouter.InServiceArea(lat, lng));
}
