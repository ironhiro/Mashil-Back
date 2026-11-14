using System.Security.Cryptography;
using System.Text;

namespace Mashil.Infrastructure.Identity;

public static class TokenHash
{
    /// <summary>SHA-256 소문자 hex (64자). Refresh Token·nonce 원문은 저장하지 않는다.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>URL 안전 랜덤 문자열 (base64url, 패딩 없음).</summary>
    public static string Random(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>
/// IdP 토큰(Apple refresh token) 저장용 AES-256-GCM. 형식: nonce(12) | tag(16) | ciphertext.
/// 키가 없으면 <see cref="IsAvailable"/> = false 이고 저장하지 않는다.
/// </summary>
public sealed class SecretBox
{
    private readonly byte[]? _key;

    public SecretBox(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) return;
        var key = Convert.FromBase64String(base64Key);
        if (key.Length != 32) throw new InvalidOperationException("OAuth:TokenEncryptionKey 는 base64 로 인코딩한 32바이트여야 합니다.");
        _key = key;
    }

    public bool IsAvailable => _key is not null;

    public byte[]? Seal(string? plaintext)
    {
        if (_key is null || string.IsNullOrEmpty(plaintext)) return null;
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[12 + 16 + data.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(output.AsSpan(0, 12), data, output.AsSpan(28), output.AsSpan(12, 16));
        return output;
    }

    public string? Open(byte[]? sealedBytes)
    {
        if (_key is null || sealedBytes is null || sealedBytes.Length < 28) return null;
        var plain = new byte[sealedBytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
