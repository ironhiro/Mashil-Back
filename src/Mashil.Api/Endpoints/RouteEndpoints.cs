using Mashil.Infrastructure.Routing;
using Microsoft.Extensions.Options;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 도보 경로 (방향 안내의 실제 길). 위치가 URL·접근 로그·프록시 캐시에 남지 않게 POST 본문으로 받는다 (NFR-06).
/// 요청 제한은 경로 등급(IP당 분당 30회, RateLimiting:Route)과 Global. 응답은 저장하지 않게 no-store.
/// </summary>
public static class RouteEndpoints
{
    public const string Attribution = "© OpenStreetMap contributors";

    /// <summary>서울시 도보 네트워크(공공누리 1유형: 출처 표시)를 지난 경로의 출처</summary>
    public const string AttributionWithSeoul = "© OpenStreetMap contributors · 서울특별시 도보 네트워크";

    public sealed record RoutePointDto(double Lat, double Lng);

    public sealed record WalkRouteRequest(RoutePointDto? From, RoutePointDto? To);

    /// <summary>points: [위도, 경도] 배열 (스냅한 출발점 → 스냅한 도착점). minutes 는 4km/h, 계단은 더 느리게 잡은 어림.</summary>
    public sealed record WalkRouteResponse(double[][] Points, double DistanceM, int Minutes, int Steps, string Attribution);

    public static void MapRouteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/routes").WithTags("Routes");

        group.MapPost("/walk", async (WalkRouteRequest? body, WalkRouter router, IOptions<WalkRoutingOptions> options, HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!options.Value.Enabled) return Error("routing_disabled", StatusCodes.Status404NotFound);
                if (body?.From is not { } from || body.To is not { } to) return Error("invalid_request", StatusCodes.Status400BadRequest);
                if (!WalkRouter.InServiceArea(from.Lat, from.Lng) || !WalkRouter.InServiceArea(to.Lat, to.Lng))
                    return Error("out_of_area", StatusCodes.Status400BadRequest);

                var result = await router.RouteAsync(new GeoPoint(from.Lat, from.Lng), new GeoPoint(to.Lat, to.Lng), ct);
                if (result.Path is { } path)
                {
                    var points = path.Points.Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lng, 6) }).ToArray();
                    return Results.Ok(new WalkRouteResponse(points, path.DistanceM, path.Minutes, path.StepsEdges,
                        path.UsesSeoul ? AttributionWithSeoul : Attribution));
                }
                return result.Error switch
                {
                    RouteError.OutOfArea => Error("out_of_area", StatusCodes.Status400BadRequest),
                    RouteError.TooFar => Error("too_far", StatusCodes.Status422UnprocessableEntity),
                    RouteError.FarFromRoad => Error("far_from_road", StatusCodes.Status422UnprocessableEntity),
                    RouteError.NoRoute => Error("no_route", StatusCodes.Status422UnprocessableEntity),
                    RouteError.Timeout => Error("route_timeout", StatusCodes.Status503ServiceUnavailable),
                    RouteError.Busy => Error("route_busy", StatusCodes.Status503ServiceUnavailable),
                    _ => Error("too_complex", StatusCodes.Status422UnprocessableEntity),
                };
            })
            .WithName("WalkRoute")
            .WithSummary("도보 경로 (OpenStreetMap)")
            .WithDescription("""
                body: `{ from: { lat, lng }, to: { lat, lng } }`. 대한민국 범위 밖이면 400 out_of_area, 직선 3km 초과면 422 too_far,
                출발·도착에서 150m 안에 걸을 수 있는 길이 없으면 422 far_from_road, 이어지는 길이 없으면 422 no_route,
                계산량 상한이면 422 too_complex, 시간 상한이면 503 route_timeout, 동시 계산이 가득 차면 503 route_busy, 꺼져 있으면 404 routing_disabled.
                위치는 서버에 저장하지 않고 로그에도 남기지 않는다. 경로 화면에는 attribution 을 표시한다 (ODbL).
                """)
            .Produces<WalkRouteResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
