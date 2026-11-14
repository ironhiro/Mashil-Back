using Mashil.Domain;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mashil.Infrastructure.Identity;

/// <summary>
/// 클라이언트가 IdP 에서 받아 서버로 보내는 자격 증명. 제공자마다 쓰는 칸이 다르다.
/// <list type="bullet">
/// <item>Kakao: <see cref="IdToken"/> (OIDC) 또는 <see cref="AccessToken"/>, 웹은 <see cref="AuthorizationCode"/> + <see cref="RedirectUri"/></item>
/// <item>Google: <see cref="IdToken"/></item>
/// <item>Apple: <see cref="IdToken"/> (+ <see cref="AuthorizationCode"/> — 탈퇴 시 revoke 할 refresh token 을 받는 데 씀)</item>
/// </list>
/// </summary>
public sealed record SocialCredential(
    string? IdToken = null,
    string? AccessToken = null,
    string? AuthorizationCode = null,
    string? RedirectUri = null);

/// <summary>IdP 가 확인해 준 신원. 서버가 서명·aud·만료·nonce 를 직접 검증한 결과만 담는다.</summary>
public sealed record VerifiedIdentity(
    AuthProvider Provider,
    string Subject,
    string? ClientId,
    string? Email,
    string? Nickname)
{
    /// <summary>Kakao: 검증까지 마친 사용자 Access Token (탈퇴 시 Admin 키 없이 unlink 하는 데 쓴다). 저장하지 않는다.</summary>
    public string? KakaoAccessToken { get; init; }

    /// <summary>Apple: 인가 코드를 교환해 받은 refresh token (revoke 용). 저장할 때는 암호화한다.</summary>
    public string? AppleRefreshToken { get; init; }
}

/// <summary>연결 해제 대상. 탈퇴 대기열(<see cref="IdpRevocation"/>)에서 만든다.</summary>
public sealed record RevocationTarget(string? Subject, string? ClientId, string? AppleRefreshToken);

public enum RevokeOutcome
{
    /// <summary>끊었거나 이미 끊겨 있음 / 끊을 것이 없음.</summary>
    Done,
    /// <summary>일시 오류 — 나중에 다시.</summary>
    Retry,
    /// <summary>필요한 키(Kakao Admin 키, Apple p8)가 설정되지 않음.</summary>
    NotConfigured,
}

public sealed record RevokeResult(RevokeOutcome Outcome, string? Error = null)
{
    public static readonly RevokeResult Done = new(RevokeOutcome.Done);
}

public interface IIdentityProvider
{
    AuthProvider Provider { get; }
    bool Enabled { get; }

    /// <summary>자격 증명을 IdP 공개키 또는 토큰 정보 API 로 검증한다. 실패하면 <see cref="AuthFailure"/>.</summary>
    /// <param name="expectedNonce">GET /api/auth/nonce 로 받은 원문. null 이면 nonce 검사를 하지 않는다 (RequireNonce 면 거부).</param>
    Task<VerifiedIdentity> VerifyAsync(SocialCredential credential, string? expectedNonce, CancellationToken ct);

    /// <summary>탈퇴 시 IdP 연결 해제. <paramref name="fresh"/> 는 탈퇴 직전 재인증 결과 (있으면 우선 사용).</summary>
    Task<RevokeResult> RevokeAsync(RevocationTarget target, VerifiedIdentity? fresh, CancellationToken ct);
}

/// <summary>인증 실패. <see cref="Code"/> 는 클라이언트에 그대로 내려가고, <see cref="Detail"/> 은 로그에만 남긴다.</summary>
public sealed class AuthFailure(string code, int status = 401, string? detail = null) : Exception($"{code}: {detail}")
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string? Detail { get; } = detail;

    public static AuthFailure Invalid(string detail) => new("invalid_credential", 401, detail);
}

/// <summary>
/// OIDC ID Token 검증 공통부: JWKS 서명(RS256), iss, aud, exp. 모르는 kid 면 JWKS 를 한 번 새로 받아 재시도한다 (키 교체 대응).
/// </summary>
public sealed class OidcTokenValidator(
    IConfigurationManager<OpenIdConnectConfiguration> configuration,
    IReadOnlyList<string> issuers)
{
    public static IConfigurationManager<OpenIdConnectConfiguration> Discovery(string metadataAddress, HttpClient http) =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http) { RequireHttps = true });

    public async Task<JsonWebToken> ValidateAsync(string idToken, IReadOnlyList<string> audiences, CancellationToken ct)
    {
        if (audiences.Count == 0) throw new AuthFailure("provider_disabled", 404, "ID Token 을 받을 aud 가 설정되지 않음");
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        for (var attempt = 0; ; attempt++)
        {
            var config = await configuration.GetConfigurationAsync(ct);
            var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuers = issuers,
                ValidAudiences = audiences,
                IssuerSigningKeys = config.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            });
            if (result.IsValid) return (JsonWebToken)result.SecurityToken;
            if (attempt == 0 && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                configuration.RequestRefresh();
                continue;
            }
            throw AuthFailure.Invalid(result.Exception?.GetType().Name + ": " + result.Exception?.Message);
        }
    }

    public static string? Claim(JsonWebToken token, string type) =>
        token.TryGetPayloadValue<object>(type, out var v) ? v?.ToString() : null;

    /// <summary>
    /// 토큰의 nonce 클레임과 서버가 발급한 원문을 비교. <paramref name="allowSha256"/> 면 SHA-256 hex 도 허용
    /// (Apple 네이티브 로그인은 보통 원문의 해시를 요청에 넣는다).
    /// </summary>
    public static void CheckNonce(JsonWebToken token, string? expected, bool required, bool allowSha256 = false)
    {
        if (expected is null)
        {
            if (required) throw new AuthFailure("nonce_required", 400, "nonce 없음");
            return;
        }
        var actual = Claim(token, "nonce");
        if (actual is null) throw AuthFailure.Invalid("토큰에 nonce 클레임 없음");
        var ok = TokenHash.FixedTimeEquals(actual, expected)
                 || (allowSha256 && TokenHash.FixedTimeEquals(actual, TokenHash.Sha256Hex(expected)));
        if (!ok) throw AuthFailure.Invalid("nonce 불일치");
    }
}

/// <summary>설정에서 켜진 제공자만 로그인에 쓰고, 연결 해제는 켜짐 여부와 상관없이 시도한다.</summary>
public sealed class IdentityProviderRegistry(IEnumerable<IIdentityProvider> providers)
{
    private readonly Dictionary<AuthProvider, IIdentityProvider> _byProvider = providers.ToDictionary(p => p.Provider);

    public IIdentityProvider GetEnabled(AuthProvider provider) =>
        _byProvider.TryGetValue(provider, out var p) && p.Enabled
            ? p
            : throw new AuthFailure("provider_disabled", 404, $"{provider} 로그인이 설정되지 않음");

    public IIdentityProvider? Find(AuthProvider provider) => _byProvider.GetValueOrDefault(provider);

    public IReadOnlyList<AuthProvider> EnabledProviders =>
        _byProvider.Values.Where(p => p.Enabled).Select(p => p.Provider).Order().ToList();
}
