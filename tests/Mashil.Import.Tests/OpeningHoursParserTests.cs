using Mashil.Domain;
using Mashil.Import.Normalization;
using static Mashil.Domain.DayType;

namespace Mashil.Import.Tests;

/// <summary>
/// 회귀 코퍼스 (NFR-12). 요구사항 §6.3 표와 전국파일 상위 패턴(06-data-profile §5)에서 뽑았다.
/// 규칙을 바꿀 때 이 표가 깨지지 않아야 한다.
/// </summary>
public class OpeningHoursParserTests
{
    private static string Describe(HoursOutcome o)
    {
        if (o.Status != ScheduleStatus.Scheduled) return o.Status.ToString();
        string M(int? m) => m is null ? "" : OpenStatusEvaluator.FormatMinute(m.Value);
        var byDay = o.Entries.GroupBy(e => e.Day).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:" + string.Join(",", g.Select(e => e.Closed ? "휴" : $"{M(e.Open)}-{M(e.Close)}")));
        return string.Join(" ", byDay);
    }

    private const string Wk = "Mon:{0} Tue:{0} Wed:{0} Thu:{0} Fri:{0}";
    private static string Week(string slot) => string.Format(Wk, slot) + $" Sat:{slot} Sun:{slot}";

    [Theory]
    // --- 24시간 ---
    [InlineData("상시", null, "AlwaysOpen")]
    [InlineData("정시", "00~24", "AlwaysOpen")]
    [InlineData("정시", "00:00~24:00", "AlwaysOpen")]
    [InlineData("정시", "00:00-24:00", "AlwaysOpen")]
    [InlineData("정시", "00:00~00:00", "AlwaysOpen")]
    [InlineData("정시", "24시간", "AlwaysOpen")]
    [InlineData("정시", "24시간 개방", "AlwaysOpen")]
    [InlineData("정시", "24", "AlwaysOpen")]
    [InlineData("24", null, "AlwaysOpen")]
    [InlineData("상시", "24시간", "AlwaysOpen")]
    [InlineData("정시", "24시", "AlwaysOpen")]
    [InlineData(null, "항시개방", "AlwaysOpen")]
    [InlineData(null, "미개방", "NotOpen")]
    [InlineData("정시", "불규칙", "Irregular")]
    [InlineData("정시", "영업중", "FollowsFacility")]
    [InlineData("정시", "영상테마파크 오픈시 개방", "FollowsFacility")]
    [InlineData("정시", "9", "Unknown")]
    [InlineData(null, "8", "Unknown")]
    [InlineData(null, "연중", "Unknown")]
    // --- 상태 코드 ---
    [InlineData("미개방", "미개방", "NotOpen")]
    [InlineData("미개방", "월~금 09시~18시", "NotOpen")]
    [InlineData("불규칙", null, "Irregular")]
    [InlineData("불규칙", "불규칙", "Irregular")]
    [InlineData("정시", "운영시간 내", "FollowsFacility")]
    [InlineData("정시", "근무시간", "FollowsFacility")]
    [InlineData("정시", "영업시간 내", "FollowsFacility")]
    // --- 정보 없음 ---
    [InlineData("정시", "9시간", "Unknown")]
    [InlineData("정시", "11시간", "Unknown")]
    [InlineData(null, "8시간", "Unknown")]
    [InlineData("정시", "연중무휴", "Unknown")]
    [InlineData("정시", "정시", "Unknown")]
    [InlineData("정시", null, "Unknown")]
    [InlineData(null, "-", "Unknown")]
    [InlineData(null, null, "Unknown")]
    public void Status_only_cases(string? openHours, string? detail, string expected) =>
        Assert.Equal(expected, Describe(OpeningHoursParser.Parse(openHours, detail)));

