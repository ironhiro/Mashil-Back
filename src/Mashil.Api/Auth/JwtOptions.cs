namespace Mashil.Api.Auth;

public sealed class JwtOptions
{
    public required string SigningKey { get; set; }
    public string Issuer { get; set; } = "mashil-api";
    public string Audience { get; set; } = "mashil-web";
    public int ExpiresMinutes { get; set; } = 60 * 24; // 24h
}
