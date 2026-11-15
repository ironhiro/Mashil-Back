using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mashil.Domain;

namespace Mashil.Import.Normalization;

/// <summary>요일 하나의 결과. 휴무면 시간 없음.</summary>
public sealed record ScheduleEntry(DayType Day, int? Open, int? Close, bool Closed)
{
    public static ScheduleEntry Slot(DayType d, int open, int close) => new(d, open, close, false);
    public static ScheduleEntry ClosedOn(DayType d) => new(d, null, null, true);
}

/// <summary>개방시간 파싱 결과.</summary>
public sealed record HoursOutcome(ScheduleStatus Status, IReadOnlyList<ScheduleEntry> Entries, IssueType? Issue = null, IssueSeverity Severity = IssueSeverity.Warning, string? Note = null)
{
    public static HoursOutcome Unknown(IssueType? issue = null, string? note = null) => new(ScheduleStatus.Unknown, [], issue, IssueSeverity.Warning, note);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>매핑 사전(<see cref="ValueMapping"/>)에 저장하는 형식.</summary>
    public string ToJson() => JsonSerializer.Serialize(new { Status, Entries }, Json);

    public static HoursOutcome FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Json) ?? throw new FormatException("빈 매핑");
        return new HoursOutcome(dto.Status, dto.Entries ?? []);
    }

    private sealed record Dto(ScheduleStatus Status, List<ScheduleEntry>? Entries);
}

/// <summary>
/// LOCALDATA <c>개방시간</c> + <c>개방시간상세</c> 자유 텍스트를 요일별 시간대로 바꾼다 (DATA-05).
/// 순수 함수 — DB 없음. 규칙은 06-data-profile §5 의 실데이터 패턴에서 뽑았다.
/// <para>판정 순서: 관리자 매핑 사전 → 개방시간 코드(상시/미개방/불규칙) → 상세 텍스트 토큰 분석.</para>
/// </summary>
public static partial class OpeningHoursParser
{
    public static readonly DayType[] Week = [DayType.Mon, DayType.Tue, DayType.Wed, DayType.Thu, DayType.Fri, DayType.Sat, DayType.Sun];
    private static readonly DayType[] Weekdays = [DayType.Mon, DayType.Tue, DayType.Wed, DayType.Thu, DayType.Fri];
    private static readonly DayType[] Weekend = [DayType.Sat, DayType.Sun];

    /// <summary>매핑 사전 키: 공백을 정리한 "개방시간|개방시간상세".</summary>
    public static string MappingKey(string? openHours, string? detail) =>
        $"{Squash(openHours)}|{Squash(detail)}";

    public static HoursOutcome Parse(string? openHours, string? detail, IReadOnlyDictionary<string, string>? mappings = null)
    {
        if (mappings is not null && mappings.TryGetValue(MappingKey(openHours, detail), out var mapped))
            return HoursOutcome.FromJson(mapped);

        var code = Squash(openHours);
        var parsedDetail = ParseDetail(detail);

        switch (code)
        {
            case "상시":
                if (parsedDetail.Status == ScheduleStatus.Scheduled)
                    return parsedDetail with { Issue = parsedDetail.Issue ?? IssueType.Conflict, Severity = IssueSeverity.Info, Note = "개방시간은 상시인데 상세에 시간대가 있어 상세를 따름" };
                return new HoursOutcome(ScheduleStatus.AlwaysOpen, []);

            case "미개방":
                return new HoursOutcome(ScheduleStatus.NotOpen, [],
                    parsedDetail.Status is ScheduleStatus.Scheduled or ScheduleStatus.AlwaysOpen ? IssueType.Conflict : null,
                    IssueSeverity.Warning, parsedDetail.Status == ScheduleStatus.Scheduled ? "미개방인데 상세에 시간대가 있음" : null);

            case "불규칙":
                return new HoursOutcome(ScheduleStatus.Irregular, []);
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            // "24" 를 개방시간 칸에 적은 경우 등은 코드 자체를 상세처럼 읽어 본다
            if (code.Length > 0 && code is not "정시") return ParseDetail(code);
            return code == "정시" ? HoursOutcome.Unknown(IssueType.NoHours, "정시인데 상세 시간 없음") : HoursOutcome.Unknown();
        }
        return parsedDetail;
    }

