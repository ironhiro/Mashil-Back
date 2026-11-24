namespace Mashil.Domain;

public sealed class Rating
{
    public long Id { get; set; }
    /// <summary>회원이 매긴 별점. 로그인 없이 매긴 별점이면 null (그때는 <see cref="DeviceKeyHash"/>).</summary>
    public Guid? UserId { get; set; }

    /// <summary>로그인 없이 매긴 별점의 기기 키 해시 (SHA-256 hex). 회원 별점이면 null. 둘 중 하나만 있다.</summary>
    public string? DeviceKeyHash { get; set; }
    public long ToiletId { get; set; }

    /// <summary>1–5 정수 점수.</summary>
    public int Score { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
