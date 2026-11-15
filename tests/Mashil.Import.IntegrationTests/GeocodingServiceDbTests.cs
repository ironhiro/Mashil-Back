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
    public async Task Address_change_invalidates_geocode_and_retries()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Import(tdb, Files.Of("K1"));
        var r1 = await Service(tdb, out var db1, new FakeGeocoder(_ => [At(37.57, 126.98)])).RunBatchAsync();
        await db1.DisposeAsync();

        var moved = Files.Row("K1").Replace("테스트로 1", "새길 9");
        await Import(tdb, Files.Raw(moved));

        await using (var mid = tdb.NewContext())
        {
            var t = await mid.Toilets.SingleAsync();
            Assert.Null(t.Location);
            Assert.Equal(CoordinateSource.Unknown, t.CoordinateSource);
        }

        var fake = new FakeGeocoder(_ => [At(37.60, 127.00, road: "새길", no: "9")]);
        var r2 = await Service(tdb, out var db2, fake).RunBatchAsync();
        await db2.DisposeAsync();
        Assert.Equal(1, r2.Matched);

        await using var check = tdb.NewContext();
        Assert.Equal(37.60, await check.Toilets.Select(t => t.Latitude).SingleAsync());
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
}