    // ---------------------------------------------------------------- 상세 텍스트

    internal static HoursOutcome ParseDetail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return HoursOutcome.Unknown();
        var text = Canonicalize(raw);
        if (text is "" or "-" or "정시" or "정시정시" or "x" or "X" or "연중" or "기타") return HoursOutcome.Unknown(IssueType.NoHours);

        if (ExactAlwaysRe().IsMatch(text)) return new HoursOutcome(ScheduleStatus.AlwaysOpen, []);
        if (text is "미개방" or "개방안함") return new HoursOutcome(ScheduleStatus.NotOpen, []);
        if (text == "불규칙") return new HoursOutcome(ScheduleStatus.Irregular, []);
        // 숫자만 있는 "9", "10" — 시간 길이로 본다 (24 는 위에서 24시간 처리)
        if (BareNumberRe().IsMatch(text)) return HoursOutcome.Unknown(IssueType.DurationOnly, "숫자만 있음 (운영 시간 길이로 추정)");

        var baseSlots = new List<(int, int)>();
        var daySlots = new Dictionary<DayType, List<(int, int)>>();
        var closed = new HashSet<DayType>();
        var hadInvalid = false;

        var tokens = TokenRe().Matches(text).ToList();
        HashSet<DayType>? pending = null;
        Match? prev = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Groups["days"].Success)
            {
                var days = DaysOf(t.Groups["days"].Value);
                var joined = prev is not null && prev.Groups["days"].Success && Between(text, prev, t).Trim(' ', ',', '·', '.', '/') == "";
                pending = joined && pending is not null ? [.. pending, .. days] : [.. days];
            }
            else if (t.Groups["range"].Success)
            {
                if (!TryRange(t, out var range)) { hadInvalid = true; prev = t; continue; }

                var target = pending;
                // "09:00-16:00/월~금", "09:00~18:00(평일)" 처럼 요일이 뒤에 오는 경우
                if (target is null && i + 1 < tokens.Count && tokens[i + 1].Groups["days"].Success
                    && TrailingDaysGlueRe().IsMatch(Between(text, t, tokens[i + 1])))
                {
                    target = [.. DaysOf(tokens[i + 1].Groups["days"].Value)];
                    i++; // 요일 토큰 소비
                    t = tokens[i];
                }

                if (target is null) baseSlots.Add(range);
                else foreach (var d in target) (daySlots.TryGetValue(d, out var l) ? l : daySlots[d] = []).Add(range);
            }
            else if (t.Groups["closed"].Success)
            {
                if (prev is not null && prev.Groups["days"].Success && pending is not null)
                    closed.UnionWith(pending);
                // "휴관일: 월요일", "휴무(토,일)" — 요일이 뒤에 오는 경우
                else if (i + 1 < tokens.Count && tokens[i + 1].Groups["days"].Success
                         && ClosedThenDaysGlueRe().IsMatch(Between(text, t, tokens[i + 1])))
                {
                    var j = i + 1;
                    closed.UnionWith(DaysOf(tokens[j].Groups["days"].Value));
                    while (j + 1 < tokens.Count && tokens[j + 1].Groups["days"].Success
                           && Between(text, tokens[j], tokens[j + 1]).Trim(' ', ',', '·', '.', '/') == "")
                        closed.UnionWith(DaysOf(tokens[++j].Groups["days"].Value));
                    i = j;
                    t = tokens[i];
                }
                // 요일 없는 "휴관" 은 정보로 쓰지 않음
                pending = null;
            }
            prev = t;
        }

        // 평일(월~금) 시간만 있고 토·일 얘기가 전혀 없는지. 아래 공휴일 규칙이 일요일을 채우기 전에 본다.
        var weekdayOnly = baseSlots.Count == 0 && Weekdays.All(daySlots.ContainsKey)
                          && daySlots.Keys.All(d => Weekdays.Contains(d) || d == DayType.Holiday) && !Weekend.Any(closed.Contains);

        // 「관공서의 공휴일에 관한 규정」 제2조 1호: 공휴일에는 일요일이 들어간다. 그래서 일요일 얘기가 따로 없고
        // 요일 공통 시간도 없는데 공휴일 규칙만 있으면, 일요일은 공휴일 규칙을 따른다.
        // 예: "(평일)08:00-23:00+(토,공휴일)09:00-23:00" → 일요일 09:00-23:00, "평일 09~18, 공휴일 휴무" → 일요일 휴무
        if (baseSlots.Count == 0 && !closed.Contains(DayType.Sun) && !daySlots.ContainsKey(DayType.Sun))
        {
            if (closed.Contains(DayType.Holiday)) closed.Add(DayType.Sun);
            else if (daySlots.TryGetValue(DayType.Holiday, out var holidaySlots)) daySlots[DayType.Sun] = [.. holidaySlots];
        }

        // 평일 시간만 있으면 주말은 휴무로 본다 (관공서·회사 건물 화장실이 대부분 이렇다). 예: "평일 09:00~18:00", "월~금 09시~18시".
        // 일요일은 위에서 공휴일 규칙을 따랐으면 그대로 둔다. 공휴일 자체는 정하지 않는다 (적힌 대로, 없으면 그 요일 시간).
        if (weekdayOnly)
        {
            closed.Add(DayType.Sat);
            if (!daySlots.ContainsKey(DayType.Sun)) closed.Add(DayType.Sun);
        }

        var entries = new List<ScheduleEntry>();
        foreach (var d in Week.Append(DayType.Holiday))
        {
            if (closed.Contains(d)) { entries.Add(ScheduleEntry.ClosedOn(d)); continue; }
            if (daySlots.TryGetValue(d, out var slots)) { entries.AddRange(slots.Distinct().Select(s => ScheduleEntry.Slot(d, s.Item1, s.Item2))); continue; }
            if (d != DayType.Holiday && baseSlots.Count > 0)
                entries.AddRange(baseSlots.Distinct().Select(s => ScheduleEntry.Slot(d, s.Item1, s.Item2)));
        }

        if (entries.Count == 0)
        {
            if (FacilityHoursRe().IsMatch(text)) return new HoursOutcome(ScheduleStatus.FollowsFacility, []);
            if (DurationRe().IsMatch(text)) return HoursOutcome.Unknown(IssueType.DurationOnly, "운영 시간 길이만 있음");
            if (NoBreakRe().IsMatch(text)) return HoursOutcome.Unknown(IssueType.NoHours, "연중무휴 — 시간 정보 없음");
            return HoursOutcome.Unknown(hadInvalid ? IssueType.OutOfRange : IssueType.Unparsed);
        }

        var weekEntries = entries.Where(e => e.Day != DayType.Holiday).ToList();
        var coveredDays = weekEntries.Select(e => e.Day).Distinct().Count();
        var allFullDay = coveredDays == 7 && weekEntries.All(e => !e.Closed && e.Open == 0 && e.Close == 1440)
                         && !entries.Any(e => e.Day == DayType.Holiday && e.Closed);
        if (allFullDay) return new HoursOutcome(ScheduleStatus.AlwaysOpen, []);

        IssueType? issue = coveredDays < 7 ? IssueType.Partial : hadInvalid ? IssueType.OutOfRange : null;
        return new HoursOutcome(ScheduleStatus.Scheduled, entries, issue, IssueSeverity.Info,
            issue == IssueType.Partial ? $"요일 {coveredDays}/7 만 시간 정보 있음" : null);
    }

    /// <summary>표기를 통일한다: 물결·대시, 전각, "9시30분", "0900-1800", "05~24", "24시간" 등.</summary>
    internal static string Canonicalize(string raw)
    {
        var s = raw.Normalize(NormalizationForm.FormKC);
        s = TildeRe().Replace(s, "~");
        s = DashRe().Replace(s, "-");
        s = s.Replace('：', ':').Replace(":.", ":").Replace(".:", ":");                                        // 23:.00 오타
        s = NextDayRe().Replace(s, "");                                                                       // 익일01:00, 익01시 → 자정 넘김
        s = HourMinuteKoreanRe().Replace(s, m => $"{m.Groups[1].Value}:{m.Groups[2].Value.PadLeft(2, '0')}");   // 9시30분 → 9:30
        s = HourKoreanRe().Replace(s, "$1:00");                                                              // 18시 → 18:00 (시간 제외)
        s = AmPmRe().Replace(s, m =>                                                                          // 오후6:00 → 18:00
        {
            var h = int.Parse(m.Groups["h"].Value);
            if (m.Groups["ap"].Value == "오후" && h < 12) h += 12;
            if (m.Groups["ap"].Value == "오전" && h == 12) h = 0;
            return $"{h}:{m.Groups["m"].Value}";
        });
        s = HhmmRangeRe().Replace(s, "$1:$2~$3:$4");                                                          // 0900-1800
        s = TimeToHhmmRe().Replace(s, "$1~$2:$3");                                                            // 09:00~1800
        s = TimeRangeRe().Replace(s, "$1~$2");                                                                // 09:00 - 18:00
        s = HourToTimeRangeRe().Replace(s, "$1:00~$2");                                                       // 09-18:00
        s = TimeToHourRangeRe().Replace(s, "$1~$2:00");                                                       // 09:00~18
        s = HourOnlyRangeRe().Replace(s, "$1:00~$2:00");                                                      // 05~24
        s = HourOnlyDashRangeRe().Replace(s, m =>                                                             // 09-18 (24 이하만)
            int.Parse(m.Groups[1].Value) <= 24 && int.Parse(m.Groups[2].Value) <= 24 ? $"{m.Groups[1].Value}:00~{m.Groups[2].Value}:00" : m.Value);
        s = AllDayWordsRe().Replace(s, "00:00~24:00");                                                        // 24시간, 상시
        return Squash(s);
    }

    private static bool TryRange(Match t, out (int, int) range)
    {
        int oh = int.Parse(t.Groups["oh"].Value), om = int.Parse(t.Groups["om"].Value);
        int ch = int.Parse(t.Groups["ch"].Value), cm = int.Parse(t.Groups["cm"].Value);
        range = default;
        if (oh > 24 || ch > 24 || om > 59 || cm > 59 || (oh == 24 && om > 0) || (ch == 24 && cm > 0)) return false;
        var open = oh * 60 + om;
        var close = ch * 60 + cm;
        if (open == 1440) open = 0;
        if (open == 0 && close == 0) close = 1440;      // 00:00~00:00
        range = (open, close);
        return true;
    }

    private static IEnumerable<DayType> DaysOf(string spec)
    {
        spec = spec.Replace("요일", "").Replace(" ", "");
        if (spec is "평일" or "주중") return Weekdays;
        if (spec == "주말") return Weekend;
        if (spec is "매일" or "연중" or "전일" or "매주" or "월~일" or "월-일") return Week;
        if (spec.Contains("휴일") || spec.Contains("공휴") || spec.Contains("국경일")) return [DayType.Holiday];

        var m = DayRangeRe().Match(spec);
        if (m.Success)
        {
            int from = DayIndex(m.Groups[1].Value[0]), to = DayIndex(m.Groups[2].Value[0]);
            var list = new List<DayType>();
            for (var i = from; ; i = i % 7 + 1)
            {
                list.Add((DayType)i);
                if (i == to || list.Count > 7) break;
            }
            return list;
        }
        return spec.Where(c => "월화수목금토일".Contains(c)).Select(c => (DayType)DayIndex(c)).Distinct();
    }

    private static int DayIndex(char c) => "월화수목금토일".IndexOf(c) + 1;

    private static string Between(string text, Match a, Match b) =>
        b.Index >= a.Index + a.Length ? text.Substring(a.Index + a.Length, b.Index - a.Index - a.Length) : "";

    private static string Squash(string? s) => s is null ? "" : SpacesRe().Replace(s.Trim(), " ");

    // ---------------------------------------------------------------- 정규식

    [GeneratedRegex(@"[∼〜～˜]")] private static partial Regex TildeRe();
    [GeneratedRegex(@"[–—―−‐]")] private static partial Regex DashRe();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRe();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*시\s*(\d{1,2})\s*분")] private static partial Regex HourMinuteKoreanRe();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*시(?!간)")] private static partial Regex HourKoreanRe();
    [GeneratedRegex(@"(?<![\d:])([0-2]\d)([0-5]\d)\s*[~-]\s*([0-2]\d)([0-5]\d)(?![\d:])")] private static partial Regex HhmmRangeRe();
    [GeneratedRegex(@"(\d{1,2}:\d{2})\s*[~-]\s*(\d{1,2}:\d{2})")] private static partial Regex TimeRangeRe();
    [GeneratedRegex(@"(?<![\d:])(\d{1,2})\s*[~-]\s*(\d{1,2}:\d{2})")] private static partial Regex HourToTimeRangeRe();
    [GeneratedRegex(@"(\d{1,2}:\d{2})\s*~\s*(\d{1,2})(?![\d:시])")] private static partial Regex TimeToHourRangeRe();
    [GeneratedRegex(@"(?<![\d:])(\d{1,2})\s*~\s*(\d{1,2})(?![\d:시])")] private static partial Regex HourOnlyRangeRe();
    [GeneratedRegex(@"24\s*시간(\s*개방|\s*운영)?|24\s*시(?!간)|상시(\s*개방)?|항시(\s*개방)?|연중\s*24")] private static partial Regex AllDayWordsRe();
    [GeneratedRegex(@"익일\s*|(?<=[~-]\s*)익\s*(?=\d)")] private static partial Regex NextDayRe();
    [GeneratedRegex(@"(?<ap>오전|오후)\s*(?<h>\d{1,2}):(?<m>\d{2})")] private static partial Regex AmPmRe();
    [GeneratedRegex(@"(\d{1,2}:\d{2})\s*[~-]\s*([0-2]\d)([0-5]\d)(?![\d:])")] private static partial Regex TimeToHhmmRe();
    [GeneratedRegex(@"(?<![\d:\-~.])(\d{1,2})-(\d{1,2})(?![\d:\-~.])")] private static partial Regex HourOnlyDashRangeRe();
    [GeneratedRegex(@"^\(?\d{1,2}\)?$")] private static partial Regex BareNumberRe();
    [GeneratedRegex(@"^(\(?00:00~24:00\)?|24|24:00|24hr?s?|24hours?|00:00~24:00\s*(연중무휴|무휴|개방)?)$", RegexOptions.IgnoreCase)] private static partial Regex ExactAlwaysRe();

    [GeneratedRegex(
        @"(?<range>(?<oh>\d{1,2}):(?<om>\d{2})~(?<ch>\d{1,2}):(?<cm>\d{2}))" +
        @"|(?<closed>휴무|휴관|폐쇄|휴장|미개방|개방\s*안\s*함|폐문|운영\s*안\s*함|close[d]?)" +
        @"|(?<days>평일|주중|주말|매일|연중|전일|매주|법정\s*공휴일|공휴일|국경일|휴일" +
        @"|(?<![\d가-힣])[월화수목금토일](요일)?\s*[~-]\s*[월화수목금토일](요일)?(?![가-힣]|요)" +
        @"|(?<![\d가-힣])[월화수목금토일]요일" +
        @"|(?<![\d가-힣])[월화수목금토일](?=[\s,/()\[\]~\-\d+:·.]|$|휴|폐|은|는))",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenRe();

    [GeneratedRegex(@"^\s*[/(\[]\s*$|^\s*\(\s*$|^\s*$")] private static partial Regex TrailingDaysGlueRe();
    [GeneratedRegex(@"^[\s:()\[\]일은는-]*$")] private static partial Regex ClosedThenDaysGlueRe();
    [GeneratedRegex(@"([월화수목금토일])[~-]([월화수목금토일])")] private static partial Regex DayRangeRe();
    [GeneratedRegex(@"운영\s*시간|운행\s*시간|근무\s*시간|영업\s*시간|영업\s*중|업무\s*시간|개관\s*시간|이용\s*시간\s*내|건물\s*개방|출입\s*가능\s*시간|오픈\s*시|개장\s*시|공연일|전시일")] private static partial Regex FacilityHoursRe();
    [GeneratedRegex(@"\d+\s*시간")] private static partial Regex DurationRe();
    [GeneratedRegex(@"연중\s*무휴|무휴")] private static partial Regex NoBreakRe();
}