    public static TheoryData<string?, string?, string> ScheduledCases => new()
    {
        { "정시", "05~24", Week("05:00-24:00") },
        { "정시", "09:00-18:00", Week("09:00-18:00") },
        { "정시", "09:00~18:00", Week("09:00-18:00") },
        { "정시", "09:00∼18:00", Week("09:00-18:00") },
        { "정시", "09:00 ~ 18:00", Week("09:00-18:00") },
        { "정시", "9:00~18:00", Week("09:00-18:00") },
        { "정시", "0900-1800", Week("09:00-18:00") },
        { "정시", "09시~18시", Week("09:00-18:00") },
        { "정시", "09-18시", Week("09:00-18:00") },
        { "정시", "09 ~ 18", Week("09:00-18:00") },
        { "정시", "정시(09:00~18:00)", Week("09:00-18:00") },
        { "정시", "(09:00~18:00)", Week("09:00-18:00") },
        { "정시", "(평일)09:00-18:00+(주말)09:00-18:00", Week("09:00-18:00") },
        { "정시", "08:00-19:00+일요일폐쇄", string.Format(Wk, "08:00-19:00") + " Sat:08:00-19:00 Sun:휴" },
        { "정시", "10:00-18:00+ 월요일휴관", "Mon:휴 Tue:10:00-18:00 Wed:10:00-18:00 Thu:10:00-18:00 Fri:10:00-18:00 Sat:10:00-18:00 Sun:10:00-18:00" },
        { "정시", "월-금06:00~22:00 토-일09:00~18:00 공휴일 휴관", string.Format(Wk, "06:00-22:00") + " Sat:09:00-18:00 Sun:09:00-18:00 Holiday:휴" },
        { "정시", "월-금(05:00-24:00), 토(05:00-17:00), 일 휴무", string.Format(Wk, "05:00-24:00") + " Sat:05:00-17:00 Sun:휴" },
        { "정시", "10:00~22:00, 일 휴무", string.Format(Wk, "10:00-22:00") + " Sat:10:00-22:00 Sun:휴" },
        { "정시", "12:00~18:00+ 월요일, 일요일 휴무", "Mon:휴 Tue:12:00-18:00 Wed:12:00-18:00 Thu:12:00-18:00 Fri:12:00-18:00 Sat:12:00-18:00 Sun:휴" },
        { "정시", "평일 09:00~18:00, 토요일 09:00~13:00, 일요일·공휴일 휴무", string.Format(Wk, "09:00-18:00") + " Sat:09:00-13:00 Sun:휴 Holiday:휴" },
        { "정시", "09:00~12:00, 13:00~18:00", Week("09:00-12:00,13:00-18:00") },
        { "정시", "09:00~18:00 (휴관일: 월요일)", "Mon:휴 Tue:09:00-18:00 Wed:09:00-18:00 Thu:09:00-18:00 Fri:09:00-18:00 Sat:09:00-18:00 Sun:09:00-18:00" },
        { "정시", "22:00~02:00", Week("22:00-02:00") },
        { "정시", "9시30분~18시", Week("09:30-18:00") },
        { "상시", "09:00~22:00", Week("09:00-22:00") }, // 상세를 따르되 Conflict 이슈
        // 실데이터 판정 불가 상위 패턴에서 추가 (2차)
        { "정시", "05:00~익일01:00", Week("05:00-01:00") },
        { "정시", "06시-익01시", Week("06:00-01:00") },
        { "정시", "10:00~23:.00", Week("10:00-23:00") },
        { "정시", "09:00~1800", Week("09:00-18:00") },
        { "정시", "09-18", Week("09:00-18:00") },
        { "정시", "오전10:00~오후6:00", Week("10:00-18:00") },
        // 일요일은 공휴일 (관공서의 공휴일에 관한 규정 제2조): 일요일 언급이 없으면 공휴일 규칙을 따른다
        { "정시", "(평일)08:00-23:00+(토,공휴일)09:00-23:00", string.Format(Wk, "08:00-23:00") + " Sat:09:00-23:00 Sun:09:00-23:00 Holiday:09:00-23:00" },
        { "정시", "평일 09:00~18:00, 토요일 09:00~13:00, 공휴일 휴무", string.Format(Wk, "09:00-18:00") + " Sat:09:00-13:00 Sun:휴 Holiday:휴" },
        // 일요일이 따로 적혀 있으면 그대로, 요일 공통 시간이 있으면 그것을 따른다
        { "정시", "평일 09:00~18:00, 일요일 10:00~16:00, 공휴일 휴무", string.Format(Wk, "09:00-18:00") + " Sun:10:00-16:00 Holiday:휴" },
        { "정시", "09:00~18:00, 공휴일 휴무", Week("09:00-18:00") + " Holiday:휴" },
    };

