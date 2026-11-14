namespace Mashil.Domain;

/// <summary>
/// 관리자가 가르쳐 준 "원본 문자열 → 표준 값" 규칙 (DATA-13). 자동 규칙보다 우선한다.
/// 예: field = opening_hours, raw_key = "정시|평일9시간(09:00~18:00)", canonical = 스케줄 JSON.
/// </summary>
public sealed class ValueMapping
{
    public long Id { get; set; }

    public required string FieldName { get; set; }

    /// <summary>정규화 키 (공백 정리한 원본). 필드별로 키를 만드는 규칙은 정규화기가 정한다.</summary>
    public required string RawKey { get; set; }

    public required string CanonicalJson { get; set; }

    public bool IsActive { get; set; } = true;
    public Guid? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
