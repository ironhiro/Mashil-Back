namespace Mashil.Domain;

public sealed class Comment
{
    public long Id { get; set; }
    public required Guid UserId { get; set; }
    public long ToiletId { get; set; }
    public required string Text { get; set; }

    /// <summary>관리자가 신고된 댓글을 숨길 때 true.</summary>
    public bool Hidden { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
