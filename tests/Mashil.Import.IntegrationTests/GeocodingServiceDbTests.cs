using Mashil.Domain;
using Mashil.Import.Geocoding;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.IntegrationTests;

public class GeocodingServiceDbTests
{
    private sealed class FakeGeocoder(Func<string, IReadOnlyList<GeocodeCandidate>> answer) : IAddressGeocoder
    {
        public List<string> Queries { get; } = [];
        public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(answer(query));
        }
    }

    private static GeocodeCandidate At(double lat, double lng, string road = "테스트로", string no = "1") =>
        new(lat, lng, $"서울 종로구 {road} {no}", "서울특별시", "종로구", road, no, null, null);

    private static GeocodingService Service(TestDatabase tdb, out Mashil.Infrastructure.MashilDbContext db, IAddressGeocoder g, int budget = 1000) =>
        new(db = tdb.NewContext(), g, new GeocodingOptions { DailyBudget = budget, RequestDelayMs = 0 });

    private static async Task Import(TestDatabase tdb, SourceFile file)
    {
        await using var db = tdb.NewContext();
        await new ImportRunner(db, ImportPolicy.Default with { MaxRowDropRatio = 1, MaxSizeChangeRatio = 100, MaxRemovalRatio = 1 })
            .RunAsync(DataSourceCodes.LocalDataPublicRestroom, file, ImportTrigger.Manual, new ImportOptions { Force = true });
    }

    [DbFact]
    public async Task Geocoded_location_is_stored_as_override_and_survives_reimport()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));

        var fake = new FakeGeocoder(_ => [At(37.57, 126.98)]);
        var r = await Service(tdb, out var db, fake).RunBatchAsync();
        await db.DisposeAsync();
        Assert.Equal((1, 1, 1), (r.Processed, r.Matched, r.ApiCalls));
        Assert.Equal(["서울특별시 종로구 테스트로 1"], fake.Queries);

        // 같은 주소 그대로 재임포트(이름만 변경) → 좌표 유지
        await Import(tdb, Files.Raw(Files.Row("K1", name: "이름 바뀜")));

        await using var check = tdb.NewContext();
        var t = await check.Toilets.Include(x => x.Overrides).SingleAsync(x => x.ManagementNo == "K1");
        Assert.Equal("이름 바뀜", t.Name);
        Assert.Equal(37.57, t.Latitude);
        Assert.Equal(CoordinateSource.KakaoGeocoded, t.CoordinateSource);
        Assert.Single(t.Overrides, o => o.IsActive && o.Source == OverrideSource.Geocode);
    }

    [DbFact]
    public async Task Cache_prevents_second_api_call_for_same_address()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1", "K2")); // 두 행 모두 같은 주소

        var fake = new FakeGeocoder(_ => [At(37.57, 126.98)]);
        var r = await Service(tdb, out var db, fake).RunBatchAsync();
        await db.DisposeAsync();

        Assert.Equal(2, r.Matched);
        Assert.Equal(1, r.ApiCalls);
        Assert.Equal(1, r.CacheHits);
    }

    [DbFact]
    public async Task No_match_marks_toilet_and_creates_issue_without_retrying()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));

        var fake = new FakeGeocoder(_ => []);
        var first = await Service(tdb, out var db1, fake).RunBatchAsync();
        await db1.DisposeAsync();
        var second = await Service(tdb, out var db2, fake).RunBatchAsync();
        await db2.DisposeAsync();

        Assert.Equal(1, first.NoMatch);
        Assert.Equal(0, second.Processed); // KakaoNoMatch 는 다시 시도하지 않음
        await using var check = tdb.NewContext();
        Assert.Equal(CoordinateSource.KakaoNoMatch, await check.Toilets.Select(t => t.CoordinateSource).SingleAsync());
        Assert.Single(await check.NormalizationIssues.Where(i => i.Type == IssueType.GeocodeNoMatch && i.Status == IssueStatus.Open).ToListAsync());
    }

    [DbFact]
    public async Task Region_only_address_is_not_geocoded_to_region_centre()
    {
        // #14: 시도뿐인 주소는 API 를 부르지 않고 NoMatch, 리 단위 도로명 칸은 건너뛰고 번호 있는 지번으로 찾는다
        await using var tdb = await TestDatabase.CreateAsync();
        static string Row(string key, string road, string lot) => Files.Row(key).Replace("서울특별시 종로구 테스트로 1,,", $"{road},{lot},");
        await Import(tdb, Files.Raw(
            Row("K1", "서울특별시", ""),
            Row("K2", "전라남도 진도군 임회면 죽림리", "전라남도 진도군 임회면 죽림리 432-12")));

        var fake = new FakeGeocoder(q => q.EndsWith("432-12")
            ? [new(34.4, 126.2, "전남 진도군 임회면 죽림리 432-12", "전라남도", "진도군", null, null, "전남 진도군 임회면 죽림리 432-12", "432")]
            : [new(37.5665, 126.978, "서울", "서울특별시", null, null, null, "서울", "")]);
        var r = await Service(tdb, out var db, fake).RunBatchAsync();
        await db.DisposeAsync();

        Assert.Equal((2, 1, 1), (r.Processed, r.Matched, r.NoMatch));
        Assert.EndsWith("432-12", Assert.Single(fake.Queries));

        await using var check = tdb.NewContext();
        var k1 = await check.Toilets.Include(t => t.Issues).SingleAsync(t => t.ManagementNo == "K1");
        Assert.Equal(CoordinateSource.KakaoNoMatch, k1.CoordinateSource);
        Assert.Null(k1.Location);
        Assert.Contains("지역 대표", Assert.Single(k1.Issues, i => i.Type == IssueType.GeocodeNoMatch).Note);
        var k2 = await check.Toilets.SingleAsync(t => t.ManagementNo == "K2");
        Assert.Equal(34.4, k2.Latitude);
    }

    [DbFact]
    public async Task Import_address_change_keeps_coordinates_and_flags_for_review()
    {
        // 2026-11-17 결정: 같은 관리번호면 주소 표기가 바뀌어도 다시 지오코딩하지 않는다 (갱신 속도)
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));
        await Service(tdb, out var db1, new FakeGeocoder(_ => [At(37.57, 126.98)])).RunBatchAsync();
        await db1.DisposeAsync();

        await Import(tdb, Files.Raw(Files.Row("K1").Replace("테스트로 1", "새길 9")));

        await using var check = tdb.NewContext();
        var t = await check.Toilets.Include(x => x.Issues).SingleAsync();
        Assert.Equal(37.57, t.Latitude);
        Assert.Equal(CoordinateSource.KakaoGeocoded, t.CoordinateSource);
        var issue = Assert.Single(t.Issues, i => i.Type == IssueType.AddressChanged && i.Status == IssueStatus.Open);
        Assert.Contains("새길 9", issue.RawValue);
        Assert.Empty(await Service(tdb, out var db2, new FakeGeocoder(_ => [])).Pending().ToListAsync());
        await db2.DisposeAsync();
    }

    [DbFact]
    public async Task Import_address_change_retries_when_there_was_no_coordinate()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));
        await Service(tdb, out var db1, new FakeGeocoder(_ => [])).RunBatchAsync(); // 못 찾음
        await db1.DisposeAsync();

        await Import(tdb, Files.Raw(Files.Row("K1").Replace("테스트로 1", "새길 9")));

        var fake = new FakeGeocoder(_ => [At(37.60, 127.00, road: "새길", no: "9")]);
        var r = await Service(tdb, out var db2, fake).RunBatchAsync();
        await db2.DisposeAsync();
        Assert.Equal(1, r.Matched);
    }

    [DbFact]
    public async Task Admin_location_wins_over_geocode()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));
        await using (var db = tdb.NewContext())
        {
            var id = await db.Toilets.Select(t => t.Id).SingleAsync();
            db.ToiletOverrides.Add(new ToiletOverride
            {
                ToiletId = id, FieldName = OverrideFields.Location, ValueJson = OverrideApplier.LocationJson(37.1, 127.1),
                Source = OverrideSource.Admin, Priority = (int)OverrideSource.Admin,
            });
            await db.SaveChangesAsync();
        }

        var r = await Service(tdb, out var db2, new FakeGeocoder(_ => [At(37.57, 126.98)])).RunBatchAsync();
        await db2.DisposeAsync();
        Assert.Equal(1, r.Matched);

        await using var check = tdb.NewContext();
        var t = await check.Toilets.SingleAsync();
        Assert.Equal(37.1, t.Latitude);
        Assert.Equal(CoordinateSource.AdminManual, t.CoordinateSource);
    }

    [DbFact]
    public async Task Daily_budget_stops_before_calling_api()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Raw(Files.Row("K1"), Files.Row("K2").Replace("테스트로 1", "다른로 2"), Files.Row("K3").Replace("테스트로 1", "또다른로 3")));

        var fake = new FakeGeocoder(q => [At(37.5, 127.0, road: q.Split(' ')[^2], no: q.Split(' ')[^1])]);
        var r = await Service(tdb, out var db, fake, budget: 2).RunBatchAsync();
        await db.DisposeAsync();

        Assert.True(r.BudgetExhausted);
        Assert.Equal(2, r.ApiCalls);
        Assert.Equal(2, r.Matched);
        await using var check = tdb.NewContext();
        Assert.Equal(2, (await check.GeocodeDailyUsage.SingleAsync()).Calls);
        Assert.Equal(1, await check.Toilets.CountAsync(t => t.Location == null && t.CoordinateSource == CoordinateSource.Unknown));
    }

    [DbFact]
    public async Task Existing_coordinates_are_adopted_as_low_priority_overrides()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await using var db = tdb.NewContext();
        db.Toilets.Add(new Toilet
        {
            ManagementNo = "OLD", Kind = "공중화장실", Name = "예전", Latitude = 37.5, Longitude = 127.0,
            Location = new NetTopologySuite.Geometries.Point(127.0, 37.5) { SRID = 4326 }, CoordinateSource = CoordinateSource.Csv,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await GeocodingService.AdoptExistingCoordinatesAsync(db, CancellationToken.None));
        Assert.Equal(0, await GeocodingService.AdoptExistingCoordinatesAsync(db, CancellationToken.None)); // 두 번 해도 중복 없음
        var o = await db.ToiletOverrides.SingleAsync();
        Assert.Equal(OverrideSource.ImportedCoordinate, o.Source);
    }

    private sealed class FakeBoth(Func<string, IReadOnlyList<PlaceCandidate>> places) : IAddressGeocoder, IPlaceSearcher
    {
        public List<string> PlaceQueries { get; } = [];
        public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]); // 주소로는 늘 못 찾음
        public Task<IReadOnlyList<PlaceCandidate>> SearchPlacesAsync(string query, CancellationToken ct)
        {
            PlaceQueries.Add(query);
            return Task.FromResult(places(query));
        }
    }

    [DbFact]
    public async Task Falls_back_to_the_toilet_name_and_flags_it_for_review()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Raw(Files.Row("K1", name: "테스트 근린공원")));

        var fake = new FakeBoth(_ => [new PlaceCandidate(37.58, 126.99, "테스트근린공원", "서울 종로구 명륜동 1", null)]);
        var r = await Service(tdb, out var db, fake).RunBatchAsync();
        await db.DisposeAsync();
        Assert.Equal((1, 1), (r.Matched, r.ApiCalls - 1)); // 주소 1번 + 이름 1번
        Assert.Equal(["종로구 테스트 근린공원"], fake.PlaceQueries);

        await using var check = tdb.NewContext();
        var t = await check.Toilets.Include(x => x.Issues).SingleAsync(x => x.ManagementNo == "K1");
        Assert.Equal(CoordinateSource.KakaoGeocoded, t.CoordinateSource);
        Assert.Equal(37.58, t.Latitude);
        Assert.Single(t.Issues, i => i.Type == IssueType.PlaceNameMatch && i.Status == IssueStatus.Open);
    }

    [DbFact]
    public async Task VWorld_is_asked_only_when_kakao_finds_nothing_and_is_flagged()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1", "K2"));

        var kakao = new FakeGeocoder(_ => []);
        var vworld = new FakeGeocoder(_ => [At(37.59, 126.97)]);
        await using var db = tdb.NewContext();
        var r = await new GeocodingService(db, kakao, new GeocodingOptions { RequestDelayMs = 0 }, fallback: vworld).RunBatchAsync();
        Assert.Equal(2, r.Matched);
        Assert.Single(kakao.Queries);   // 같은 주소: 두 번째는 캐시
        Assert.Single(vworld.Queries);
        Assert.True(await db.GeocodeCache.AnyAsync(c => c.QueryKey == "vworld:서울특별시 종로구 테스트로 1" && c.Status == GeocodeCacheStatus.Matched));

        await using var check = tdb.NewContext();
        var t = await check.Toilets.Include(x => x.Issues).FirstAsync(x => x.ManagementNo == "K1");
        Assert.Equal(37.59, t.Latitude);
        Assert.Single(t.Issues, i => i.Type == IssueType.FallbackGeocode && i.Status == IssueStatus.Open);
    }

    [DbFact]
    public async Task VWorld_is_not_asked_when_kakao_matches()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));

        var vworld = new FakeGeocoder(_ => [At(37.59, 126.97)]);
        await using var db = tdb.NewContext();
        await new GeocodingService(db, new FakeGeocoder(_ => [At(37.57, 126.98)]), new GeocodingOptions { RequestDelayMs = 0 }, fallback: vworld).RunBatchAsync();
        Assert.Empty(vworld.Queries);
    }

    [DbFact]
    public async Task Name_that_does_not_match_leaves_no_match()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Raw(Files.Row("K1", name: "테스트 근린공원")));

        var fake = new FakeBoth(_ => [new PlaceCandidate(37.58, 126.99, "다른 체육센터", "서울 종로구 명륜동 1", null)]);
        var r = await Service(tdb, out var db, fake).RunBatchAsync();
        await db.DisposeAsync();
        Assert.Equal(1, r.NoMatch);

        await using var check = tdb.NewContext();
        var t = await check.Toilets.Include(x => x.Issues).SingleAsync(x => x.ManagementNo == "K1");
        Assert.Equal(CoordinateSource.KakaoNoMatch, t.CoordinateSource);
        Assert.Contains(t.Issues, i => i.Type == IssueType.GeocodeNoMatch && i.Note!.Contains("이름"));
    }

    [DbFact]
    public async Task No_match_is_retried_when_the_name_changes()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Raw(Files.Row("K1", name: "테스트 근린공원")));
        var none = new FakeBoth(_ => []);
        await Service(tdb, out var db1, none).RunBatchAsync();
        await db1.DisposeAsync();

        await Import(tdb, Files.Raw(Files.Row("K1", name: "테스트 어린이공원"))); // 이름만 바뀜

        await using var check = tdb.NewContext();
        var t = await check.Toilets.SingleAsync(x => x.ManagementNo == "K1");
        Assert.Equal(CoordinateSource.Unknown, t.CoordinateSource); // 다음 배치에서 새 이름으로 다시
    }

    [DbFact]
    public async Task Reported_address_is_not_seen_as_changed_by_daily_import()
    {
        // 제보로 고친 주소는 원본과 다르다. 원본 파일이 다시 와도(심지어 원본 주소가 또 바뀌어도) 최종 주소는 그대로라 아무 일도 없어야 한다
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));
        await Service(tdb, out var db1, new FakeGeocoder(_ => [At(37.57, 126.98)])).RunBatchAsync();
        await db1.DisposeAsync();
        await using (var db = tdb.NewContext())
        {
            var t = await db.Toilets.SingleAsync();
            db.ToiletOverrides.Add(new ToiletOverride
            {
                ToiletId = t.Id, FieldName = OverrideFields.RoadAddress, ValueJson = "\"서울특별시 종로구 제보로 5\"",
                Source = OverrideSource.ChangeRequest, Priority = (int)OverrideSource.ChangeRequest, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            t.RoadAddress = "서울특별시 종로구 제보로 5";
            await db.SaveChangesAsync();
        }

        await Import(tdb, Files.Of("K1"));
        await Import(tdb, Files.Raw(Files.Row("K1").Replace("테스트로 1", "원본길 7")));

        await using var check = tdb.NewContext();
        var after = await check.Toilets.Include(x => x.Issues).SingleAsync();
        Assert.Equal("서울특별시 종로구 제보로 5", after.RoadAddress);
        Assert.Equal(37.57, after.Latitude);
        Assert.DoesNotContain(after.Issues, i => i.Type == IssueType.AddressChanged);
    }
}
