namespace Mashil.Domain;

/// <summary>
/// 사용자의 화장실 추가/수정/삭제 요청. 실제 반영은 관리자 승인 후 별도 트랜잭션.
/// </summary>
public sealed class ChangeRequest
{
    public long Id { get; set; }
    public required Guid RequesterId { get; set; }

    /// <summary>대상 toilet (Add 요청일 때는 null).</summary>
    public long? TargetToiletId { get; set; }

    public ChangeRequestKind Kind { get; set; }

    /// <summary>요청 페이로드 (jsonb). 새 화장실의 필드, 또는 수정할 필드의 diff.</summary>
    public required string PayloadJson { get; set; }

    public ChangeRequestStatus Status { get; set; } = ChangeRequestStatus.Pending;

    public Guid? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ChangeRequestKind
{
    Add = 0,
    Edit = 1,
    Delete = 2,
}

public enum ChangeRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}
