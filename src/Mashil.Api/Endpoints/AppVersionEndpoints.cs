using Microsoft.Extensions.Options;

namespace Mashil.Api.Endpoints;

/// <summary>플랫폼 하나의 버전 정책. 값이 비어 있으면 그 안내를 하지 않는다.</summary>
public sealed class PlatformVersionOptions
{
    /// <summary>이보다 낮은 앱은 강제 업데이트 화면 (예: "1.0.0").</summary>
    public string? Minimum { get; set; }

    /// <summary>스토어의 최신 버전. 이보다 낮으면 권장 업데이트 안내.</summary>
    public string? Latest { get; set; }

    /// <summary>업데이트 버튼이 여는 스토어 주소.</summary>
    public string? StoreUrl { get; set; }
}

/// <summary>설정 <c>App:Versions:Ios</c>, <c>App:Versions:Android</c>.</summary>
public sealed class AppVersionOptions
{
    public PlatformVersionOptions Ios { get; set; } = new();
    public PlatformVersionOptions Android { get; set; } = new();
}

/// <summary>
/// 앱 버전 확인 (APP-09). 앱이 시작할 때 <c>GET /api/app/version?platform=ios|android</c> 를 부른다.
/// 스토어에 새 버전을 올린 뒤 <c>App__Versions__Android__Latest=1.2.0</c> 처럼 환경 변수로 바꾸고 재시작하면 된다.
/// </summary>
public static class AppVersionEndpoints
{
    public static IServiceCollection AddAppVersion(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AppVersionOptions>(config.GetSection("App:Versions"));
        return services;
    }

    public static void MapAppVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/app/version", (string? platform, IOptionsSnapshot<AppVersionOptions> o) =>
            {
                var p = platform?.Trim().ToLowerInvariant() switch
                {
                    "ios" => o.Value.Ios,
                    "android" => o.Value.Android,
                    _ => null,
                };
                if (p is null) return AuthEndpoints.Error("unknown_platform", 400);
                return Results.Ok(new AppVersionResponse(Blank(p.Minimum), Blank(p.Latest), Blank(p.StoreUrl)));
            })
            .WithTags("App")
            .WithSummary("앱 버전 정책")
            .WithDescription("`platform` = `ios` / `android`. `{ minimum, latest, storeUrl }` — 현재 버전이 minimum 보다 낮으면 강제 업데이트, latest 보다 낮으면 권장 업데이트. 값이 null 이면 그 안내를 하지 않는다.")
            .Produces<AppVersionResponse>()
            .Produces(StatusCodes.Status400BadRequest);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public sealed record AppVersionResponse(string? Minimum, string? Latest, string? StoreUrl);
}
