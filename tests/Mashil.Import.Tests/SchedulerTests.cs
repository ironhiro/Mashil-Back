using Mashil.Worker;

namespace Mashil.Import.Tests;

public class SchedulerTests
{
    private static readonly TimeOnly At = new(4, 30);

    [Fact]
    public void Before_time_today_runs_today_kst()
    {
        // 2026-11-14 01:00 KST = 2026-11-13 16:00 UTC
        var next = DailyImportScheduler.NextOccurrence(new DateTimeOffset(2026, 11, 13, 16, 0, 0, TimeSpan.Zero), At);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 19, 30, 0, TimeSpan.Zero), next); // 11-14 04:30 KST
    }

    [Fact]
    public void After_time_runs_tomorrow_kst()
    {
        // 2026-11-14 16:00 KST = 07:00 UTC
        var next = DailyImportScheduler.NextOccurrence(new DateTimeOffset(2026, 11, 14, 7, 0, 0, TimeSpan.Zero), At);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 19, 30, 0, TimeSpan.Zero), next); // 11-15 04:30 KST
    }

    [Fact]
    public void Exactly_at_time_schedules_next_day()
    {
        var next = DailyImportScheduler.NextOccurrence(new DateTimeOffset(2026, 11, 13, 19, 30, 0, TimeSpan.Zero), At);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 19, 30, 0, TimeSpan.Zero), next);
    }
}
