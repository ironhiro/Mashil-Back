namespace Mashil.Domain;

/// <summary>
/// 자동 정규화가 실패했거나 의심스러운 값 (DATA-12). 관리자 검수 큐.
/// 재정규화할 때 해당 화장실의 Open 이슈는 다시 계산되고, Resolved/Ignored 는 남는다.
/// </summary>
public sealed class NormalizationIssue
{
    public long Id { get; set; }

    public long ToiletId { get; set; }
    public Toilet? Toilet { get; set; }

    public required string FieldName { get; set; }
    public IssueType Type { get; set; }
    public IssueSeverity Severity { get; set; }

    /// <summary>문제가 된 원본 값 (같은 값 일괄 처리용으로 인덱스).</summary>
    public string? RawValue { get; set; }

    public string? Note { get; set; }

    public IssueStatus Status { get; set; } = IssueStatus.Open;

    public long? ResolvedByMappingId { get; set; }
    public Guid? ResolvedById { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum IssueType
{
    Unparsed = 0,
    /// <summary>일부 요일만 시간이 있음.</summary>
    Partial = 1,
    /// <summary>"9시간"처럼 길이만 있음.</summary>
    DurationOnly = 2,
    /// <summary>"연중무휴"처럼 시간 정보 없음.</summary>
    NoHours = 3,
    /// <summary>개방시간과 상세가 서로 맞지 않음 (예: 미개방인데 시간대가 있음).</summary>
    Conflict = 4,
    OutOfRange = 5,
    UnknownCode = 6,
    InvalidPhone = 7,
    GeocodeNoMatch = 8,
    /// <summary>지오코딩 결과의 시도가 개방자치단체코드와 다름 (Info).</summary>
    RegionMismatch = 9,
    /// <summary>지오코딩 후보가 여러 개.</summary>
    Ambiguous = 10,
}

public enum IssueSeverity { Info = 0, Warning = 1, Error = 2 }

public enum IssueStatus { Open = 0, Resolved = 1, Ignored = 2 }

/// <summary>이슈 필드 이름.</summary>
public static class IssueFields
{
    public const string OpeningHours = "opening_hours";
    public const string Kind = "kind";
    public const string Phone = "phone";
    public const string Facilities = "facilities";
    public const string DataReferenceDate = "data_reference_date";
    public const string Geocode = "geocode";
}
