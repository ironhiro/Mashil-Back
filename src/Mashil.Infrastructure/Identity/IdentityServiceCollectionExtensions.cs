using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mashil.Infrastructure.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// OAuth 검증·토큰·탈퇴·신고 서비스. API 와 Worker(연결 해제 재시도)가 같이 쓴다.
    /// <c>MashilDbContext</c> 와 <c>TimeProvider</c> 는 호출하는 쪽에서 등록한다.
    /// </summary>
    public static IServiceCollection AddMashilIdentity(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OAuthOptions>(config.GetSection(OAuthOptions.Section));
        // 환경 변수 템플릿의 빈 칸(OAuth__Kakao__Audiences__0=)이 [""] 로 바인딩돼 제공자가 켜지지 않도록
        services.PostConfigure<OAuthOptions>(o =>
        {
            o.Kakao.Audiences = NonBlank(o.Kakao.Audiences);
            o.Kakao.RedirectUris = NonBlank(o.Kakao.RedirectUris);
            o.Google.ClientIds = NonBlank(o.Google.ClientIds);
            o.Apple.ClientIds = NonBlank(o.Apple.ClientIds);
        });
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.Configure<ConsentOptions>(config.GetSection(ConsentOptions.Section));
        services.Configure<ModerationOptions>(config.GetSection(ModerationOptions.Section));

        services.AddHttpClient("oidc-metadata", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(nameof(KakaoIdentityProvider), c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(nameof(AppleIdentityProvider), c => c.Timeout = TimeSpan.FromSeconds(10));

        services.AddSingleton(sp => new SecretBox(sp.GetRequiredService<IOptions<OAuthOptions>>().Value.TokenEncryptionKey));

        // JWKS 는 제공자마다 하나씩, 프로세스 수명 동안 캐시 (ConfigurationManager 가 주기적으로 갱신)
        services.AddKeyedSingleton("kakao", (sp, _) => Validator(sp, KakaoIdentityProvider.MetadataAddress, [KakaoIdentityProvider.Issuer]));
        services.AddKeyedSingleton("google", (sp, _) => Validator(sp, GoogleIdentityProvider.MetadataAddress, GoogleIdentityProvider.Issuers));
        services.AddKeyedSingleton("apple", (sp, _) => Validator(sp, AppleIdentityProvider.MetadataAddress, [AppleIdentityProvider.Issuer]));

        services.AddSingleton<IIdentityProvider>(sp => new KakaoIdentityProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KakaoIdentityProvider)),
            sp.GetRequiredService<IOptions<OAuthOptions>>().Value.Kakao,
            config["Kakao:RestApiKey"],
            sp.GetRequiredKeyedService<OidcTokenValidator>("kakao"),
            sp.GetRequiredService<ILogger<KakaoIdentityProvider>>()));
        services.AddSingleton<IIdentityProvider>(sp => new GoogleIdentityProvider(
            sp.GetRequiredService<IOptions<OAuthOptions>>().Value.Google,
            sp.GetRequiredKeyedService<OidcTokenValidator>("google")));
        services.AddSingleton<IIdentityProvider>(sp => new AppleIdentityProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AppleIdentityProvider)),
            sp.GetRequiredService<IOptions<OAuthOptions>>().Value.Apple,
            sp.GetRequiredKeyedService<OidcTokenValidator>("apple"),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AppleIdentityProvider>>()));
        services.AddSingleton<IdentityProviderRegistry>();

        services.AddSingleton<AccessTokenIssuer>();
        services.AddScoped<NonceService>();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<ConsentService>();
        services.AddScoped<OAuthLoginService>();
        services.AddScoped<IdpRevocationProcessor>();
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<ModerationService>();
        return services;
    }

    internal static string[] NonBlank(string[]? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToArray() ?? [];

    private static OidcTokenValidator Validator(IServiceProvider sp, string metadata, IReadOnlyList<string> issuers) =>
        new(OidcTokenValidator.Discovery(metadata, sp.GetRequiredService<IHttpClientFactory>().CreateClient("oidc-metadata")), issuers);
}
