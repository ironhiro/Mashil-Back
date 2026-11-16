using Mashil.Import.Geocoding;
using Microsoft.Extensions.Options;

namespace Mashil.Worker.Geocoding;

/// <summary>
/// 좌표가 없는 화장실을 계속 지오코딩한다 (G13). 실제 로직은 <see cref="GeocodingService"/>.
/// 일일 예산을 다 쓰거나 Kakao 가 429/401 을 주면 다음 날 KST 00:05 까지 쉰다.
/// LOCALDATA 파일에는 좌표가 없으므로 역지오코딩(좌표 → 주소)은 더 이상 하지 않는다.
/// </summary>
public sealed class GeocodeWorker(
    ILogger<GeocodeWorker> logger,
    IOptions<GeocodeWorkerOptions> opts,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock) : BackgroundService
{
    private readonly GeocodeWorkerOptions _opts = opts.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            logger.LogInformation("GeocodeWorker disabled (Geocode:Enabled = false). 비활성화된 채로 종료.");
            return;
        }

        logger.LogInformation("GeocodeWorker started. BatchSize={Batch}, RequestDelay={Delay}ms, DailyBudget={Budget}",
            _opts.BatchSize, _opts.RequestDelayMs, _opts.DailyBudget);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetService<GeocodingService>();
                if (service is null)
                {
                    logger.LogWarning("Kakao:RestApiKey 가 없어 지오코딩을 하지 않습니다.");
                    return;
                }

                var r = await service.RunBatchAsync(ct: stoppingToken);
                if (r.Processed > 0)
                    logger.LogInformation("Geocode batch: 처리 {Processed} (성공 {Matched} / 실패 {NoMatch}), API {Calls}, 캐시 {Hits}",
                        r.Processed, r.Matched, r.NoMatch, r.ApiCalls, r.CacheHits);

                // 일시 오류는 다음 배치에서 다시 하지만, 계속 나면 원인을 알 수 있게 남긴다
                if (r.TransientError is not null)
                    logger.LogWarning("Kakao 주소 검색 실패 ({Error}) — 다음 배치에서 다시", r.TransientError);

                if (r.BudgetExhausted || r.GeocoderUnavailable)
                {
                    sleep = UntilNextKstDay(clock.GetUtcNow());
                    logger.LogWarning("지오코딩 중단 ({Reason}) — {Hours:N1}시간 뒤 재개",
                        r.BudgetExhausted ? "일일 예산 소진" : "Kakao 호출 불가(429/401)", sleep.TotalHours);
                }
                else sleep = TimeSpan.FromSeconds(r.Processed > 0 ? _opts.BatchSleepSeconds : _opts.IdleSleepSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "GeocodeWorker 루프 오류 — 잠시 뒤 재시도");
                sleep = TimeSpan.FromSeconds(_opts.IdleSleepSeconds);
            }

            await Task.Delay(sleep, clock, stoppingToken);
        }
    }

    internal static TimeSpan UntilNextKstDay(DateTimeOffset nowUtc)
    {
        var kst = nowUtc.ToOffset(TimeSpan.FromHours(9));
        var next = new DateTimeOffset(kst.Date.AddDays(1).AddMinutes(5), TimeSpan.FromHours(9));
        return next - nowUtc;
    }
}
