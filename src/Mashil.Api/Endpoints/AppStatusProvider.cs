using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.Extensions.Options;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 지금 점검 상태 (OPS-05, ADM-06). DB(<c>app_status</c>) + 설정(App:Status) 을 합친 값을 몇 초 동안 캐시한다 —
/// 쓰기 요청마다 확인하므로 요청마다 DB 를 읽지 않게. 관리자가 바꾸면 이 인스턴스의 캐시는 바로 비우고,
/// 다른 인스턴스는 캐시가 끝나는 대로(기본 5초) 따라온다.
/// </summary>
public sealed class AppStatusProvider(IServiceScopeFactory scopes, IOptionsMonitor<AppStatusOptions> options, TimeProvider time, ILogger<AppStatusProvider> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppStatusSnapshot? _cached;
    private DateTimeOffset _expires;

    private AppStatusSnapshot Config()
    {
        var o = options.CurrentValue;
        return new AppStatusSnapshot(o.Maintenance, o.ReadOnly, o.Message, o.Until);
    }

    private TimeSpan CacheFor => TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.CacheSeconds, 0, 60));

    public async Task<AppStatusSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } hit && time.GetUtcNow() < _expires) return hit;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } again && time.GetUtcNow() < _expires) return again;
            AppStatusSnapshot value;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
                value = AppStatusStore.Merge(await AppStatusStore.LoadAsync(db, ct), Config());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // DB 에 닿지 못해도 점검 상태 확인은 실패하지 않는다 — 설정 값으로
                logger.LogWarning(ex, "점검 상태를 DB 에서 읽지 못해 설정 값을 씀");
                value = Config();
            }
            _cached = value;
            _expires = time.GetUtcNow() + CacheFor;
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>관리자 화면에서 바꾼 직후 호출.</summary>
    public void Invalidate() => _cached = null;

    /// <summary>설정(App:Status) 값만 — 관리자 화면에서 "설정으로 켜져 있음"을 알려 주려고.</summary>
    public AppStatusSnapshot ConfigValue => Config();
}

/// <summary>
/// 점검 중 쓰기 차단 (OPS-05: "가능하면 조회는 유지하고 쓰기만 차단한다").
/// 전체 점검이나 읽기 전용이면 <c>/api</c> 아래 POST·PUT·PATCH·DELETE 를 503 <c>{"error":"maintenance"}</c> 로 돌려보낸다.
/// 로그인·토큰 갱신(<c>/api/auth</c>)과 관리자 기능(<c>/api/admin</c>), 관리자 계정의 요청은 막지 않는다 (점검을 끄려면 관리자가 써야 하므로).
/// 인증 뒤, CORS 뒤에 둔다 — 503 에도 CORS 헤더가 붙어야 웹이 이유를 읽는다.
/// </summary>
public static class MaintenanceWriteBlock
{
    public static IApplicationBuilder UseMaintenanceWriteBlock(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (IsWrite(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/api")
            && !ctx.Request.Path.StartsWithSegments("/api/auth") && !ctx.Request.Path.StartsWithSegments("/api/admin")
            && !ctx.User.IsInRole(nameof(UserRole.Admin)))
        {
            var status = await ctx.RequestServices.GetRequiredService<AppStatusProvider>().GetAsync(ctx.RequestAborted);
            if (status.WritesBlocked)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                if (status.Until is { } until && until > DateTimeOffset.UtcNow)
                    ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)).ToString();
                await ctx.Response.WriteAsJsonAsync(new { error = "maintenance", status.Maintenance, status.ReadOnly, status.Message, status.Until });
                return;
            }
        }
        await next(ctx);
    });

    private static bool IsWrite(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
