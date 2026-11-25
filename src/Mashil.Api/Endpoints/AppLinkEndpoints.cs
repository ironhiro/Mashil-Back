using Microsoft.Extensions.Options;

namespace Mashil.Api.Endpoints;

/// <summary>설정 <c>App:Links</c>. 비어 있는 플랫폼은 확인 파일을 내지 않는다 (404).</summary>
public sealed class AppLinkOptions
{
    /// <summary>Android 패키지 이름.</summary>
    public string AndroidPackage { get; set; } = "dev.ironhiro.mashil";

    /// <summary>앱 서명 인증서 SHA-256 지문들 ("AB:CD:…"). Play 앱 서명 키와 업로드 키, 필요하면 디버그 키.</summary>
    public List<string> AndroidSha256 { get; set; } = [];

    /// <summary>iOS 앱 ID들 ("{Team ID}.dev.ironhiro.mashil").</summary>
    public List<string> AppleAppIds { get; set; } = [];
}

/// <summary>
/// 공유 링크(<c>https://{호스트}/toilets/{id}</c>)를 앱이 열게 하는 도메인 확인 파일 (docs/APP-LINKS.md).
/// <list type="bullet">
/// <item>Android App Links: <c>/.well-known/assetlinks.json</c></item>
/// <item>iOS Universal Links: <c>/.well-known/apple-app-site-association</c> (확장자 없음, application/json, 리디렉션 없이)</item>
/// </list>
/// 앱이 받는 경로는 <c>/toilets/*</c> 뿐이다. 나머지 주소는 계속 웹(브라우저에서는 앱 설치 안내)으로 열린다.
/// 값은 공개 정보(패키지 이름, 인증서 지문, 앱 ID)라 환경 변수로 넣는다.
/// </summary>
public static class AppLinkEndpoints
{
    public const string AppPaths = "/toilets/*";

    public static IServiceCollection AddAppLinks(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AppLinkOptions>(config.GetSection("App:Links"));
        return services;
    }

    public static void MapAppLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/assetlinks.json", (IOptionsSnapshot<AppLinkOptions> o) =>
                AssetLinks(o.Value) is { } body ? Results.Json(body) : Results.NotFound())
            .ExcludeFromDescription();

        app.MapGet("/.well-known/apple-app-site-association", (IOptionsSnapshot<AppLinkOptions> o) =>
                AppleAssociation(o.Value) is { } body ? Results.Json(body, contentType: "application/json") : Results.NotFound())
            .ExcludeFromDescription();
    }

    internal static object? AssetLinks(AppLinkOptions o)
    {
        var prints = Clean(o.AndroidSha256).Select(p => p.ToUpperInvariant()).ToArray();
        if (prints.Length == 0 || string.IsNullOrWhiteSpace(o.AndroidPackage)) return null;
        return new[]
        {
            new
            {
                relation = new[] { "delegate_permission/common.handle_all_urls" },
                target = new { @namespace = "android_app", package_name = o.AndroidPackage.Trim(), sha256_cert_fingerprints = prints },
            },
        };
    }

    internal static object? AppleAssociation(AppLinkOptions o)
    {
        var ids = Clean(o.AppleAppIds).ToArray();
        if (ids.Length == 0) return null;
        return new
        {
            applinks = new
            {
                details = new[] { new { appIDs = ids, components = new[] { new Dictionary<string, string> { ["/"] = AppPaths } } } },
            },
        };
    }

    private static IEnumerable<string> Clean(IEnumerable<string> xs) =>
        xs.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
}
