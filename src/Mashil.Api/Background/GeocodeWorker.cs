using System.Globalization;
using Mashil.Api.External;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Mashil.Api.Background;

/// <summary>
/// 두 큐를 한 워커에서 처리:
///  1. Geocode — 주소만 있는 행 (Location null AND has address) → Kakao 주소검색 → Location 채움
///  2. Reverse-geocode — 좌표만 있는 행 (Location not null AND no address) → Kakao coord2address → 주소 채움
/// 매칭 실패 행은 KakaoNoMatch 로 표시해 무한 재시도 차단.
/// </summary>
public sealed class GeocodeWorker(
    ILogger<GeocodeWorker> logger,
    IOptions<GeocodeWorkerOptions> opts,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private readonly GeocodeWorkerOptions _opts = opts.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            logger.LogInformation("GeocodeWorker disabled (Geocode:Enabled = false). 비활성화된 채로 종료.");
            return;
        }

        logger.LogInformation(
            "GeocodeWorker started. BatchSize={Batch}, RequestDelay={Delay}ms, BatchSleep={Sleep}s",
            _opts.BatchSize, _opts.RequestDelayMs, _opts.BatchSleepSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = 0;
                processed += await ProcessGeocodeBatchAsync(stoppingToken);
                processed += await ProcessReverseGeocodeBatchAsync(stoppingToken);

                var sleep = processed > 0 ? _opts.BatchSleepSeconds : _opts.IdleSleepSeconds;
                await Task.Delay(TimeSpan.FromSeconds(sleep), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GeocodeWorker batch failed; backing off.");
                await Task.Delay(TimeSpan.FromSeconds(_opts.IdleSleepSeconds), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessGeocodeBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
        var kakao = scope.ServiceProvider.GetRequiredService<KakaoLocalClient>();

        var batch = await db.Toilets
            .Where(t => t.Location == null
                     && t.CoordinateSource == CoordinateSource.Unknown
                     && (t.RoadAddress != null || t.LotAddress != null))
            .OrderBy(t => t.Id)
            .Take(_opts.BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        logger.LogInformation("Geocoding {Count} rows...", batch.Count);
        var matched = 0;
        foreach (var t in batch)
        {
            ct.ThrowIfCancellationRequested();
            var query = t.RoadAddress ?? t.LotAddress!;
            try
            {
                var resp = await kakao.SearchAddressAsync(query, ct);
                var first = resp?.Documents.FirstOrDefault();
                if (first is null
                    || !double.TryParse(first.X, NumberStyles.Any, CultureInfo.InvariantCulture, out var lng)
                    || !double.TryParse(first.Y, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat))
                {
                    t.CoordinateSource = CoordinateSource.KakaoNoMatch;
                    continue;
                }
                t.Latitude = lat;
                t.Longitude = lng;
                t.Location = new Point(lng, lat) { SRID = 4326 };
                t.CoordinateSource = CoordinateSource.KakaoGeocoded;
                t.UpdatedAt = DateTime.UtcNow;
                matched++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "geocode 실패 (id={Id}, query={Query}); 다음 배치에서 재시도.", t.Id, query);
                // 상태 변경 안 함 → 다음 배치에서 재시도
            }

            await Task.Delay(_opts.RequestDelayMs, ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Geocode batch done. matched={Matched}, no-match={NoMatch}",
            matched, batch.Count - matched);
        return batch.Count;
    }

    private async Task<int> ProcessReverseGeocodeBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
        var kakao = scope.ServiceProvider.GetRequiredService<KakaoLocalClient>();

        var batch = await db.Toilets
            .Where(t => t.Location != null
                     && t.RoadAddress == null
                     && t.LotAddress == null
                     && t.AddressSource == AddressSource.Unknown)
            .OrderBy(t => t.Id)
            .Take(_opts.BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        logger.LogInformation("Reverse-geocoding {Count} rows...", batch.Count);
        var matched = 0;
        foreach (var t in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await kakao.Coord2AddressAsync(t.Longitude!.Value, t.Latitude!.Value, ct);
                var first = resp?.Documents.FirstOrDefault();
                var road = first?.RoadAddress?.AddressName;
                var lot = first?.Address?.AddressName;
                if (string.IsNullOrWhiteSpace(road) && string.IsNullOrWhiteSpace(lot))
                {
                    t.AddressSource = AddressSource.KakaoNoMatch;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(road)) t.RoadAddress = road;
                if (!string.IsNullOrWhiteSpace(lot)) t.LotAddress = lot;
                t.AddressSource = AddressSource.KakaoReverse;
                t.UpdatedAt = DateTime.UtcNow;
                matched++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "reverse-geocode 실패 (id={Id}); 다음 배치에서 재시도.", t.Id);
            }

            await Task.Delay(_opts.RequestDelayMs, ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Reverse-geocode batch done. matched={Matched}, no-match={NoMatch}",
            matched, batch.Count - matched);
        return batch.Count;
    }
}
