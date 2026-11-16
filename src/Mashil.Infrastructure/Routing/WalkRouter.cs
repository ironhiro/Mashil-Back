using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Mashil.Infrastructure.Routing;

/// <summary>설정 섹션 <c>Routing</c>. 계산량 상한은 서비스 거부를 막는 값이라 운영에서 함부로 키우지 않는다.</summary>
public sealed class WalkRoutingOptions
{
    public const string Section = "Routing";

    /// <summary>꺼져 있으면 경로 API 가 404 를 준다 (웹은 직선 안내로 돌아간다).</summary>
    public bool Enabled { get; set; }

    /// <summary>출발·도착 직선 거리 상한 (m). 화장실 길 안내라 짧게.</summary>
    public double MaxDistanceM { get; set; } = 3_000;

    /// <summary>출발·도착에서 길까지 이보다 멀면 실패 (m).</summary>
    public double SnapMaxM { get; set; } = 150;

    /// <summary>출발·도착을 감싸는 범위에 더하는 여백 (m). 못 찾으면 한 번 넓혀 다시 찾는다.</summary>
    public double MarginM { get; set; } = 400;

    /// <summary>한 번에 읽는 간선 수 상한 (메모리·시간 상한).</summary>
    public int MaxEdges { get; set; } = 150_000;

    /// <summary>A* 가 꺼내 볼 노드 수 상한.</summary>
    public int MaxExpanded { get; set; } = 300_000;

    /// <summary>요청 하나의 시간 상한 (초).</summary>
    public int TimeoutSeconds { get; set; } = 5;
}

public enum RouteError { OutOfArea, TooFar, FarFromRoad, NoRoute, TooComplex, Timeout }

public sealed record RouteResult(WalkPath? Path, RouteError? Error);

/// <summary>
/// 도보 경로 (MON-03 의 "앱 안 실제 경로"). 출발·도착을 감싸는 범위의 walk_edges 만 읽어 <see cref="WalkPathFinder"/> 로 찾는다.
/// 위치는 개인정보라 로그에 남기지 않는다 (NFR-06): 건수와 시간만.
/// </summary>
public sealed class WalkRouter(MashilDbContext db, IOptions<WalkRoutingOptions> options, ILogger<WalkRouter> logger)
{
    /// <summary>받는 좌표 범위 (대한민국 주변). 밖이면 계산하지 않는다.</summary>
    public static bool InServiceArea(double lat, double lng) =>
        double.IsFinite(lat) && double.IsFinite(lng) && lat is >= 32.5 and <= 39.0 && lng is >= 124.0 and <= 132.0;

    public async Task<RouteResult> RouteAsync(GeoPoint from, GeoPoint to, CancellationToken ct)
    {
        var o = options.Value;
        if (!InServiceArea(from.Lat, from.Lng) || !InServiceArea(to.Lat, to.Lng)) return new(null, RouteError.OutOfArea);
        var straight = WalkPathFinder.Haversine(from, to);
        if (straight > o.MaxDistanceM) return new(null, RouteError.TooFar);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 30)));
        var started = DateTime.UtcNow;
        try
        {
            // 처음엔 좁게, 못 찾으면(돌아가는 길) 한 번 넓혀서
            foreach (var margin in new[] { Math.Max(o.MarginM, straight * 0.3), Math.Max(o.MarginM, straight * 0.3) * 2.5 })
            {
                var edges = await LoadAsync(from, to, margin, o.MaxEdges, timeout.Token);
                if (edges is null) return Fail(RouteError.TooComplex, 0);
                try
                {
                    var path = WalkPathFinder.Find(edges, from, to, o.SnapMaxM, o.MaxExpanded);
                    logger.LogInformation("도보 경로 {Edges}개 간선에서 {Points}점 {Distance:F0}m, {Ms}ms",
                        edges.Count, path.Points.Count, path.DistanceM, (DateTime.UtcNow - started).TotalMilliseconds);
                    return new(path, null);
                }
                catch (RouteException ex) when (ex.Failure == RouteFailure.NoRoute)
                {
                    continue; // 범위를 넓혀 다시
                }
                catch (RouteException ex)
                {
                    return Fail(ex.Failure == RouteFailure.FarFromRoad ? RouteError.FarFromRoad : RouteError.TooComplex, edges.Count);
                }
            }
            return Fail(RouteError.NoRoute, 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(RouteError.Timeout, 0);
        }

        RouteResult Fail(RouteError error, int edges)
        {
            logger.LogInformation("도보 경로 실패 {Error} (간선 {Edges}개, {Ms}ms)", error, edges, (DateTime.UtcNow - started).TotalMilliseconds);
            return new(null, error);
        }
    }

    /// <summary>범위 안 간선. 상한을 넘으면 null (너무 넓거나 촘촘함).</summary>
    private async Task<List<RouteEdge>?> LoadAsync(GeoPoint a, GeoPoint b, double marginM, int maxEdges, CancellationToken ct)
    {
        var dLat = marginM / 110_574;
        var dLng = marginM / (111_320 * Math.Cos((a.Lat + b.Lat) / 2 * Math.PI / 180));
        var envelope = new Envelope(
            Math.Min(a.Lng, b.Lng) - dLng, Math.Max(a.Lng, b.Lng) + dLng,
            Math.Min(a.Lat, b.Lat) - dLat, Math.Max(a.Lat, b.Lat) + dLat);
        var box = new GeometryFactory(new PrecisionModel(), 4326).ToGeometry(envelope);

        // 매개변수화된 쿼리 (EF). 상한 + 1 개까지만 읽어 넘었는지 본다
        var rows = await db.WalkEdges.AsNoTracking()
            .Where(e => e.Geom.Intersects(box))
            .Select(e => new { e.Id, e.Source, e.Target, e.LengthM, e.CostFactor, e.Steps, e.Geom })
            .Take(maxEdges + 1)
            .ToListAsync(ct);
        if (rows.Count > maxEdges) return null;
        return rows.Select(r => new RouteEdge(r.Id, r.Source, r.Target, r.LengthM, r.CostFactor, r.Steps,
            r.Geom.Coordinates.Select(c => new GeoPoint(c.Y, c.X)).ToArray())).ToList();
    }
}
