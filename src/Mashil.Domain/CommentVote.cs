namespace Mashil.Domain;

/// <summary>
/// 댓글에 대한 도움돼요(+1) / 아니에요(-1). 사용자당 댓글 하나에 한 표 (다시 누르면 바뀌거나 취소).
/// 자기 댓글에는 누를 수 없다. 추천(<see cref="ToiletRecommender"/>)의 "믿을 만한 정보" 신호로 쓴다.
/// </summary>
public sealed class CommentVote
{
    public long CommentId { get; set; }
    public required Guid UserId { get; set; }

    /// <summary>+1 또는 -1.</summary>
    public short Value { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
