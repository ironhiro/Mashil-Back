using Mashil.Domain;

namespace Mashil.Infrastructure.Identity;

/// <summary>
/// Google ID Token 검증 (iss = accounts.google.com, aud = 등록한 클라이언트 ID).
/// 서버가 Google 토큰을 저장하지 않으므로 탈퇴 시 끊을 것이 없다 — 앱이 SDK 로 signOut/disconnect 한다.
/// </summary>
public sealed class GoogleIdentityProvider(GoogleAuthOptions options, OidcTokenValidator oidc) : IIdentityProvider
{
    public const string MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";
    public static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    public AuthProvider Provider => AuthProvider.Google;
    public bool Enabled => options.Enabled;

    public async Task<VerifiedIdentity> VerifyAsync(SocialCredential c, string? expectedNonce, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.IdToken)) throw new AuthFailure("invalid_request", 400, "Google 은 idToken 이 필요");
        var token = await oidc.ValidateAsync(c.IdToken, options.ClientIds, ct);
        // 앱(google_sign_in 7)은 nonce 를 초기화 때 한 번만 정할 수 있어 로그인마다 받은 nonce 를 넣지 못한다.
        // 웹은 공통 흐름대로 nonce 를 보내므로, 토큰에 nonce 가 없고 필수가 아니면 요청의 nonce 는 보지 않는다.
        // 토큰에 nonce 가 있으면 그대로 일치해야 한다.
        var nonceToCheck = OidcTokenValidator.Claim(token, "nonce") is null && !options.RequireNonce ? null : expectedNonce;
        OidcTokenValidator.CheckNonce(token, nonceToCheck, options.RequireNonce);
        if (string.IsNullOrWhiteSpace(token.Subject)) throw AuthFailure.Invalid("sub 없음");

        var email = OidcTokenValidator.Claim(token, "email_verified") is "True" or "true"
            ? OidcTokenValidator.Claim(token, "email")
            : null;
        return new VerifiedIdentity(AuthProvider.Google, token.Subject, token.Audiences.FirstOrDefault(),
            email, OidcTokenValidator.Claim(token, "name"));
    }

    public Task<RevokeResult> RevokeAsync(RevocationTarget target, VerifiedIdentity? fresh, CancellationToken ct) =>
        Task.FromResult(RevokeResult.Done);
}
