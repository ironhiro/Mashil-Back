namespace Mashil.Domain;

public sealed class Comment
{
    public long Id { get; set; }
    public required Guid UserId { get; set; }
    public long ToiletId { get; set; }
    public required string Text { get; set; }

    /// <summary>관리자 숨김 또는 신고 누적 자동 숨김이면 true (UGC-05).</summary>
    public bool Hidden { get; set; }

    /// <summary>신고 수 (비정규화 캐시). 신고 큐 정렬과 자동 숨김 판정에 쓴다.</summary>
    public int ReportCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
