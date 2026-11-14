using System.Security.Claims;
using System.Text;
using Mashil.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mashil.Infrastructure.Identity;

/// <summary>마실 Access JWT 발급 (HS256). 이메일 등 개인정보는 넣지 않는다.</summary>
public sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JwtOptions _o = options.Value;

    public (string Token, DateTime ExpiresAt) Issue(UserProfile user)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_o.ExpiresMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey)), SecurityAlgorithms.HmacSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
