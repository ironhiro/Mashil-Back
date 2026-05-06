using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Mashil.Api.Auth;

public static class CurrentUser
{
    /// <summary>
    /// 인증된 요청에서 user id (Guid) 를 꺼낸다. 인증 안된 요청이면 null.
    /// </summary>
    public static Guid? TryGetUserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public static Guid GetUserIdOrThrow(this ClaimsPrincipal principal) =>
        principal.TryGetUserId() ?? throw new UnauthorizedAccessException("user id missing");
}
