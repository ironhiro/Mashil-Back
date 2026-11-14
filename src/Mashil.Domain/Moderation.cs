namespace Mashil.Domain;

public enum ReportReason
{
    Spam = 0,
    Abuse = 1,
    False = 2,
    Other = 3,
}

public enum ReportStatus
{
    Open = 0,
    Actioned = 1,
    Dismissed = 2,
}

/// <summary>댓글 신고. 같은 댓글은 한 사용자가 한 번만 (UK comment_id + reporter_id, UGC-03).</summary>
public sealed class CommentReport
{
    public long Id { get; set; }
    public long CommentId { get; set; }
    public required Guid ReporterId { get; set; }
    public ReportReason Reason { get; set; }
    public string? Detail { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

/// <summary>사용자 차단. 차단한 사용자의 댓글을 본인 화면에서 숨긴다 (UGC-04).</summary>
public sealed class UserBlock
{
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
