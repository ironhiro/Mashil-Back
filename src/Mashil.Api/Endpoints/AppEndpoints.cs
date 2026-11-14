using Microsoft.Extensions.Options;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 점검 모드 (OPS-05, Mashil-App docs/05-operations.md §4).
/// 웹·앱이 시작할 때와 API 오류가 이어질 때 확인한다. 값은 설정(App:Status)에서 읽으므로
/// 환경 변수 <c>App__Status__Maintenance=true</c> 같은 방식으로 재시작만으로 켜고 끈다.
/// </summary>
public sealed class AppStatusOptions
{
    /// <summary>전체 점검 — 웹·앱이 "문이 잠시 닫혔어요" 화면을 보여 준다.</summary>
    public bool Maintenance { get; set; }

    /// <summary>읽기 전용 점검 — 찾기는 되고 평가·댓글·제보만 쉰다 (쓰기 차단은 별도).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>사용자에게 보여 줄 추가 문구 (선택).</summary>
    public string? Message { get; set; }

    /// <summary>다시 여는 시각 (ISO 8601, 선택).</summary>
    public DateTimeOffset? Until { get; set; }
}

public static class AppEndpoints
{
    public static IServiceCollection AddAppStatus(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AppStatusOptions>(config.GetSection("App:Status"));
        return services;
    }

    public static void MapAppEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/app").WithTags("App");

        group.MapGet("/status", (IOptionsSnapshot<AppStatusOptions> o) =>
            {
                var s = o.Value;
                return Results.Ok(new AppStatusResponse(s.Maintenance, s.ReadOnly, s.Message, s.Until));
            })
            .WithSummary("점검 모드 상태")
            .WithDescription("`{ maintenance, readOnly, message, until }`. 캐시하지 말 것 — 점검을 끄면 바로 반영돼야 한다.");
    }

    public sealed record AppStatusResponse(bool Maintenance, bool ReadOnly, string? Message, DateTimeOffset? Until);
}
