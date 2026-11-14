namespace Mashil.Domain;

public enum OpenStatusKind
{
    Unknown = 0,
    AlwaysOpen = 1,
    Open = 2,
    Closed = 3,
}

/// <summary>지금 열려 있는지 (MAP-05). <see cref="ClosesAtMinute"/>·<see cref="NextOpen"/> 은 안내용.</summary>
public sealed record OpenStatus(OpenStatusKind Kind, int? ClosesAtMinute = null, NextOpening? NextOpen = null)
{
    public static readonly OpenStatus Unknown = new(OpenStatusKind.Unknown);
    public static readonly OpenStatus Always = new(OpenStatusKind.AlwaysOpen);
}

/// <summary>다음 개방: 며칠 뒤(0 = 오늘) 몇 분에.</summary>
public sealed record NextOpening(int DaysFromToday, DayType Day, int Minute);

/// <summary>
/// 구조화된 스케줄로 현재 개방 상태를 판정한다 (KST 기준 시각을 넘겨줄 것).
/// 공휴일 규칙이 없으면 그날 요일 규칙을 쓴다. 자정을 넘기는 시간대는 전날 규칙도 본다.
/// </summary>
public static class OpenStatusEvaluator
{
    public static OpenStatus Evaluate(ScheduleStatus status, IReadOnlyCollection<OpeningSchedule> schedules, DateTime nowKst, Func<DateOnly, bool>? isHoliday = null)
    {
        switch (status)
        {
            case ScheduleStatus.AlwaysOpen: return OpenStatus.Always;
            case ScheduleStatus.Scheduled: break;
            default: return OpenStatus.Unknown; // 미개방·불규칙·건물 운영시간은 "열림"으로 단정하지 않음
        }

        isHoliday ??= _ => false;
        var today = DateOnly.FromDateTime(nowKst);
        var minute = nowKst.Hour * 60 + nowKst.Minute;

        var todayRules = RulesFor(schedules, today, isHoliday);
        if (todayRules is null) return OpenStatus.Unknown; // 오늘 요일 정보 없음 (부분 스케줄)

        foreach (var s in todayRules.Where(s => !s.IsClosed))
        {
            var (open, close) = (s.OpenMinute!.Value, s.CloseMinute!.Value);
            if (close > open && minute >= open && minute < close) return new(OpenStatusKind.Open, close);
            if (close <= open && minute >= open) return new(OpenStatusKind.Open, close); // 자정 넘김, 오늘 저녁 구간
        }

        // 어제 시작해서 자정을 넘긴 시간대가 아직 이어지는지
        var yesterdayRules = RulesFor(schedules, today.AddDays(-1), isHoliday) ?? [];
        foreach (var s in yesterdayRules.Where(s => !s.IsClosed))
        {
            var (open, close) = (s.OpenMinute!.Value, s.CloseMinute!.Value);
            if (close <= open && minute < close) return new(OpenStatusKind.Open, close);
        }

        return new(OpenStatusKind.Closed, null, FindNextOpening(schedules, today, minute, isHoliday));
    }

    private static List<OpeningSchedule>? RulesFor(IReadOnlyCollection<OpeningSchedule> schedules, DateOnly date, Func<DateOnly, bool> isHoliday)
    {
        if (isHoliday(date))
        {
            var holiday = schedules.Where(s => s.DayType == DayType.Holiday).ToList();
            if (holiday.Count > 0) return holiday;
        }
        var day = ToDayType(date.DayOfWeek);
        var rules = schedules.Where(s => s.DayType == day).ToList();
        return rules.Count > 0 ? rules : null;
    }

    private static NextOpening? FindNextOpening(IReadOnlyCollection<OpeningSchedule> schedules, DateOnly today, int minute, Func<DateOnly, bool> isHoliday)
    {
        for (var d = 0; d <= 7; d++)
        {
            var date = today.AddDays(d);
            var rules = RulesFor(schedules, date, isHoliday);
            if (rules is null) continue;
            var next = rules.Where(s => !s.IsClosed && (d > 0 || s.OpenMinute > minute))
                .Select(s => s.OpenMinute!.Value).DefaultIfEmpty(-1).Min();
            if (next >= 0) return new NextOpening(d, ToDayType(date.DayOfWeek), next);
        }
        return null;
    }

    public static DayType ToDayType(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => DayType.Mon,
        DayOfWeek.Tuesday => DayType.Tue,
        DayOfWeek.Wednesday => DayType.Wed,
        DayOfWeek.Thursday => DayType.Thu,
        DayOfWeek.Friday => DayType.Fri,
        DayOfWeek.Saturday => DayType.Sat,
        _ => DayType.Sun,
    };

    public static string FormatMinute(int m) => $"{m / 60:D2}:{m % 60:D2}";
}
