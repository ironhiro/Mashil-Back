using Mashil.Worker;
using Microsoft.Extensions.Logging.Abstractions;

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

    // --- 실패한 회차 다시 돌리기 ---

    private static Task<int> Retry(Queue<bool> results, List<bool> notified, int retries = 1) =>
        DailyImportScheduler.RetryAsync(
            (notify, _) => { notified.Add(notify); return Task.FromResult(results.Dequeue()); },
            retries, TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Success_runs_once()
    {
        // 첫 시도는 다시 돌릴 수 있어 실패 알림을 미룬다 (성공·검증 거부 같은 다른 결과의 알림은 그대로)
        var notified = new List<bool>();
        Assert.Equal(1, await Retry(new Queue<bool>([false]), notified));
        Assert.Equal([false], notified);
    }

    [Fact]
    public async Task Failure_then_success_retries_without_notifying_first()
    {
        // 11-27 개발처럼 첫 회차가 DB 시간 초과로 실패해도, 다시 돌아 성공하면 실패 알림이 없다
        var notified = new List<bool>();
        Assert.Equal(2, await Retry(new Queue<bool>([true, false]), notified));
        Assert.Equal([false, true], notified);
    }

    [Fact]
    public async Task Failing_every_time_notifies_only_on_the_last_attempt()
    {
        var notified = new List<bool>();
        Assert.Equal(3, await Retry(new Queue<bool>([true, true, true]), notified, retries: 2));
        Assert.Equal([false, false, true], notified);
    }

    [Fact]
    public async Task No_retries_notifies_the_first_failure()
    {
        var notified = new List<bool>();
        Assert.Equal(1, await Retry(new Queue<bool>([true]), notified, retries: 0));
        Assert.Equal([true], notified);
    }
}
