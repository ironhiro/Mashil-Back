using System.Security.Claims;
using Mashil.Api.Auth;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterInput input,
            MashilDbContext db,
            IPasswordHasher<UserProfile> hasher,
            JwtTokenService jwt,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Password))
                return Results.BadRequest(new { error = "email/password required" });

            var email = input.Email.Trim().ToLowerInvariant();
            if (await db.UserProfiles.AnyAsync(u => u.Email == email, ct))
                return Results.Conflict(new { error = "email already registered" });

            var user = new UserProfile
            {
                Email = email,
                DisplayName = input.DisplayName,
                PasswordHash = string.Empty,
            };
            user.PasswordHash = hasher.HashPassword(user, input.Password);
            db.UserProfiles.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new AuthResponse(jwt.Issue(user), user.Id, user.Email, user.Role.ToString()));
        })
        .WithSummary("회원가입 + 즉시 JWT 발급")
        .WithDescription("이메일/비밀번호 등록 후 토큰까지 한 번에 반환. 이메일 중복 시 409.")
        .Produces<AuthResponse>()
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/login", async (
            LoginInput input,
            MashilDbContext db,
            IPasswordHasher<UserProfile> hasher,
            JwtTokenService jwt,
            CancellationToken ct) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();
            var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null)
                return Results.Unauthorized();

            var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password);
            if (verify == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            if (verify == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, input.Password);
            }
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new AuthResponse(jwt.Issue(user), user.Id, user.Email, user.Role.ToString()));
        })
        .WithSummary("로그인 + JWT 발급")
        .WithDescription("이메일/비밀번호 검증 후 토큰 반환. 실패 시 401 (이메일 부재/비번 불일치 구분 안 함).")
        .Produces<AuthResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var id = principal.TryGetUserId();
            if (id is null) return Results.Unauthorized();
            var user = await db.UserProfiles.FindAsync([id.Value], ct);
            return user is null
                ? Results.NotFound()
                : Results.Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.Role.ToString()));
        })
        .RequireAuthorization()
        .WithSummary("현재 로그인 사용자 프로필")
        .WithDescription("JWT sub claim 으로 본인 행 조회. 토큰 없으면 401, JWT 는 유효하나 행이 없으면 404 (희박 — 사용자 삭제됐는데 토큰 살아있는 경우).")
        .Produces<MeResponse>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>회원가입 페이로드.</summary>
    /// <param name="Email">이메일 (소문자/trim 적용 후 저장).</param>
    /// <param name="Password">비밀번호 평문 — 서버에서 PasswordHasher 로 해시.</param>
    /// <param name="DisplayName">표시 이름 (선택).</param>
    public sealed record RegisterInput(string Email, string Password, string? DisplayName);

    /// <summary>로그인 페이로드.</summary>
    public sealed record LoginInput(string Email, string Password);

    /// <summary>회원가입/로그인 공통 응답.</summary>
    /// <param name="Token">Bearer JWT — 만료까지 그대로 사용.</param>
    /// <param name="UserId">UserProfile.Id (Guid).</param>
    /// <param name="Email">정규화된 이메일 (소문자).</param>
    /// <param name="Role">UserRole enum (`User` / `Admin`).</param>
    public sealed record AuthResponse(string Token, Guid UserId, string Email, string Role);

    /// <summary>`/me` 응답.</summary>
    public sealed record MeResponse(Guid Id, string Email, string? DisplayName, string Role);
}
