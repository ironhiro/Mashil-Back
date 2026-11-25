using System.Net;
using System.Text.Json;
using Mashil.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Mashil.Api.Tests;

/// <summary>공유 링크 도메인 확인 파일 (App Links, Universal Links).</summary>
public class AppLinkTests
{
    private const string Print = "aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99";

    private static async Task<HttpClient> StartAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAppLinks(builder.Configuration);
        var app = builder.Build();
        app.MapAppLinkEndpoints();
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Not_configured_is_404()
    {
        using var c = await StartAsync([]);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/.well-known/assetlinks.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/.well-known/apple-app-site-association")).StatusCode);
    }

    [Fact]
    public async Task Assetlinks_lists_package_and_upper_case_prints()
    {
        using var c = await StartAsync(new() { ["App:Links:AndroidSha256:0"] = Print, ["App:Links:AndroidSha256:1"] = " " + Print.ToUpperInvariant() });
        var res = await c.GetAsync("/.well-known/assetlinks.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var e = doc.RootElement[0];
        Assert.Equal("delegate_permission/common.handle_all_urls", e.GetProperty("relation")[0].GetString());
        var t = e.GetProperty("target");
        Assert.Equal("android_app", t.GetProperty("namespace").GetString());
        Assert.Equal("dev.ironhiro.mashil", t.GetProperty("package_name").GetString());
        var prints = t.GetProperty("sha256_cert_fingerprints");
        Assert.Equal(1, prints.GetArrayLength()); // 대소문자만 다른 중복은 하나로
        Assert.Equal(Print.ToUpperInvariant(), prints[0].GetString());
    }

    [Fact]
    public async Task Apple_association_only_claims_toilet_paths()
    {
        using var c = await StartAsync(new() { ["App:Links:AppleAppIds:0"] = "ABCDE12345.dev.ironhiro.mashil" });
        var res = await c.GetAsync("/.well-known/apple-app-site-association");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var d = doc.RootElement.GetProperty("applinks").GetProperty("details")[0];
        Assert.Equal("ABCDE12345.dev.ironhiro.mashil", d.GetProperty("appIDs")[0].GetString());
        Assert.Equal("/toilets/*", d.GetProperty("components")[0].GetProperty("/").GetString());
    }
}
