namespace Mashil.Domain;

public sealed class Rating
{
    public long Id { get; set; }
    public required Guid UserId { get; set; }
    public long ToiletId { get; set; }

    /// <summary>1–5 정수 점수.</summary>
    public int Score { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
