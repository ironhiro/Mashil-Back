using System.Security.Cryptography;
using System.Text;

namespace Mashil.Api.Auth;

/// <summary>
/// 로그인 없이 매기는 별점의 주인 (2026-11-24 결정: 출시 때는 회원 기능을 숨기고 별점만 누구나).
/// 웹이 처음 열릴 때 만든 무작위 UUID 를 <c>X-Mashil-Device</c> 헤더로 보낸다. 기기 고유 ID(광고 ID, ANDROID_ID)가 아니다.
/// DB 에는 원래 값 대신 SHA-256 해시만 둔다 (DB 가 새도 그 값으로 남의 별점을 고치지 못하게).
/// 같은 기기 키로는 화장실마다 별점 하나 (다시 보내면 고친다). 앱을 지우면 새 키가 된다.
/// </summary>
public static class DeviceKey
{
    public const string Header = "X-Mashil-Device";

    /// <summary>헤더가 UUID 모양이면 그 해시(소문자 hex 64자), 아니면 null.</summary>
    public static string? TryReadHash(HttpRequest request)
    {
        var raw = request.Headers[Header].ToString();
        return Guid.TryParse(raw, out var key) && key != Guid.Empty ? Hash(key) : null;
    }

    public static string Hash(Guid key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(key.ToString("D"))));
}
