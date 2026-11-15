using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Mashil.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mashil.Infrastructure.Identity;

/// <summary>
/// Sign in with Apple. ID Token 검증 + (설정돼 있으면) 인가 코드를 refresh token 으로 교환해 둔다.
/// 탈퇴 시 그 refresh token 을 <c>/auth/revoke</c> 로 폐기한다 — Apple 심사 요건 (STO-02).
/// </summary>
public sealed class AppleIdentityProvider(
    HttpClient http,
    AppleAuthOptions options,
    OidcTokenValidator oidc,
    TimeProvider time,
    ILogger<AppleIdentityProvider> logger) : IIdentityProvider
{
    public const string Issuer = "https://appleid.apple.com";
    public const string MetadataAddress = "https://appleid.apple.com/.well-known/openid-configuration";
    private const string TokenUrl = "https://appleid.apple.com/auth/token";
    private const string RevokeUrl = "https://appleid.apple.com/auth/revoke";

    public AuthProvider Provider => AuthProvider.Apple;
    public bool Enabled => options.Enabled;

    public async Task<VerifiedIdentity> VerifyAsync(SocialCredential c, string? expectedNonce, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.IdToken)) throw new AuthFailure("invalid_request", 400, "Apple 은 idToken 이 필요");
        var token = await oidc.ValidateAsync(c.IdToken, options.ClientIds, ct);
        OidcTokenValidator.CheckNonce(token, expectedNonce, options.RequireNonce, allowSha256: true);
        if (string.IsNullOrWhiteSpace(token.Subject)) throw AuthFailure.Invalid("sub 없음");

        var clientId = token.Audiences.FirstOrDefault();
        string? refreshToken = null;
        if (!string.IsNullOrWhiteSpace(c.AuthorizationCode) && clientId is not null)
        {
            if (options.CanCallTokenEndpoint)
            {
                try { refreshToken = await ExchangeCodeAsync(clientId, c.AuthorizationCode, ct); }
                catch (Exception ex) when (ex is HttpRequestException or AuthFailure or JsonException)
                {
                    // 로그인은 막지 않는다. 탈퇴 때 재인증으로 받은 코드로 다시 시도할 수 있다.
                    logger.LogWarning(ex, "Apple 인가 코드 교환 실패 — refresh token 없이 로그인");
                }
            }
            else
            {
                logger.LogWarning("Apple TeamId/KeyId/PrivateKey 가 없어 인가 코드를 교환하지 않음 (탈퇴 시 revoke 불가)");
            }
        }

        return new VerifiedIdentity(AuthProvider.Apple, token.Subject, clientId, OidcTokenValidator.Claim(token, "email"), null)
        { AppleRefreshToken = refreshToken };
    }

    public async Task<RevokeResult> RevokeAsync(RevocationTarget target, VerifiedIdentity? fresh, CancellationToken ct)
    {
        if (!options.CanCallTokenEndpoint) return new(RevokeOutcome.NotConfigured, "OAuth:Apple TeamId/KeyId/PrivateKey 없음");
        var token = fresh?.AppleRefreshToken ?? target.AppleRefreshToken;
        var clientId = fresh?.ClientId ?? target.ClientId;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("Apple revoke 할 토큰이 없음 — 건너뜀");
            return RevokeResult.Done;
        }

        using var res = await http.PostAsync(RevokeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = CreateClientSecret(clientId),
            ["token"] = token,
            ["token_type_hint"] = "refresh_token",
        }), ct);
        if (res.IsSuccessStatusCode) return RevokeResult.Done;
        var body = await res.Content.ReadAsStringAsync(ct);
        // invalid_grant: 이미 폐기됐거나 만료된 토큰 → 더 할 것이 없음
        if (body.Contains("invalid_grant", StringComparison.Ordinal)) return RevokeResult.Done;
        return new(RevokeOutcome.Retry, $"HTTP {(int)res.StatusCode} {(body.Length > 200 ? body[..200] : body)}");
    }

    private async Task<string?> ExchangeCodeAsync(string clientId, string code, CancellationToken ct)
    {
        using var res = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = CreateClientSecret(clientId),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
        }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw AuthFailure.Invalid($"Apple 코드 교환 HTTP {(int)res.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
    }

    /// <summary>Apple client_secret: .p8 키로 ES256 서명한 JWT (iss=TeamId, sub=client_id, aud=appleid, 5분).</summary>
    internal string CreateClientSecret(string clientId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(options.PrivateKey);
        // 키마다 새 ECDsa 를 만들고 바로 버리므로 서명 공급자 캐시를 끈다 (캐시된 공급자가 dispose 된 키를 쥐는 것 방지)
        var key = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = options.KeyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.TeamId,
            Audience = Issuer,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", clientId)]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
        });
    }
}
