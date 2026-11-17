using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.Geocoding;

/// <summary>설정 섹션 <c>Geocode</c>.</summary>
public sealed record GeocodingOptions
{
    /// <summary>하루 최대 API 호출 수. 무료 한도 100,000 중 다른 로컬 API 몫을 남겨 둔다 (DATA-25).</summary>
    public int DailyBudget { get; init; } = 80_000;

    public int BatchSize { get; init; } = 50;

    /// <summary>호출 간격 (초당 제한 보호).</summary>
    public int RequestDelayMs { get; init; } = 200;
}

public sealed record GeocodeBatchResult(int Processed, int Matched, int NoMatch, int ApiCalls, int CacheHits, bool BudgetExhausted, bool GeocoderUnavailable, string? Error = null);

/// <summary>
/// 좌표가 없는 화장실을 지오코딩한다 (DATA-10·24·25, G13).
/// <list type="number">
/// <item>도로명 → 지번 순으로 상세 주소를 뺀 질의를 만든다 (<see cref="GeocodeQueryBuilder"/>)</item>
/// <item>캐시에 있으면 API 를 부르지 않는다. 일일 예산을 넘으면 멈춘다</item>
/// <item>원문과 도로명·본번이 같은 후보만 인정하고, 여러 개면 개방자치단체코드로 추정한 시도와 맞는 후보를 고른다</item>
/// <item>주소로 못 찾으면 화장실 이름으로 찾는다 (<see cref="PlaceNameSearch"/>). 같은 시군구·같은 이름일 때만, 확인용 이슈를 남긴다</item>
/// <item>결과는 화장실에 직접 쓰지 않고 <c>toilet_overrides(location, Geocode)</c> 로 저장 → 재임포트해도 유지</item>
/// <item>실패·모호·시도 불일치는 정규화 이슈로 남긴다</item>
/// </list>
/// </summary>
public sealed class GeocodingService(
    MashilDbContext db,
    IAddressGeocoder geocoder,
    GeocodingOptions? options = null,
    TimeProvider? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    IPlaceSearcher? places = null)
{
    private readonly IPlaceSearcher? _places = places ?? geocoder as IPlaceSearcher;
    private readonly GeocodingOptions _opts = options ?? new GeocodingOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private Dictionary<int, string>? _sidoByCode;

    /// <summary>지오코딩 대상: 노출 중, 좌표 없음, 아직 시도 안 함, 주소 있음.</summary>
    public IQueryable<Toilet> Pending() => db.Toilets.Where(t =>
        t.Status != ToiletStatus.Removed && t.Location == null && t.CoordinateSource == CoordinateSource.Unknown
        && (t.RoadAddress != null || t.LotAddress != null));

    public async Task<GeocodeBatchResult> RunBatchAsync(int? limit = null, CancellationToken ct = default)
    {
        _sidoByCode ??= await BuildSidoByCodeAsync(ct);
        var take = Math.Min(limit ?? _opts.BatchSize, _opts.BatchSize);
        var toilets = await Pending()
            .Include(t => t.Overrides.Where(o => o.IsActive))
            .Include(t => t.Issues.Where(i => i.Status == IssueStatus.Open && i.FieldName == IssueFields.Geocode))
            .OrderBy(t => t.Id).Take(take).ToListAsync(ct);

        int matched = 0, noMatch = 0, calls = 0, hits = 0;
        bool budgetOut = false, unavailable = false;
        string? error = null;
        var usage = await TodayUsageAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;

        foreach (var t in toilets)
        {
            var queries = GeocodeQueryBuilder.Build(t.RoadAddress, t.LotAddress);
            var expectedSido = _sidoByCode.GetValueOrDefault(t.LocalGovCode) ?? SidoNames.Canonical(t.RoadAddress ?? t.LotAddress);
            GeocodeCacheEntry? hit = null;
            var stop = false;

            foreach (var q in queries)
            {
                if (q.IsRegionOnly) continue; // 번호 없는 주소 — 다음 질의(보통 지번)로
                var cached = await db.GeocodeCache.FindAsync([q.Query], ct);
                if (cached is null)
                {
                    if (usage.Calls >= _opts.DailyBudget) { budgetOut = stop = true; break; }

                    IReadOnlyList<GeocodeCandidate> candidates;
                    try
                    {
                        usage.Calls++; calls++;
                        candidates = await geocoder.SearchAsync(q.Query, ct);
                    }
                    catch (GeocoderUnavailableException ex) { error = ex.Message; unavailable = stop = true; break; }
                    catch (HttpRequestException ex) { error = ex.StatusCode is { } sc ? $"HTTP {(int)sc}" : ex.Message; stop = true; break; } // 일시 오류 — 이 화장실은 다음 배치에서 다시
                    finally { await _delay(TimeSpan.FromMilliseconds(_opts.RequestDelayMs), ct); }

                    cached = Choose(q, candidates, expectedSido);
                    db.GeocodeCache.Add(cached);
                }
                else hits++;

                if (cached.Status == GeocodeCacheStatus.Matched) { hit = cached; break; }
            }

            // 주소로 못 찾음 → 이름으로 (예: "중랑구 망우3동 공영주차장")
            var place = hit is null && !stop && _places is not null ? PlaceNameSearch.Build(t.Name, t.RoadAddress, t.LotAddress) : null;
            if (place is not null)
            {
                var cached = await db.GeocodeCache.FindAsync([place.CacheKey], ct);
                if (cached is null)
                {
                    if (usage.Calls >= _opts.DailyBudget) budgetOut = stop = true;
                    else
                    {
                        try
                        {
                            usage.Calls++; calls++;
                            var found = await _places!.SearchPlacesAsync(place.Query, ct);
                            cached = PlaceNameSearch.Choose(place, found, expectedSido);
                            db.GeocodeCache.Add(cached);
                        }
                        catch (GeocoderUnavailableException ex) { error = ex.Message; unavailable = stop = true; }
                        catch (HttpRequestException ex) { error = ex.StatusCode is { } sc ? $"HTTP {(int)sc}" : ex.Message; stop = true; }
                        finally { await _delay(TimeSpan.FromMilliseconds(_opts.RequestDelayMs), ct); }
                    }
                }
                else hits++;
                if (cached?.Status == GeocodeCacheStatus.Matched) hit = cached;
            }
            var byName = place is not null && hit?.QueryKey == place.CacheKey;

            if (stop && hit is null) break; // 예산 소진·오류: 이 화장실은 미처리로 남긴다

            t.Issues.RemoveAll(i => i.FieldName == IssueFields.Geocode && i.Status == IssueStatus.Open);
            var queryText = string.Join(" | ", queries.Select(q => q.Query));

            if (hit is not null)
            {
                SetGeocodeLocation(t, hit.Latitude!.Value, hit.Longitude!.Value, now);
                if (byName)
                    AddIssue(t, IssueType.PlaceNameMatch, IssueSeverity.Info, place!.Query, $"주소로 못 찾아 이름으로 찾은 위치: {hit.MatchedAddress} — 확인 권장", now);
                if (expectedSido is not null && hit.MatchedSido is not null && hit.MatchedSido != expectedSido)
                    AddIssue(t, IssueType.RegionMismatch, IssueSeverity.Info, queryText, $"결과 {hit.MatchedSido} ≠ 기관코드 추정 {expectedSido}", now);
                if (hit.CandidateCount > 1)
                    AddIssue(t, IssueType.Ambiguous, IssueSeverity.Info, queryText, $"후보 {hit.CandidateCount}개 중 선택: {hit.MatchedAddress}", now);
                matched++;
            }
            else
            {
                t.CoordinateSource = CoordinateSource.KakaoNoMatch; // 같은 주소로 다시 시도하지 않음 (주소가 바뀌면 재시도)
                var note = queries.All(q => q.MainNo is null)
                    ? "주소에 건물번호·지번이 없어 지역 대표 좌표만 나옴"
                    : "주소로 좌표를 찾지 못함";
                note += place is not null ? $", 이름(\"{place.Query}\")으로도 못 찾음 — 관리자 좌표 입력 필요" : " — 관리자 좌표 입력 필요";
                AddIssue(t, IssueType.GeocodeNoMatch, IssueSeverity.Warning, queryText, note, now);
                noMatch++;
            }
            t.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return new GeocodeBatchResult(matched + noMatch, matched, noMatch, calls, hits, budgetOut, unavailable, error);
    }

    /// <summary>
    /// 검증된 후보 중 하나를 고른다. 원문과 다른 주소로 잘못 매칭되는 것보다 NoMatch 가 낫다.
    /// 건물번호·지번이 없는 지역 단위 후보(시도·시군구·리·도로 대표점)는 검증 근거가 없어도 채택하지 않는다 (#14).
    /// </summary>
    internal static GeocodeCacheEntry Choose(GeocodeQuery q, IReadOnlyList<GeocodeCandidate> candidates, string? expectedSido)
    {
        var verified = candidates.Where(c => c.IsPinpoint && q.Matches(c)).ToList();
        if (verified.Count == 0)
            return new GeocodeCacheEntry { QueryKey = q.Query, Status = GeocodeCacheStatus.NoMatch, CandidateCount = candidates.Count };

        // 같은 지점을 도로명·지번 문서로 두 번 주는 경우가 있어 좌표로 묶는다
        var distinct = verified.GroupBy(c => (Math.Round(c.Latitude, 4), Math.Round(c.Longitude, 4))).Select(g => g.First()).ToList();
        var pick = distinct.Count == 1 ? distinct[0]
            : distinct.FirstOrDefault(c => expectedSido is not null && c.Sido == expectedSido) ?? distinct[0];

        return new GeocodeCacheEntry
        {
            QueryKey = q.Query, Status = GeocodeCacheStatus.Matched,
            Latitude = pick.Latitude, Longitude = pick.Longitude,
            MatchedAddress = pick.AddressName, MatchedSido = pick.Sido, CandidateCount = distinct.Count,
        };
    }

    private static void SetGeocodeLocation(Toilet t, double lat, double lng, DateTime now)
    {
        foreach (var o in t.Overrides.Where(o => o.IsActive && o.FieldName == OverrideFields.Location && o.Source == OverrideSource.Geocode))
            o.IsActive = false;
        t.Overrides.Add(new ToiletOverride
        {
            FieldName = OverrideFields.Location, ValueJson = OverrideApplier.LocationJson(lat, lng),
            Source = OverrideSource.Geocode, Priority = (int)OverrideSource.Geocode, CreatedAt = now,
        });
        OverrideApplier.Apply(t, t.Overrides); // 관리자 좌표가 있으면 그것이 이긴다
    }

    private static void AddIssue(Toilet t, IssueType type, IssueSeverity sev, string raw, string note, DateTime now) =>
        t.Issues.Add(new NormalizationIssue { FieldName = IssueFields.Geocode, Type = type, Severity = sev, RawValue = raw, Note = note, CreatedAt = now });

    private async Task<GeocodeDailyUsage> TodayUsageAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime.AddHours(9)); // KST
        var usage = await db.GeocodeDailyUsage.FindAsync([today], ct);
        if (usage is null) db.GeocodeDailyUsage.Add(usage = new GeocodeDailyUsage { UsageDate = today });
        return usage;
    }

    /// <summary>개방자치단체코드 → 그 코드 행들의 주소에서 가장 많이 나온 시도 (06-data-profile §9.2 방식).</summary>
    private async Task<Dictionary<int, string>> BuildSidoByCodeAsync(CancellationToken ct)
    {
        var rows = await db.Toilets.AsNoTracking()
            .Where(t => t.LocalGovCode != 0 && (t.RoadAddress != null || t.LotAddress != null))
            .Select(t => new { t.LocalGovCode, Address = t.RoadAddress ?? t.LotAddress })
            .ToListAsync(ct);
        return rows
            .Select(r => (r.LocalGovCode, Sido: SidoNames.Canonical(r.Address)))
            .Where(r => r.Sido is not null)
            .GroupBy(r => r.LocalGovCode)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Sido!).OrderByDescending(x => x.Count()).First().Key);
    }

    /// <summary>
    /// 이미 화장실에 들어 있는 좌표(예전 표준데이터, 이전 지오코딩 워커)를 오버라이드로 옮긴다.
    /// 그래야 관리자 좌표를 지웠을 때 되돌아갈 값이 남는다 (P1 검증에서 확인한 한계).
    /// </summary>
    public static Task<int> AdoptExistingCoordinatesAsync(MashilDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            INSERT INTO toilet_overrides ("ToiletId", "FieldName", "ValueJson", "Source", "Priority", "IsActive", "CreatedAt")
            SELECT t."Id", 'location', jsonb_build_object('lat', t."Latitude", 'lng', t."Longitude"),
                   CASE WHEN t."CoordinateSource" = 'KakaoGeocoded' THEN 'Geocode' ELSE 'ImportedCoordinate' END,
                   CASE WHEN t."CoordinateSource" = 'KakaoGeocoded' THEN 10 ELSE 5 END,
                   TRUE, now()
            FROM toilets t
            WHERE t."Location" IS NOT NULL AND t."Latitude" IS NOT NULL AND t."Longitude" IS NOT NULL
              AND t."CoordinateSource" IN ('Csv', 'KakaoGeocoded')
              AND NOT EXISTS (SELECT 1 FROM toilet_overrides o
                              WHERE o."ToiletId" = t."Id" AND o."FieldName" = 'location' AND o."IsActive")
            """, ct);
}
