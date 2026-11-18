using Mashil.Import;
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
    /// <summary>원본 좌표 검증을 마지막으로 돌린 날 (KST). 하루 한 번, 지오코딩할 게 없을 때.</summary>
    private DateOnly? _coordinateCheckDay;

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
                if (r.Error is not null && !r.GeocoderUnavailable)
                    logger.LogWarning("Kakao 주소 검색 실패 ({Error}) — 다음 배치에서 다시", r.Error);

                if (r.BudgetExhausted || r.GeocoderUnavailable)
                {
                    sleep = UntilNextKstDay(clock.GetUtcNow());
                    logger.LogWarning("지오코딩 중단 ({Reason}) — {Hours:N1}시간 뒤 재개",
                        r.BudgetExhausted ? "일일 예산 소진" : $"Kakao 호출 불가: {r.Error}", sleep.TotalHours);
                }
                else sleep = TimeSpan.FromSeconds(r.Processed > 0 ? _opts.BatchSleepSeconds : _opts.IdleSleepSeconds);

                // 좌표 없는 화장실을 다 처리했으면 하루 한 번 원본 좌표가 주소와 맞는지 확인 (틀린 원본 좌표를 주소 좌표로)
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(9));
                if (r.Processed == 0 && !r.BudgetExhausted && !r.GeocoderUnavailable && _coordinateCheckDay != today)
                {
                    await using var checkScope = scopeFactory.CreateAsyncScope();
                    var c = await checkScope.ServiceProvider.GetRequiredService<GeocodingService>().CheckImportedCoordinatesAsync(stoppingToken);
                    logger.LogInformation("원본 좌표 검증: 대상 {Candidates}, 확인 {Checked}, 바꿈 {Fixed}, API {Calls}{Error}",
                        c.Candidates, c.Checked, c.Fixed, c.ApiCalls, c.Error is null ? "" : $" — 멈춤: {c.Error}");
                    // 좌표가 바뀌었으니 중복도 다시 (같은 화장실이 좌표만 달라 둘로 보이던 경우)
                    var hidden = await DuplicateMarker.MarkAsync(checkScope.ServiceProvider.GetRequiredService<Mashil.Infrastructure.MashilDbContext>(), stoppingToken);
                    logger.LogInformation("원본 중복 숨김: {Hidden}", hidden);
                    if (!c.Stopped) _coordinateCheckDay = today; // 멈췄으면 다음 루프에서 이어서 (캐시 덕에 API 는 새 주소만)
                }
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
