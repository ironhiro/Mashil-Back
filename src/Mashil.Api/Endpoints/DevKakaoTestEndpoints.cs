using System.Text.Encodings.Web;
using Mashil.Domain;
using Mashil.Infrastructure;
using Mashil.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 개발 환경 전용 카카오 로그인 테스트 페이지. 앱(P5) 없이 브라우저로 실제 카카오 계정 로그인 → 서버 검증 → 가입·탈퇴를 확인한다.
/// 웹 인가 코드 방식 (USR-04) 과 같은 경로(<see cref="OAuthLoginService"/>, <see cref="AccountDeletionService"/>)를 탄다.
/// 콘솔에 Redirect URI <c>http://localhost:5099/dev/kakao/callback</c> 을 등록해야 한다.
/// </summary>
public static class DevKakaoTestEndpoints
{
    public const string CallbackPath = "/dev/kakao/callback";

    public static void MapDevKakaoTestEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        // mode=login (기본) 또는 mode=delete&user=<마실 사용자 id> (재인증 후 탈퇴)
        app.MapGet("/dev/kakao/login", async (string? mode, Guid? user, HttpRequest req, NonceService nonces,
                IOptions<OAuthOptions> oauth, IConfiguration config, CancellationToken ct) =>
            {
                var restKey = oauth.Value.Kakao.RestApiKey ?? config["Kakao:RestApiKey"];
                if (string.IsNullOrWhiteSpace(restKey)) return Html("REST API 키가 없습니다 (Kakao:RestApiKey).");
                var redirect = Redirect(req);
                if (!oauth.Value.Kakao.RedirectUris.Contains(redirect))
                    return Html($"OAuth:Kakao:RedirectUris 에 {Enc(redirect)} 가 없습니다.");

                var (nonce, _) = await nonces.IssueAsync(ct);
                var state = mode == "delete" && user is not null ? $"delete.{user}.{nonce}" : $"login..{nonce}";
                var url = "https://kauth.kakao.com/oauth/authorize?response_type=code"
                          + "&client_id=" + Uri.EscapeDataString(restKey)
                          + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                          + "&nonce=" + Uri.EscapeDataString(nonce)
                          + "&state=" + Uri.EscapeDataString(state)
                          + (mode == "delete" ? "&prompt=login" : "");
                return Results.Redirect(url);
            })
            .ExcludeFromDescription();

        app.MapGet(CallbackPath, async (string? code, string? state, string? error, string? error_description,
                HttpRequest req, OAuthLoginService login, AccountDeletionService deletion, MashilDbContext db, CancellationToken ct) =>
            {
                if (error is not null) return Html($"카카오가 오류를 돌려줌: {Enc(error)} {Enc(error_description)}");
                if (code is null || state is null) return Html("code/state 없음");
                var parts = state.Split('.', 3);
                if (parts.Length != 3) return Html("state 형식 오류");
                var (mode, userPart, nonce) = (parts[0], parts[1], parts[2]);
                var credential = new SocialCredential(AuthorizationCode: code, RedirectUri: Redirect(req));

                try
                {
                    if (mode == "delete" && Guid.TryParse(userPart, out var userId))
                    {
                        await deletion.DeleteAsync(userId, AuthProvider.Kakao, credential, nonce, null, ct);
                        var u = await db.UserProfiles.AsNoTracking().FirstAsync(x => x.Id == userId, ct);
                        var pending = await db.IdpRevocations.CountAsync(r => r.Provider == AuthProvider.Kakao, ct);
                        return Html($"""
                            <h2>탈퇴 완료</h2>
                            <table>
                            <tr><th>상태</th><td>{u.Status}</td></tr>
                            <tr><th>표시 이름 / 이메일</th><td>{Enc(u.DisplayName ?? "(파기됨)")} / {Enc(u.Email ?? "(파기됨)")}</td></tr>
                            <tr><th>카카오 연결 끊기</th><td>{(pending == 0 ? "완료 (대기열 비어 있음)" : $"대기열 {pending}건 — Worker 가 재시도")}</td></tr>
                            </table>
                            <p>카카오 계정 → 연결된 서비스 관리에서 이 앱이 사라졌는지 확인할 수 있습니다.</p>
                            <p><a href="/dev/kakao/login">다시 로그인 (새 사용자로 가입되어야 함)</a></p>
                            """);
                    }

                    var r = await login.SignInAsync(AuthProvider.Kakao, credential, nonce, "web", null, ct);
                    var identity = await db.UserIdentities.AsNoTracking()
                        .FirstAsync(i => i.UserId == r.User.Id && i.Provider == AuthProvider.Kakao, ct);
                    return Html($"""
                        <h2>카카오 로그인 성공</h2>
                        <table>
                        <tr><th>결과</th><td>{(r.IsNewUser ? "신규 가입" : "기존 사용자 로그인")}</td></tr>
                        <tr><th>마실 사용자 ID</th><td>{r.User.Id}</td></tr>
                        <tr><th>표시 이름</th><td>{Enc(r.User.DisplayName ?? "(없음)")}</td></tr>
                        <tr><th>카카오 회원번호</th><td>{Enc(identity.ProviderSubject)}</td></tr>
                        <tr><th>토큰 aud (client)</th><td>{Mask(identity.ClientId)}</td></tr>
                        <tr><th>약관 동의 필요</th><td>{r.ConsentRequired}</td></tr>
                        <tr><th>Access Token</th><td>{Mask(r.Tokens.AccessToken)} (만료 {r.Tokens.AccessTokenExpiresAt:HH:mm:ss} UTC)</td></tr>
                        <tr><th>Refresh Token</th><td>{Mask(r.Tokens.RefreshToken)}</td></tr>
                        </table>
                        <p>검증 방식은 서버 로그의 <code>OAuth 로그인 Kakao (...)</code> 줄에 나옵니다.</p>
                        <p><a href="/dev/kakao/login">한 번 더 로그인 (기존 사용자로 인식되어야 함)</a></p>
                        <p><a href="/dev/kakao/login?mode=delete&user={r.User.Id}">탈퇴 테스트 — 카카오로 다시 로그인해 본인 확인 후 삭제·연결 끊기</a></p>
                        """);
                }
                catch (AuthFailure f)
                {
                    return Html($"<h2>실패: {Enc(f.Code)}</h2><pre>{Enc(f.Detail)}</pre>");
                }
            })
            .ExcludeFromDescription();
    }

    private static string Redirect(HttpRequest req) => $"{req.Scheme}://{req.Host}{CallbackPath}";

    private static string Enc(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    private static string Mask(string? s) => s is null ? "(없음)" : s.Length <= 10 ? "***" : Enc(s[..6]) + "…" + Enc(s[^4..]);

    private static IResult Html(string body) => Results.Content($$"""
        <!doctype html><meta charset="utf-8"><title>마실 · 카카오 로그인 테스트 (dev)</title>
        <style>body{font-family:system-ui,sans-serif;max-width:720px;margin:40px auto;padding:0 16px;line-height:1.6}
        th{text-align:left;padding-right:16px;color:#555;font-weight:500} td{font-family:ui-monospace,monospace}
        a{color:#1F6F69}</style>
        <p style="color:#888">개발 환경 전용 테스트 페이지</p>
        {{body}}
        """, "text/html; charset=utf-8");
}
