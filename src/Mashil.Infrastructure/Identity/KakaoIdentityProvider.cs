using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mashil.Domain;
using Microsoft.Extensions.Logging;

namespace Mashil.Infrastructure.Identity;

/// <summary>
/// 카카오 로그인 검증과 연결 끊기.
/// <list type="bullet">
/// <item>ID Token (OpenID Connect 활성화 필요): JWKS 서명 + iss=https://kauth.kakao.com + aud=앱 키 + nonce</item>
/// <item>Access Token: <c>/v1/user/access_token_info</c> 로 app_id 가 우리 앱인지 확인 → <c>/v2/user/me</c> 로 닉네임.
///   토큰 정보 API 만 믿으면 다른 앱이 발급받은 토큰으로 로그인할 수 있으므로 app_id 확인이 핵심이다.</item>
/// <item>인가 코드 (웹, USR-04): 서버가 REST API 키로 토큰을 교환한 뒤 위 둘 중 하나로 검증</item>
/// </list>
/// </summary>
public sealed class KakaoIdentityProvider(
    HttpClient http,
    KakaoAuthOptions options,
    string? fallbackRestApiKey,
    OidcTokenValidator oidc,
    ILogger<KakaoIdentityProvider> logger) : IIdentityProvider
{
    public const string Issuer = "https://kauth.kakao.com";
    public const string MetadataAddress = "https://kauth.kakao.com/.well-known/openid-configuration";
    private const string TokenUrl = "https://kauth.kakao.com/oauth/token";
    private const string TokenInfoUrl = "https://kapi.kakao.com/v1/user/access_token_info";
    private const string MeUrl = "https://kapi.kakao.com/v2/user/me";
    private const string UnlinkUrl = "https://kapi.kakao.com/v1/user/unlink";

    public AuthProvider Provider => AuthProvider.Kakao;
    public bool Enabled => options.Enabled;

    private string? RestApiKey => string.IsNullOrWhiteSpace(options.RestApiKey) ? fallbackRestApiKey : options.RestApiKey;

    public async Task<VerifiedIdentity> VerifyAsync(SocialCredential c, string? expectedNonce, CancellationToken ct)
    {
        var idToken = c.IdToken;
        var accessToken = c.AccessToken;
        IReadOnlyList<string> audiences = options.Audiences;

        if (!string.IsNullOrWhiteSpace(c.AuthorizationCode))
        {
            (idToken, accessToken) = await ExchangeCodeAsync(c.AuthorizationCode, c.RedirectUri, ct);
            // 서버가 REST API 키로 직접 교환해 받은 ID Token 은 aud 가 REST API 키다
            audiences = [.. options.Audiences, RestApiKey!];
        }

        if (!string.IsNullOrWhiteSpace(idToken))
        {
            var token = await oidc.ValidateAsync(idToken, audiences, ct);
            OidcTokenValidator.CheckNonce(token, expectedNonce, options.RequireNonce);
            var subject = token.Subject;
            if (string.IsNullOrWhiteSpace(subject)) throw AuthFailure.Invalid("sub 없음");

            // 함께 온 Access Token 은 같은 사용자·같은 앱 것일 때만 탈퇴 unlink 용으로 쓴다
            string? verifiedAccess = null;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                try
                {
                    var info = await TokenInfoAsync(accessToken, ct);
                    if (info.Id.ToString() == subject && (options.AppId is null || info.AppId == options.AppId))
                        verifiedAccess = accessToken;
                }
                catch (AuthFailure ex)
                {
                    // 로그인 자체는 ID Token 으로 이미 검증됨 — 부가 토큰 문제로 막지 않는다
                    logger.LogInformation("Kakao 부가 Access Token 무시: {Detail}", ex.Detail);
                }
            }

            return new VerifiedIdentity(AuthProvider.Kakao, subject, token.Audiences.FirstOrDefault(),
                OidcTokenValidator.Claim(token, "email"), OidcTokenValidator.Claim(token, "nickname"))
            { KakaoAccessToken = verifiedAccess, VerifiedBy = "id_token" };
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            if (options.RequireNonce) throw new AuthFailure("nonce_required", 400, "Access Token 방식은 nonce 를 검증할 수 없음 — ID Token 으로 보낼 것");
            if (options.AppId is null) throw new AuthFailure("provider_disabled", 404, "OAuth:Kakao:AppId 가 없어 Access Token 을 검증할 수 없음");

            var info = await TokenInfoAsync(accessToken, ct);
            if (info.AppId != options.AppId) throw AuthFailure.Invalid($"다른 앱의 토큰 (app_id={info.AppId})");

            var me = await GetJsonAsync(MeUrl, accessToken, ct);
            var id = me.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
            if (id != info.Id) throw AuthFailure.Invalid("사용자 정보 불일치");

            string? nickname = null, email = null;
            if (me.TryGetProperty("kakao_account", out var account))
            {
                if (account.TryGetProperty("profile", out var profile) && profile.TryGetProperty("nickname", out var n))
                    nickname = n.GetString();
                if (account.TryGetProperty("email", out var e) && IsTrue(account, "is_email_valid") && IsTrue(account, "is_email_verified"))
                    email = e.GetString();
            }
            if (nickname is null && me.TryGetProperty("properties", out var props) && props.TryGetProperty("nickname", out var pn))
                nickname = pn.GetString();

            return new VerifiedIdentity(AuthProvider.Kakao, id.ToString(), info.AppId.ToString(), email, nickname)
            { KakaoAccessToken = accessToken, VerifiedBy = "access_token" };
        }

        throw new AuthFailure("invalid_request", 400, "idToken, accessToken, authorizationCode 중 하나가 필요");
    }

    public async Task<RevokeResult> RevokeAsync(RevocationTarget target, VerifiedIdentity? fresh, CancellationToken ct)
    {
        // 1) 재인증 때 검증한 사용자 토큰 — Admin 키 없이도 끊을 수 있다
        if (fresh?.KakaoAccessToken is { } userToken && fresh.Subject == target.Subject)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, UnlinkUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            var r = await SendUnlinkAsync(req, ct);
            if (r.Outcome == RevokeOutcome.Done) return r;
            logger.LogInformation("Kakao 사용자 토큰으로 unlink 실패 ({Error}) — Admin 키로 재시도", r.Error);
        }

        // 2) Admin 키 + 회원번호
        if (string.IsNullOrWhiteSpace(options.AdminKey)) return new(RevokeOutcome.NotConfigured, "OAuth:Kakao:AdminKey 없음");
        if (string.IsNullOrWhiteSpace(target.Subject)) return RevokeResult.Done;

        using var adminReq = new HttpRequestMessage(HttpMethod.Post, UnlinkUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["target_id_type"] = "user_id",
                ["target_id"] = target.Subject,
            }),
        };
        adminReq.Headers.Authorization = new AuthenticationHeaderValue("KakaoAK", options.AdminKey);
        return await SendUnlinkAsync(adminReq, ct);
    }

    private async Task<RevokeResult> SendUnlinkAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return RevokeResult.Done;
        var body = await res.Content.ReadAsStringAsync(ct);
        // -101: 이미 앱과 연결되지 않은 사용자 → 끊긴 것으로 본다
        if (KakaoErrorCode(body) == -101) return RevokeResult.Done;
        return new(RevokeOutcome.Retry, $"HTTP {(int)res.StatusCode} {Trim(body)}");
    }

    private async Task<(string? IdToken, string? AccessToken)> ExchangeCodeAsync(string code, string? redirectUri, CancellationToken ct)
    {
        if (options.RedirectUris.Length == 0 || string.IsNullOrWhiteSpace(RestApiKey))
            throw new AuthFailure("provider_disabled", 404, "Kakao 인가 코드 방식이 설정되지 않음 (RedirectUris, RestApiKey)");
        if (redirectUri is null || !options.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            throw new AuthFailure("invalid_request", 400, "허용되지 않은 redirect_uri");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = RestApiKey!,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
        };
        if (!string.IsNullOrWhiteSpace(options.ClientSecret)) form["client_secret"] = options.ClientSecret;

        using var res = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw AuthFailure.Invalid($"인가 코드 교환 실패 HTTP {(int)res.StatusCode} {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        return (Str(doc.RootElement, "id_token"), Str(doc.RootElement, "access_token"));
    }

    private async Task<(long Id, long AppId)> TokenInfoAsync(string accessToken, CancellationToken ct)
    {
        var info = await GetJsonAsync(TokenInfoUrl, accessToken, ct);
        return (info.GetProperty("id").GetInt64(), info.GetProperty("app_id").GetInt64());
    }

    private async Task<JsonElement> GetJsonAsync(string url, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw AuthFailure.Invalid($"Kakao 토큰 거부 HTTP {(int)res.StatusCode} {Trim(body)}");
        if (!res.IsSuccessStatusCode)
            throw new AuthFailure("idp_unavailable", 503, $"Kakao HTTP {(int)res.StatusCode} {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static int? KakaoErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}
