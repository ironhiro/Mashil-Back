namespace Mashil.Domain;

/// <summary>
/// 원본보다 우선하는 필드 값 (DATA-11). 재임포트는 원본만 갱신하고, 표준 값은 매번
/// <c>활성 오버라이드 중 우선순위 최대 ?? 원본</c> 으로 다시 계산한다 — 관리자 수정이 덮어써지지 않음 (G1).
/// </summary>
public sealed class ToiletOverride
{
    public long Id { get; set; }

    public long ToiletId { get; set; }
    public Toilet? Toilet { get; set; }

    /// <summary><see cref="OverrideFields"/> 중 하나.</summary>
    public required string FieldName { get; set; }

    /// <summary>JSON 값 (jsonb). 문자열 필드는 JSON 문자열, location 은 {"lat":..,"lng":..}, null 은 값 비우기.</summary>
    public required string ValueJson { get; set; }

    public OverrideSource Source { get; set; }

    /// <summary>높을수록 우선. 기본은 <see cref="OverrideSource"/> 의 정수값.</summary>
    public int Priority { get; set; }

    public long? ChangeRequestId { get; set; }
    public Guid? CreatedById { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>우선순위 = 정수값. Admin 30 &gt; ChangeRequest 20 &gt; Geocode 10 &gt; ImportedCoordinate 5.</summary>
public enum OverrideSource
{
    /// <summary>예전 데이터(좌표가 있던 표준데이터)에서 가져온 좌표. 지오코딩보다 낮다.</summary>
    ImportedCoordinate = 5,
    Geocode = 10,
    ChangeRequest = 20,
    Admin = 30,
}

/// <summary>오버라이드할 수 있는 필드 이름.</summary>
public static class OverrideFields
{
    public const string Name = "name";
    public const string RoadAddress = "road_address";
    public const string LotAddress = "lot_address";
    public const string Location = "location";
    public const string Phone = "phone";
    public const string OpenHours = "open_hours";
    public const string OpenHoursDetail = "open_hours_detail";
    public const string Status = "status";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Name, RoadAddress, LotAddress, Location, Phone, OpenHours, OpenHoursDetail, Status,
    };
}
