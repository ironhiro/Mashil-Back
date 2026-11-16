using System.Net;
using Mashil.Api.Ops;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Mashil.Api.Tests;

public class RateLimitingTests
{
    [Theory]
    [InlineData("GET", "/healthz/ready", RateLimitTier.None)]
    [InlineData("GET", "/swagger/index.html", RateLimitTier.None)]
    [InlineData("GET", "/api/toilets/near", RateLimitTier.Read)]
    [InlineData("OPTIONS", "/api/toilets/1/comments", RateLimitTier.Read)]
    [InlineData("POST", "/api/toilets/1/comments", RateLimitTier.Write)]
    [InlineData("PATCH", "/api/admin/comments/3", RateLimitTier.Write)]
    [InlineData("GET", "/api/auth/me", RateLimitTier.Auth)]
    [InlineData("POST", "/api/auth/oauth/kakao", RateLimitTier.Auth)]
    [InlineData("POST", "/api/auth/me/withdraw", RateLimitTier.Auth)]
    [InlineData("GET", "/api/authors", RateLimitTier.Read)] // 세그먼트 단위 비교
    public void Classify_by_path_and_method(string method, string path, RateLimitTier expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        Assert.Equal(expected, RateLimiting.Classify(ctx.Request));
    }

    [Fact]
    public void Ipv6_clients_share_a_slash_64()
    {
        var a = new DefaultHttpContext();
        a.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8:1:2:aaaa::1");
        var b = new DefaultHttpContext();
        b.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8:1:2:bbbb::9");
        Assert.Equal(RateLimiting.ClientKey(a), RateLimiting.ClientKey(b));

        var v4 = new DefaultHttpContext();
        v4.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.7");
        Assert.Equal("203.0.113.7", RateLimiting.ClientKey(v4));
    }

    private const string Proxy = "10.9.0.2";

    private static async Task<TestServer> StartAsync(bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = enabled ? "true" : "false",
            ["RateLimiting:Global:PermitLimit"] = "8",
            ["RateLimiting:Global:WindowSeconds"] = "60",
            ["RateLimiting:Write:PermitLimit"] = "3",
            ["RateLimiting:Write:WindowSeconds"] = "60",
            ["RateLimiting:Auth:PermitLimit"] = "2",
            ["RateLimiting:Auth:WindowSeconds"] = "30",
            ["ForwardedHeaders:KnownNetworks:0"] = "10.9.0.0/24",
        });
        builder.Services.AddMashilForwardedHeaders(builder.Configuration);
        builder.Services.AddMashilRateLimiting(builder.Configuration);

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseMashilRateLimiting();
        app.Run(ctx => ctx.Response.WriteAsync("ok"));
        await app.StartAsync();
        return app.GetTestServer();
    }

    private static Task<HttpContext> Send(TestServer server, string method, string path, string remote, string? forwardedFor = null) =>
        server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            if (forwardedFor is not null) c.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });

    private static async Task<int> Status(TestServer server, string method, string path, string remote, string? forwardedFor = null) =>
        (await Send(server, method, path, remote, forwardedFor)).Response.StatusCode;

    [Fact]
    public async Task Auth_tier_returns_429_with_retry_after()
    {
        using var server = await StartAsync();
        for (var i = 0; i < 2; i++)
            Assert.Equal(200, await Status(server, "POST", "/api/auth/refresh", Proxy, "203.0.113.1"));

        var rejected = await Send(server, "POST", "/api/auth/refresh", Proxy, "203.0.113.1");
        Assert.Equal(429, rejected.Response.StatusCode);
        var retryAfter = int.Parse(rejected.Response.Headers.RetryAfter.ToString());
        Assert.InRange(retryAfter, 1, 30);

        // 다른 클라이언트(프록시가 넘긴 다른 IP)는 따로 센다
        Assert.Equal(200, await Status(server, "POST", "/api/auth/refresh", Proxy, "203.0.113.2"));
    }

    [Fact]
    public async Task Reads_use_the_global_budget_and_health_is_never_limited()
    {
        using var server = await StartAsync();
        for (var i = 0; i < 8; i++)
            Assert.Equal(200, await Status(server, "GET", "/api/toilets/near", Proxy, "203.0.113.3"));
        Assert.Equal(429, await Status(server, "GET", "/api/toilets/near", Proxy, "203.0.113.3"));

        for (var i = 0; i < 20; i++)
            Assert.Equal(200, await Status(server, "GET", "/healthz/ready", Proxy));
    }

    [Fact]
    public async Task Writes_hit_their_own_limit_before_the_global_one()
    {
        using var server = await StartAsync();
        for (var i = 0; i < 3; i++)
            Assert.Equal(200, await Status(server, "POST", "/api/toilets/1/ratings", Proxy, "203.0.113.4"));
        Assert.Equal(429, await Status(server, "POST", "/api/toilets/1/ratings", Proxy, "203.0.113.4"));
        // 쓰기 한도를 넘겨도 조회는 된다 (Global 8 중 5 사용 — 거절된 요청은 미들웨어가 한 번 더 시도해서 2 를 쓴다)
        Assert.Equal(200, await Status(server, "GET", "/api/toilets/1", Proxy, "203.0.113.4"));
    }

    [Fact]
    public async Task Forwarded_for_from_an_untrusted_peer_is_ignored()
    {
        using var server = await StartAsync();
        // 프록시가 아닌 곳에서 X-Forwarded-For 를 바꿔 가며 보내도 접속 IP 하나로 센다
        for (var i = 0; i < 2; i++)
            Assert.Equal(200, await Status(server, "POST", "/api/auth/login", "198.51.100.9", $"203.0.113.{100 + i}"));
        Assert.Equal(429, await Status(server, "POST", "/api/auth/login", "198.51.100.9", "203.0.113.200"));
    }

    [Fact]
    public async Task Disabled_means_no_limit()
    {
        using var server = await StartAsync(enabled: false);
        for (var i = 0; i < 50; i++)
            Assert.Equal(200, await Status(server, "POST", "/api/auth/login", Proxy, "203.0.113.5"));
    }
}
