using System.Net;
using Mashil.Api.Ops;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Mashil.Api.Tests;

/// <summary>Container Apps 이미지: API 가 웹 빌드도 내보내고, 인그레스 뒤 실제 IP 를 쓴다.</summary>
public class WebHostingTests : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("mashil-web-").FullName;

    public WebHostingTests()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>마실</title>");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc123.js"), "console.log(1)");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private async Task<TestServer> StartAsync(bool withWeb = true, bool trustIngress = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            WebRootPath = withWeb ? _webRoot : Path.Combine(_webRoot, "nothing-here"),
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:TrustIngress"] = trustIngress ? "true" : "false",
        });
        builder.Services.AddMashilForwardedHeaders(builder.Configuration);
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseMashilSecurityHeaders();
        app.MapGet("/api/ping", () => "pong");
        app.MapGet("/api/ip", (HttpContext c) => c.Connection.RemoteIpAddress!.ToString());
        app.MapMashilWeb();
        await app.StartAsync();
        return app.GetTestServer();
    }

    [Fact]
    public async Task Spa_routes_get_index_html_without_cache_and_with_security_headers()
    {
        using var server = await StartAsync();
        var res = await server.CreateClient().GetAsync("/toilets/12");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("마실", await res.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.Equal("max-age=31536000", res.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(res.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Hashed_assets_are_cached_for_a_year_and_missing_files_are_404_not_index()
    {
        using var server = await StartAsync();
        var client = server.CreateClient();
        var asset = await client.GetAsync("/assets/index-abc123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/index-gone.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/favicon-missing.png")).StatusCode);
    }

    [Fact]
    public async Task Api_keeps_working_and_unknown_api_paths_are_404()
    {
        using var server = await StartAsync();
        var client = server.CreateClient();
        Assert.Equal("pong", await client.GetStringAsync("/api/ping"));
        var missing = await client.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("<!doctype", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/toilets/12", null)).StatusCode);
    }

    [Fact]
    public async Task Without_a_web_build_nothing_is_served_but_headers_still_apply()
    {
        using var server = await StartAsync(withWeb: false);
        var client = server.CreateClient();
        var res = await client.GetAsync("/toilets/12");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.True(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Trust_ingress_uses_the_last_forwarded_address_so_spoofing_does_not_help()
    {
        using var server = await StartAsync(trustIngress: true);
        var ctx = await server.SendAsync(c =>
        {
            c.Request.Path = "/api/ip";
            c.Connection.RemoteIpAddress = IPAddress.Parse("100.100.0.9"); // 인그레스 (주소가 고정이 아님)
            c.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 203.0.113.7";  // 앞은 클라이언트가 꾸민 값, 끝은 인그레스가 붙인 값
        });
        using var reader = new StreamReader(ctx.Response.Body);
        Assert.Equal("203.0.113.7", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Without_trust_ingress_an_unknown_proxy_is_ignored()
    {
        using var server = await StartAsync(trustIngress: false);
        var ctx = await server.SendAsync(c =>
        {
            c.Request.Path = "/api/ip";
            c.Connection.RemoteIpAddress = IPAddress.Parse("100.100.0.9");
            c.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        });
        using var reader = new StreamReader(ctx.Response.Body);
        Assert.Equal("100.100.0.9", await reader.ReadToEndAsync());
    }
}
