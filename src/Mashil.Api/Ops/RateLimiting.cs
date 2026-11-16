using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Mashil.Api.Ops;

/// <summary>
/// 요청 빈도 제한 (ASP.NET 기본 RateLimiter). 클라이언트 IP 단위로 센다.
/// <list type="bullet">
///   <item>Global — <c>/api/*</c> 전체 (조회 포함). 넉넉하게</item>
///   <item>Write — <c>/api/*</c> 의 GET·HEAD·OPTIONS 외 요청 (평점·댓글·제보·신고·관리자 수정)</item>
///   <item>Auth — <c>/api/auth/*</c> 전체 (로그인·토큰 갱신·탈퇴). 가장 엄격</item>
/// </list>
/// 쓰기·인증 요청은 자기 등급과 Global 을 둘 다 통과해야 한다. <c>/healthz*</c> 는 세지 않는다 (Caddy 헬스체크).
/// 엔드포인트 메타데이터 없이 경로·메서드로만 나누므로 Endpoints 파일을 고칠 필요가 없다.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string Section = "RateLimiting";

    /// <summary>끄면 제한 없음. 개발 설정(appsettings.Development.json)은 false — 로컬 k6(한 IP 에서 수백 req/s)용.</summary>
    public bool Enabled { get; set; } = true;

    public RateLimitRule Global { get; set; } = new() { PermitLimit = 1200, WindowSeconds = 60 };
    public RateLimitRule Write { get; set; } = new() { PermitLimit = 60, WindowSeconds = 60 };
    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 30, WindowSeconds = 60 };
}

public sealed class RateLimitRule
{
    /// <summary>창 하나에서 허용하는 요청 수.</summary>
    public int PermitLimit { get; set; }

    /// <summary>고정 창 길이(초).</summary>
    public int WindowSeconds { get; set; } = 60;
}

public enum RateLimitTier
{
    /// <summary>제한하지 않음 (헬스체크, Swagger, 개발용 페이지).</summary>
    None,
    Read,
    Write,
    Auth,
}

public static class RateLimiting
{
    /// <summary>경로·메서드로 등급을 정한다.</summary>
    public static RateLimitTier Classify(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api")) return RateLimitTier.None;
        if (request.Path.StartsWithSegments("/api/auth")) return RateLimitTier.Auth;
        var m = request.Method;
        return HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m)
            ? RateLimitTier.Read
            : RateLimitTier.Write;
    }

    /// <summary>
    /// 파티션 키 = 클라이언트 IP. <c>UseForwardedHeaders</c> 뒤라서 Caddy 가 넘긴 X-Forwarded-For 가 반영돼 있다.
    /// IPv6 는 /64 단위로 묶는다 (한 회선이 /64 를 통째로 받으므로 주소만 바꿔 피하는 것을 막음).
    /// </summary>
    public static string ClientKey(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }
        return ip.ToString();
    }

    public static PartitionedRateLimiter<HttpContext> CreateLimiter(RateLimitingOptions o)
    {
        var global = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            Classify(ctx.Request) == RateLimitTier.None
                ? RateLimitPartition.GetNoLimiter("none")
                : Window("global:" + ClientKey(ctx), o.Global));

        var tiered = PartitionedRateLimiter.Create<HttpContext, string>(ctx => Classify(ctx.Request) switch
        {
            RateLimitTier.Auth => Window("auth:" + ClientKey(ctx), o.Auth),
            RateLimitTier.Write => Window("write:" + ClientKey(ctx), o.Write),
            _ => RateLimitPartition.GetNoLimiter("none"),
        });

        return PartitionedRateLimiter.CreateChained(global, tiered);
    }

    private static RateLimitPartition<string> Window(string key, RateLimitRule rule) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, rule.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, rule.WindowSeconds)),
            QueueLimit = 0, // 줄 세우지 않고 바로 429
            AutoReplenishment = true,
        });

    public static IServiceCollection AddMashilRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var o = config.GetSection(RateLimitingOptions.Section).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.AddSingleton(o);
        if (!o.Enabled) return services;

        services.AddRateLimiter(rl =>
        {
            rl.GlobalLimiter = CreateLimiter(o);
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rl.OnRejected = async (ctx, ct) =>
            {
                var seconds = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                    : Math.Max(1, o.Global.WindowSeconds);
                ctx.HttpContext.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
                await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited" }, ct);
            };
        });
        return services;
    }

    /// <summary><c>UseCors</c> 뒤에 둔다 (429 에도 CORS 헤더가 붙어야 웹이 응답을 읽는다).</summary>
    public static IApplicationBuilder UseMashilRateLimiting(this IApplicationBuilder app)
    {
        var o = app.ApplicationServices.GetRequiredService<RateLimitingOptions>();
        return o.Enabled ? app.UseRateLimiter() : app;
    }

    // --- 프록시 뒤의 클라이언트 IP ---

    /// <summary>
    /// X-Forwarded-For·X-Forwarded-Proto 는 <c>ForwardedHeaders:KnownProxies</c>(IP) 또는
    /// <c>ForwardedHeaders:KnownNetworks</c>(CIDR) 에서 온 요청만 믿는다. 비우면 루프백만 (ASP.NET 기본값).
    /// VM(Compose)은 Caddy 가 있는 내부 네트워크 대역을 넣는다. 아무나 헤더를 꾸며 제한을 피하지 못하게 하려는 것.
    /// <para>
    /// <c>ForwardedHeaders:TrustIngress=true</c> (Azure Container Apps): 인그레스 주소가 고정이 아니라 보낸 쪽은 따지지 않고,
    /// <c>ForwardLimit=1</c> 로 X-Forwarded-For 의 <b>마지막 값(인그레스가 붙인 실제 접속 IP)</b>만 쓴다.
    /// 클라이언트가 앞쪽에 꾸며 넣은 값은 무시된다. 컨테이너에 인그레스 말고 들어올 길이 없을 때만 켠다.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMashilForwardedHeaders(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection("ForwardedHeaders");
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var trustIngress = section.GetValue<bool>("TrustIngress");

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1; // 바로 앞 프록시(Caddy 또는 인그레스) 한 단계만
            if (trustIngress)
            {
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                return;
            }
            if (proxies.Length + networks.Length == 0) return;

            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var p in proxies.Where(s => !string.IsNullOrWhiteSpace(s)))
                o.KnownProxies.Add(IPAddress.Parse(p.Trim()));
            foreach (var n in networks.Where(s => !string.IsNullOrWhiteSpace(s)))
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n.Trim()));
        });
        return services;
    }
}
