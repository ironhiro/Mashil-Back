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
        OidcTokenValidator.CheckNonce(token, expectedNonce, options.RequireNonce);
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
