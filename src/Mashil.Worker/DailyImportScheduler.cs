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
}

/// <summary>매일 정해진 KST 시각에 <see cref="DailyImportJob"/> 을 실행한다.</summary>
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
        logger.LogInformation("일일 임포트 예약: 매일 KST {Time}, 소스 {Source}", opts.TimeKst, opts.SourceCode);

        if (opts.RunOnStartup) await RunAsync(opts.SourceCode, ImportTrigger.Manual, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            var next = NextOccurrence(now, at);
            logger.LogInformation("다음 실행: {Next:yyyy-MM-dd HH:mm} KST", next.ToOffset(Kst));
            await Task.Delay(next - now, clock, stoppingToken);
            await RunAsync(opts.SourceCode, ImportTrigger.Scheduled, stoppingToken);
        }
    }

    public static DateTimeOffset NextOccurrence(DateTimeOffset nowUtc, TimeOnly atKst)
    {
        var nowKst = nowUtc.ToOffset(Kst);
        var candidate = new DateTimeOffset(DateOnly.FromDateTime(nowKst.DateTime).ToDateTime(atKst), Kst);
        if (candidate <= nowKst) candidate = candidate.AddDays(1);
        return candidate.ToUniversalTime();
    }

    private async Task RunAsync(string sourceCode, ImportTrigger trigger, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<DailyImportJob>();
            await job.ExecuteOnceAsync(sourceCode, trigger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 종료 중 — 중단된 실행은 다음 실행이 Abandoned 로 정리
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "일일 임포트 중 예기치 않은 오류");
        }
    }
}
