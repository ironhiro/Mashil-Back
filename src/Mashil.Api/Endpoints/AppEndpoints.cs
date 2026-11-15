namespace Mashil.Api.Endpoints;

/// <summary>
/// 점검 모드 (OPS-05, Mashil-App docs/05-operations.md §4).
/// 웹·앱이 시작할 때와 API 오류가 이어질 때 확인한다. 평소에는 관리자 화면(<c>PUT /api/admin/app-status</c>, DB)에서 켜고 끈다.
/// 이 설정(App:Status)은 DB 에 값이 없거나 DB 에 닿지 못할 때의 폴백이고, 여기서 켠 플래그는 DB 와 상관없이 켜진다
/// (환경 변수 <c>App__Status__Maintenance=true</c> 로 재시작해서 켜는 비상 스위치). 합치는 규칙은 <see cref="Mashil.Infrastructure.AppStatusStore"/>.
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

    /// <summary>DB 값을 캐시하는 시간(초). 쓰기 요청마다 확인하므로 DB 를 매번 읽지 않게.</summary>
    public int CacheSeconds { get; set; } = 5;
}

public static class AppEndpoints
{
    public static IServiceCollection AddAppStatus(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AppStatusOptions>(config.GetSection("App:Status"));
        services.AddSingleton<AppStatusProvider>();
        return services;
    }

    public static void MapAppEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/app").WithTags("App");

        group.MapGet("/status", async (AppStatusProvider status, CancellationToken ct) =>
            {
                var s = await status.GetAsync(ct);
                return Results.Ok(new AppStatusResponse(s.Maintenance, s.ReadOnly, s.Message, s.Until));
            })
            .WithSummary("점검 모드 상태")
            .WithDescription("`{ maintenance, readOnly, message, until }`. 클라이언트는 캐시하지 말 것 — 점검을 끄면 바로 반영돼야 한다 (서버는 몇 초만 캐시). 점검 중에는 일반 사용자의 쓰기 요청이 503 `{\"error\":\"maintenance\"}`.");
    }

    public sealed record AppStatusResponse(bool Maintenance, bool ReadOnly, string? Message, DateTimeOffset? Until);
}