    [Theory]
    [MemberData(nameof(ScheduledCases))]
    public void Scheduled_cases(string? openHours, string? detail, string expected) =>
        Assert.Equal(expected, Describe(OpeningHoursParser.Parse(openHours, detail)));

    public static TheoryData<string?, string?, string> PartialCases => new()
    {
        { "정시", "(평일) 09:00-18:00", string.Format(Wk, "09:00-18:00") },
        { "정시", "(평일)09:00~18:00", string.Format(Wk, "09:00-18:00") },
        { "정시", "평일 09:00~18:00", string.Format(Wk, "09:00-18:00") },
        { "정시", "월~금 09시~18시", string.Format(Wk, "09:00-18:00") },
        { "정시", "09:00-16:00/월~금", string.Format(Wk, "09:00-16:00") },
        { "정시", "평일9시간(09:00~18:00)", string.Format(Wk, "09:00-18:00") },
    };

    [Theory]
    [MemberData(nameof(PartialCases))]
    public void Partial_schedule_flags_issue(string? openHours, string? detail, string expected)
    {
        var o = OpeningHoursParser.Parse(openHours, detail);
        Assert.Equal(expected, Describe(o));
        Assert.Equal(IssueType.Partial, o.Issue);
    }

    [Theory]
    [InlineData("정시", "9시간", IssueType.DurationOnly)]
    [InlineData("정시", "연중무휴", IssueType.NoHours)]
    [InlineData("정시", null, IssueType.NoHours)]
    [InlineData("미개방", "월~금 09시~18시", IssueType.Conflict)]
    [InlineData("상시", "09:00~22:00", IssueType.Conflict)]
    [InlineData("정시", "알수없는텍스트", IssueType.Unparsed)]
    [InlineData("정시", "25:00~26:00", IssueType.OutOfRange)]
    public void Issues_are_reported(string? openHours, string? detail, IssueType expected) =>
        Assert.Equal(expected, OpeningHoursParser.Parse(openHours, detail).Issue);

    [Theory]
    [InlineData("상시", null)]
    [InlineData("정시", "09:00~18:00")]
    [InlineData("미개방", "미개방")]
    public void Clean_cases_have_no_issue(string? openHours, string? detail) =>
        Assert.Null(OpeningHoursParser.Parse(openHours, detail).Issue);

    [Fact]
    public void Admin_mapping_overrides_rules()
    {
        var canonical = new HoursOutcome(ScheduleStatus.Scheduled, [ScheduleEntry.Slot(Mon, 540, 1080)]).ToJson();
        var mappings = new Dictionary<string, string> { [OpeningHoursParser.MappingKey("정시", "평일9시간")] = canonical };

        var o = OpeningHoursParser.Parse("정시", "  평일9시간 ", mappings);
        Assert.Equal("Mon:09:00-18:00", Describe(o));
        Assert.Null(o.Issue);
    }

    [Fact]
    public void Single_char_day_does_not_match_inside_words()
    {
        // "일반" 의 "일" 을 일요일로 읽으면 안 된다
        Assert.Equal(Week("09:00-18:00"), Describe(OpeningHoursParser.Parse("정시", "09:00~18:00 일반인 이용 가능")));
    }
}
