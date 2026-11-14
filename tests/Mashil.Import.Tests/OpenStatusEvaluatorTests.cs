using Mashil.Domain;
using Mashil.Import.Normalization;

namespace Mashil.Import.Tests;

public class OpenStatusEvaluatorTests
{
    private static List<OpeningSchedule> Schedules(string? openHours, string detail) =>
        OpeningHoursParser.Parse(openHours, detail).Entries
            .Select(e => new OpeningSchedule { DayType = e.Day, OpenMinute = e.Open, CloseMinute = e.Close, IsClosed = e.Closed })
            .ToList();

    // 2026-11-14 은 토요일
    private static readonly DateTime SatEvening = new(2026, 11, 14, 20, 40, 0);
    private static readonly DateTime SatMorning = new(2026, 11, 14, 10, 0, 0);
    private static readonly DateTime SunNight = new(2026, 11, 15, 1, 0, 0);

    private const string Olympic = "월-금06:00~22:00 토-일09:00~18:00 공휴일 휴관";

    [Fact]
    public void Open_within_range_reports_closing_time()
    {
        var s = OpenStatusEvaluator.Evaluate(ScheduleStatus.Scheduled, Schedules("정시", Olympic), SatMorning);
        Assert.Equal(OpenStatusKind.Open, s.Kind);
        Assert.Equal(18 * 60, s.ClosesAtMinute);
    }

    [Fact]
    public void Closed_after_hours_reports_next_opening_tomorrow()
    {
        var s = OpenStatusEvaluator.Evaluate(ScheduleStatus.Scheduled, Schedules("정시", Olympic), SatEvening);
        Assert.Equal(OpenStatusKind.Closed, s.Kind);
        Assert.Equal(new NextOpening(1, DayType.Sun, 9 * 60), s.NextOpen);
    }

    [Fact]
    public void Holiday_rule_applies_when_date_is_holiday()
    {
        var s = OpenStatusEvaluator.Evaluate(ScheduleStatus.Scheduled, Schedules("정시", Olympic), SatMorning, d => d == new DateOnly(2026, 11, 14));
        Assert.Equal(OpenStatusKind.Closed, s.Kind);
    }

    [Fact]
    public void Closed_day_is_skipped_when_finding_next_opening()
    {
        // 일요일 폐쇄 → 토요일 밤에는 월요일 08:00 을 알려 줘야 함
        var s = OpenStatusEvaluator.Evaluate(ScheduleStatus.Scheduled, Schedules("정시", "08:00-19:00+일요일폐쇄"), SatEvening);
        Assert.Equal(new NextOpening(2, DayType.Mon, 8 * 60), s.NextOpen);
    }

    [Fact]
    public void Overnight_range_is_open_after_midnight()
    {
        var s = OpenStatusEvaluator.Evaluate(ScheduleStatus.Scheduled, Schedules("정시", "22:00~02:00"), SunNight);
        Assert.Equal(OpenStatusKind.Open, s.Kind);
        Assert.Equal(120, s.ClosesAtMinute);
    }

    [Fact]
    public void Partial_schedule_is_unknown_on_missing_day()
    {
        var s = OpenStatusEvaluator.Evaluate(ScheduleStatus.Scheduled, Schedules("정시", "(평일)09:00~18:00"), SatMorning);
        Assert.Equal(OpenStatusKind.Unknown, s.Kind);
    }

    [Theory]
    [InlineData(ScheduleStatus.AlwaysOpen, OpenStatusKind.AlwaysOpen)]
    [InlineData(ScheduleStatus.NotOpen, OpenStatusKind.Unknown)]
    [InlineData(ScheduleStatus.Irregular, OpenStatusKind.Unknown)]
    [InlineData(ScheduleStatus.FollowsFacility, OpenStatusKind.Unknown)]
    [InlineData(ScheduleStatus.Unknown, OpenStatusKind.Unknown)]
    public void Non_scheduled_statuses(ScheduleStatus status, OpenStatusKind expected) =>
        Assert.Equal(expected, OpenStatusEvaluator.Evaluate(status, [], SatMorning).Kind);
}
