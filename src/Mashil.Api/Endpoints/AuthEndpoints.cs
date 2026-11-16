using System.Security.Claims;
using Mashil.Api.Auth;
using Mashil.Domain;
using Mashil.Infrastructure;
using Mashil.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

/// <summary>
/// OAuth 로그인·토큰 갱신·동의·계정 삭제 (USR-01~05, SD-08~11).
/// 실패 응답은 <c>{ "error": "&lt;code&gt;" }</c> — 상세 원인은 서버 로그에만 남긴다.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/providers", (IdentityProviderRegistry providers) =>
                Results.Ok(new { providers = providers.EnabledProviders.Select(p => p.ToString().ToLowerInvariant()) }))
            .WithSummary("켜져 있는 로그인 제공자")
            .WithDescription("서버 설정에 키가 들어간 제공자만. 앱은 플랫폼(iOS 면 apple 포함)과 교집합으로 버튼을 그린다.");

        group.MapGet("/nonce", async (NonceService nonces, CancellationToken ct) =>
            {
                var (nonce, expiresAt) = await nonces.IssueAsync(ct);
                return Results.Ok(new NonceResponse(nonce, expiresAt));
            })
            .WithSummary("1회용 nonce 발급 (5분)")
            .WithDescription("IdP 로그인 요청에 넣고, 같은 값을 `POST /oauth/{provider}` 의 `nonce` 로 보낸다. Apple 은 SHA-256(hex) 을 요청에 넣어도 된다.")
            .Produces<NonceResponse>();

        group.MapPost("/oauth/{provider}", async (
                string provider,
                OAuthLoginInput input,
                OAuthLoginService login,
                ILoggerFactory logs,
                CancellationToken ct) =>
            {
                if (!TryParseProvider(provider, out var p)) return Error("provider_unknown", 404);
                try
                {
                    var r = await login.SignInAsync(p, input.ToCredential(), Blank(input.Nonce), input.Device, input.DisplayName, ct);
                    return Results.Ok(TokenResponse.From(r.Tokens, r.User, r.IsNewUser, r.ConsentRequired));
                }
                catch (AuthFailure f) { return Fail(f, logs); }
            })
            .WithSummary("소셜 로그인 = 자동 가입 (SD-09)")
            .WithDescription("""
                provider = `kakao` / `google` / `apple`. 서버가 IdP 공개키(JWKS)나 토큰 정보 API 로 직접 검증한다.
                - kakao: `idToken`(OIDC, 권장) 또는 `accessToken`. 둘 다 보내면 accessToken 은 탈퇴 시 연결 끊기에만 쓴다. 웹은 `authorizationCode` + `redirectUri`
                - google: `idToken`
                - apple: `idToken` (+ `authorizationCode`: 탈퇴 시 revoke 용), 첫 로그인 때만 오는 이름은 `displayName` 으로
                `consentRequired = true` 면 약관 동의 화면을 띄우고 `POST /consents` 를 호출한다.
                오류: 400 invalid_request / nonce_required, 401 invalid_credential / nonce_invalid, 404 provider_disabled, 503 idp_unavailable
                """)
            .Produces<TokenResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/refresh", async (RefreshInput input, RefreshTokenService refresh, ILoggerFactory logs, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(input.RefreshToken)) return Error("invalid_request", 400);
                try
                {
                    var (tokens, user) = await refresh.RotateAsync(input.RefreshToken, ct);
                    return Results.Ok(TokenResponse.From(tokens, user, isNewUser: false, consentRequired: null));
                }
                catch (AuthFailure f) { return Fail(f, logs); }
            })
            .WithSummary("토큰 갱신 (Refresh 회전, SD-11)")
            .WithDescription("쓴 refresh token 은 즉시 폐기되고 새 쌍을 준다. 이미 쓴 토큰을 다시 보내면 그 기기 세션 전체를 끊는다 (401 reauth_required). **갱신 요청은 한 번에 하나만** 보낼 것.")
            .Produces<TokenResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", async (RefreshInput input, RefreshTokenService refresh, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(input.RefreshToken)) await refresh.RevokeFamilyAsync(input.RefreshToken, ct);
                return Results.NoContent();
            })
            .WithSummary("로그아웃")
            .WithDescription("이 기기의 refresh token 을 폐기. IdP 세션 로그아웃(SDK signOut)은 클라이언트가 한다.")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/me", async (ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
            {
                var me = await LoadMeAsync(db, principal.GetUserIdOrThrow(), ct);
                return me is null ? Results.NotFound() : Results.Ok(me);
            })
            .RequireAuthorization()
            .WithSummary("내 프로필")
            .Produces<MeResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPatch("/me", async (UpdateMeInput input, ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
            {
                var name = OAuthLoginService.CleanDisplayName(input.DisplayName);
                if (name is null) return Error("display_name_required", 400);
                var userId = principal.GetUserIdOrThrow();
                await db.UserProfiles.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, name), ct);
                return Results.Ok(await LoadMeAsync(db, userId, ct));
            })
            .RequireAuthorization()
            .WithSummary("표시 이름 변경")
            .WithDescription($"앞뒤·중복 공백 정리 후 최대 {OAuthLoginService.MaxDisplayNameLength}자.")
            .Produces<MeResponse>();

        group.MapGet("/consents", async (ClaimsPrincipal principal, ConsentService consents, CancellationToken ct) =>
            {
                var states = await consents.StatusAsync(principal.GetUserIdOrThrow(), ct);
                return Results.Ok(new ConsentStatusResponse(states.Any(s => !s.Agreed),
                    states.Select(s => new ConsentItem(s.DocType, s.Version, s.Agreed)).ToList()));
            })
            .RequireAuthorization()
            .WithSummary("약관 동의 상태")
            .Produces<ConsentStatusResponse>();

        group.MapPost("/consents", async (ConsentInput input, ClaimsPrincipal principal, ConsentService consents, CancellationToken ct) =>
            {
                if (input.Items is not { Count: > 0 }) return Error("invalid_request", 400);
                var ok = await consents.AgreeAsync(principal.GetUserIdOrThrow(),
                    input.Items.Select(i => (i.DocType, i.Version)).ToList(), ct);
                return ok ? Results.NoContent() : Error("consent_version_mismatch", 409);
            })
            .RequireAuthorization()
            .WithSummary("약관 동의 저장")
            .WithDescription("현재 버전(`GET /consents`)에만 동의할 수 있다. 다른 버전이면 409 consent_version_mismatch — 화면을 새로 받아 다시 보여줄 것.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);

        // 탈퇴는 POST: 재인증 자격 증명을 본문에 실어 보내는데, DELETE 본문은 HTTP 에서 의미가 정해져 있지 않아
        // 프록시·게이트웨이가 버릴 수 있다 (RFC 9110 §9.3.5). DELETE 는 이전 앱 버전을 위해 당분간 같이 둔다.
        group.MapPost("/me/withdraw", DeleteAccountAsync)
            .RequireAuthorization()
            .WithSummary("계정 삭제 (USR-03, SD-08)")
            .WithDescription("""
                가입한 IdP 로 다시 로그인해 받은 자격 증명을 보낸다 (`provider` + `idToken`/`accessToken`/`authorizationCode`, `nonce`).
                개인정보(이메일·표시 이름·소셜 연결·refresh token·차단 목록)를 즉시 파기하고, 댓글·평점은 "탈퇴한 사용자"로 남긴다.
                IdP 연결 해제(Kakao unlink, Apple revoke)는 바로 시도하고, 실패하면 Worker 가 재시도한다 — 탈퇴는 막지 않는다.
                오류: 400 reauth_required, 403 identity_mismatch (다른 계정으로 재인증), 401 invalid_credential
                """)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("/me", DeleteAccountAsync)
            .RequireAuthorization()
            .WithSummary("계정 삭제 (예전 경로)")
            .WithDescription("`POST /api/auth/me/withdraw` 와 같다. 본문이 있는 DELETE 라 중간 장비가 본문을 버릴 수 있어 새 경로를 쓴다. 이전 앱이 사라지면 지운다.")
            .Produces(StatusCodes.Status204NoContent);

        MapLegacyPasswordEndpoints(group);
    }

    /// <summary>레거시 이메일·비밀번호 — <c>Auth:LegacyPasswordLogin = true</c> 일 때만 (기본 꺼짐, 개발 설정에서만 켬).</summary>
    private static void MapLegacyPasswordEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/register", async (
                LegacyRegisterInput input, MashilDbContext db, IPasswordHasher<UserProfile> hasher,
                RefreshTokenService refresh, ConsentService consents, IConfiguration config, CancellationToken ct) =>
            {
                if (!LegacyEnabled(config)) return Results.NotFound();
                if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password))
                    return Error("invalid_request", 400);
                var email = input.Email.Trim().ToLowerInvariant();
                if (await db.UserProfiles.AnyAsync(u => u.Email == email && u.Status == UserStatus.Active, ct))
                    return Error("email_taken", 409);

                var user = new UserProfile { Email = email, DisplayName = OAuthLoginService.CleanDisplayName(input.DisplayName) };
                user.PasswordHash = hasher.HashPassword(user, input.Password);
                db.UserProfiles.Add(user);
                await db.SaveChangesAsync(ct);
                var tokens = await refresh.IssueAsync(user, "web", ct);
                return Results.Ok(TokenResponse.From(tokens, user, true, await consents.IsRequiredAsync(user.Id, ct)));
            })
            .WithSummary("[레거시·개발 전용] 이메일 회원가입")
            .Produces<TokenResponse>();

        group.MapPost("/login", async (
                LegacyLoginInput input, MashilDbContext db, IPasswordHasher<UserProfile> hasher,
                RefreshTokenService refresh, ConsentService consents, IConfiguration config, CancellationToken ct) =>
            {
                if (!LegacyEnabled(config)) return Results.NotFound();
                var email = input.Email?.Trim().ToLowerInvariant();
                var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Email == email && u.Status == UserStatus.Active, ct);
                if (user?.PasswordHash is null || input.Password is null) return Results.Unauthorized();
                var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password);
                if (verify == PasswordVerificationResult.Failed) return Results.Unauthorized();
                if (verify == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, input.Password);
                user.LastLoginAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                var tokens = await refresh.IssueAsync(user, "web", ct);
                return Results.Ok(TokenResponse.From(tokens, user, false, await consents.IsRequiredAsync(user.Id, ct)));
            })
            .WithSummary("[레거시·개발 전용] 이메일 로그인")
            .Produces<TokenResponse>();
    }

    private static bool LegacyEnabled(IConfiguration config) => config.GetValue<bool>("Auth:LegacyPasswordLogin");

    private static async Task<MeResponse?> LoadMeAsync(MashilDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;
        var providers = await db.UserIdentities.Where(i => i.UserId == userId)
            .Select(i => i.Provider).ToListAsync(ct);
        return new MeResponse(user.Id, user.Email, user.DisplayName, user.Role.ToString(),
            providers.Select(p => p.ToString().ToLowerInvariant()).ToList(), user.CreatedAt);
    }

    private static bool TryParseProvider(string value, out AuthProvider provider) =>
        Enum.TryParse(value, ignoreCase: true, out provider) && Enum.IsDefined(provider) && !int.TryParse(value, out _);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>탈퇴 처리. POST /me/withdraw 와 (예전) DELETE /me 가 같이 쓴다.</summary>
    private static async Task<IResult> DeleteAccountAsync(
        [FromBody] DeleteAccountInput input,
        ClaimsPrincipal principal,
        AccountDeletionService deletion,
        IPasswordHasher<UserProfile> hasher,
        IConfiguration config,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        AuthProvider? provider = null;
        if (input.Provider is not null)
        {
            if (!TryParseProvider(input.Provider, out var p)) return Error("provider_unknown", 404);
            provider = p;
        }
        Func<UserProfile, bool>? legacy = LegacyEnabled(config) && !string.IsNullOrEmpty(input.Password)
            ? u => u.PasswordHash is not null && hasher.VerifyHashedPassword(u, u.PasswordHash, input.Password) != PasswordVerificationResult.Failed
            : null;
        try
        {
            await deletion.DeleteAsync(principal.GetUserIdOrThrow(), provider,
                provider is null ? null : input.ToCredential(), Blank(input.Nonce), legacy, ct);
            return Results.NoContent();
        }
        catch (AuthFailure f) { return Fail(f, logs); }
    }

    internal static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);

    private static IResult Fail(AuthFailure f, ILoggerFactory logs)
    {
        logs.CreateLogger("Mashil.Auth").LogInformation("인증 실패 {Code}: {Detail}", f.Code, f.Detail);
        return Error(f.Code, f.Status);
    }

    public sealed record NonceResponse(string Nonce, DateTime ExpiresAt);

    /// <summary>소셜 로그인 요청.</summary>
    /// <param name="IdToken">OIDC ID Token (kakao·google·apple).</param>
    /// <param name="AccessToken">Kakao Access Token.</param>
    /// <param name="AuthorizationCode">Kakao 웹 인가 코드 / Apple 인가 코드.</param>
    /// <param name="RedirectUri">Kakao 웹 인가 코드를 받은 redirect_uri.</param>
    /// <param name="Nonce">`GET /nonce` 원문.</param>
    /// <param name="Device">`ios` / `android` / `web`.</param>
    /// <param name="DisplayName">IdP 가 닉네임을 주지 않을 때(Apple) 기본 표시 이름.</param>
    public sealed record OAuthLoginInput(
        string? IdToken, string? AccessToken, string? AuthorizationCode, string? RedirectUri,
        string? Nonce, string? Device, string? DisplayName)
    {
        public SocialCredential ToCredential() => new(IdToken, AccessToken, AuthorizationCode, RedirectUri);
    }

    public sealed record RefreshInput(string? RefreshToken);

    public sealed record UpdateMeInput(string? DisplayName);

    public sealed record ConsentInput(IReadOnlyList<ConsentItemInput>? Items);
    public sealed record ConsentItemInput(ConsentDocType DocType, string Version);
    public sealed record ConsentItem(ConsentDocType DocType, string Version, bool Agreed);
    public sealed record ConsentStatusResponse(bool Required, IReadOnlyList<ConsentItem> Documents);

    /// <summary>계정 삭제 재인증. 레거시 이메일 계정은 `password`.</summary>
    public sealed record DeleteAccountInput(
        string? Provider, string? IdToken, string? AccessToken, string? AuthorizationCode, string? RedirectUri,
        string? Nonce, string? Password)
    {
        public SocialCredential ToCredential() => new(IdToken, AccessToken, AuthorizationCode, RedirectUri);
    }

    public sealed record LegacyRegisterInput(string? Email, string? Password, string? DisplayName);
    public sealed record LegacyLoginInput(string? Email, string? Password);

    /// <summary>로그인·갱신 응답. `token` 은 `accessToken` 과 같은 값 (기존 웹 호환).</summary>
    public sealed record TokenResponse(
        string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt,
        string Token, Guid UserId, string? DisplayName, string Role, bool IsNewUser, bool? ConsentRequired)
    {
        public static TokenResponse From(TokenPair t, UserProfile u, bool isNewUser, bool? consentRequired) =>
            new(t.AccessToken, t.AccessTokenExpiresAt, t.RefreshToken, t.RefreshTokenExpiresAt,
                t.AccessToken, u.Id, u.DisplayName, u.Role.ToString(), isNewUser, consentRequired);
    }

    public sealed record MeResponse(
        Guid Id, string? Email, string? DisplayName, string Role, IReadOnlyList<string> Providers, DateTime CreatedAt);
}
