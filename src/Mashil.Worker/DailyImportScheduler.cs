using Mashil.Domain;
using Mashil.Import;
using Microsoft.Extensions.Options;

namespace Mashil.Worker;

/// <summary>설정 섹션 <c>Import:Schedule</c>.</summary>
public sealed class ImportScheduleOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>매일 실행 시각 (KST, HH:mm). 원본 반영이 22시대에도 있어 새벽으로 둔다 (06-data-profile §8).</summary>
    public string TimeKst { get; set; } = "04:30";

    /// <summary>시작하자마자 한 번 실행 (개발·점검용).</summary>
    public bool RunOnStartup { get; set; }

    public string SourceCode { get; set; } = DataSourceCodes.LocalDataPublicRestroom;

    /// <summary>
    /// 실패(Failed: 다운로드 실패, DB 시간 초과·연결 끊김 같은 예외)한 회차를 다시 돌리는 횟수.
    /// 실패 알림은 마지막 시도가 실패했을 때만 보낸다. 사전 검증 거부·대량 삭제 보류는 다시 돌리지 않는다.
    /// </summary>
    public int Retries { get; set; } = 1;

    /// <summary>다시 돌리기 전 기다리는 시간. DB 가 한가해지고 원본 사이트가 돌아올 시간.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>매일 정해진 KST 시각에 <see cref="DailyImportJob"/> 을 실행한다. 실패하면 <see cref="ImportScheduleOptions.Retries"/> 번 다시.</summary>
public sealed class DailyImportScheduler(
    IServiceScopeFactory scopes,
    IOptions<ImportScheduleOptions> options,
    ILogger<DailyImportScheduler> logger,
    TimeProvider clock) : BackgroundService
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9); // 한국은 일광절약시간 없음

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("일일 임포트 비활성화 (Import:Schedule:Enabled = false)");
            return;
        }

        var at = TimeOnly.ParseExact(opts.TimeKst, "HH:mm");
        logger.LogInformation("일일 임포트 예약: 매일 KST {Time}, 소스 {Source}, 실패 시 {Retries}번 더 ({Delay} 뒤)",
            opts.TimeKst, opts.SourceCode, opts.Retries, opts.RetryDelay);

        if (opts.RunOnStartup) await RunWithRetryAsync(opts, ImportTrigger.Manual, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            var next = NextOccurrence(now, at);
            logger.LogInformation("다음 실행: {Next:yyyy-MM-dd HH:mm} KST", next.ToOffset(Kst));
            await Task.Delay(next - now, clock, stoppingToken);
            await RunWithRetryAsync(opts, ImportTrigger.Scheduled, stoppingToken);
        }
    }

    public static DateTimeOffset NextOccurrence(DateTimeOffset nowUtc, TimeOnly atKst)
    {
        var nowKst = nowUtc.ToOffset(Kst);
        var candidate = new DateTimeOffset(DateOnly.FromDateTime(nowKst.DateTime).ToDateTime(atKst), Kst);
        if (candidate <= nowKst) candidate = candidate.AddDays(1);
        return candidate.ToUniversalTime();
    }

    private Task RunWithRetryAsync(ImportScheduleOptions opts, ImportTrigger trigger, CancellationToken ct) =>
        RetryAsync(
            (notifyFailure, token) => RunAsync(opts.SourceCode, trigger, notifyFailure, token),
            opts.Retries, opts.RetryDelay, clock, logger, ct);

    /// <summary>
    /// <paramref name="attempt"/> 을 실패하는 동안 <paramref name="retries"/> 번까지 다시 부른다.
    /// attempt 는 (마지막 시도인지 = 실패를 알릴지) 를 받고, 실패했으면 true 를 돌려준다.
    /// </summary>
    public static async Task<int> RetryAsync(
        Func<bool, CancellationToken, Task<bool>> attempt,
        int retries, TimeSpan delay, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        retries = Math.Max(0, retries);
        for (var i = 0; ; i++)
        {
            var isLast = i >= retries;
            var failed = await attempt(isLast, ct);
            if (!failed || isLast) return i + 1;
            logger.LogWarning("일일 임포트 실패 — {Delay} 뒤 다시 시도 ({Attempt}/{Retries})", delay, i + 1, retries);
            await Task.Delay(delay, clock, ct);
        }
    }

    /// <summary>한 번 실행. 실패했으면 true (Failed 실행, 예기치 않은 예외). 이미 실행 중이어서 건너뛴 것은 실패가 아니다.</summary>
    private async Task<bool> RunAsync(string sourceCode, ImportTrigger trigger, bool notifyFailure, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<DailyImportJob>();
            var run = await job.ExecuteOnceAsync(sourceCode, trigger, ct, notifyFailure);
            return run?.Status == ImportRunStatus.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 종료 중 — 중단된 실행은 다음 실행이 Abandoned 로 정리
            return false;
        }
        catch (Exception ex)
        {
            if (notifyFailure) logger.LogError(ex, "일일 임포트 중 예기치 않은 오류");
            else logger.LogWarning(ex, "일일 임포트 중 예기치 않은 오류 — 다시 시도 예정");
            return true;
        }
    }
}
