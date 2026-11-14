namespace Mashil.Domain;

/// <summary>
/// 요일 유형별 개방 시간대 1칸 (DATA-05). 분 단위: 0 = 00:00, 1440 = 24:00.
/// <see cref="IsClosed"/> 면 그 요일은 휴무 (시간 없음).
/// <see cref="CloseMinute"/> &lt;= <see cref="OpenMinute"/> 이면 자정을 넘기는 시간대 (예: 22:00~02:00).
/// </summary>
public sealed class OpeningSchedule
{
    public long Id { get; set; }

    public long ToiletId { get; set; }
    public Toilet? Toilet { get; set; }

    public DayType DayType { get; set; }

    public int? OpenMinute { get; set; }
    public int? CloseMinute { get; set; }
    public bool IsClosed { get; set; }

    public bool IsFullDay => !IsClosed && OpenMinute == 0 && CloseMinute == 1440;
}

/// <summary>요일 유형. 공휴일은 별도 칸 — 규칙이 없으면 그날의 요일 규칙을 따른다.</summary>
public enum DayType
{
    Mon = 1,
    Tue = 2,
    Wed = 3,
    Thu = 4,
    Fri = 5,
    Sat = 6,
    Sun = 7,
    Holiday = 8,
}

/// <summary>개방 시간 판정 상태 (요구사항 §6.3).</summary>
public enum ScheduleStatus
{
    Unknown = 0,
    AlwaysOpen = 1,
    Scheduled = 2,
    /// <summary>개방시간 "미개방".</summary>
    NotOpen = 3,
    /// <summary>개방시간 "불규칙".</summary>
    Irregular = 4,
    /// <summary>"운영시간 내", "근무시간" 등 — 건물 운영시간을 따름.</summary>
    FollowsFacility = 5,
}

/// <summary>화장실 구분 코드 (요구사항 §6.2).</summary>
public enum ToiletKind
{
    Other = 0,
    Public = 1,
    Open = 2,
    Temporary = 3,
    Mobile = 4,
}
