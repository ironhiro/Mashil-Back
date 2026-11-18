using Mashil.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.IntegrationTests;

public class ChangeRequestApplierDbTests
{
    private static async Task<(Guid admin, ChangeRequest cr)> Request(TestDatabase tdb, ChangeRequestKind kind, long? target, string payload)
    {
        await using var db = tdb.NewContext();
        var user = new UserProfile { Email = $"{Guid.NewGuid():N}@example.invalid", PasswordHash = "x" };
        db.UserProfiles.Add(user);
        var cr = new ChangeRequest { RequesterId = user.Id, Kind = kind, TargetToiletId = target, PayloadJson = payload };
        db.ChangeRequests.Add(cr);
        await db.SaveChangesAsync();
        return (user.Id, cr);
    }

    private static async Task<ChangeRequestApplier.Result> Apply(TestDatabase tdb, ChangeRequest cr, Guid admin)
    {
        await using var db = tdb.NewContext();
        db.Attach(cr);
        return await ChangeRequestApplier.ApplyAsync(db, cr, admin, DateTime.UtcNow, CancellationToken.None);
    }

    private static async Task<long> ImportOne(TestDatabase tdb, SourceFile file)
    {
        await using var db = tdb.NewContext();
        await new ImportRunner(db).RunAsync(DataSourceCodes.LocalDataPublicRestroom, file, ImportTrigger.Manual, new ImportOptions { Force = true, SkipValidation = true });
        return await db.Toilets.Select(t => t.Id).SingleAsync();
    }

    [DbFact]
    public async Task Edit_creates_overrides_reparses_hours_and_survives_reimport()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var file = Files.Of("K1");
        var id = await ImportOne(tdb, file);

        var (admin, cr) = await Request(tdb, ChangeRequestKind.Edit, id,
            """{"name":"고친 이름","openHours":"정시","openHoursDetail":"평일 09:00~18:00, 토·일 휴무","phone":"0212345678","kind":"개방화장실"}""");
        var r = await Apply(tdb, cr, admin);
        Assert.Contains(OverrideFields.Name, r.AppliedFields);
        Assert.Contains("kind", r.IgnoredKeys);

        await ImportOne(tdb, file); // 원본 그대로 재임포트 (force)

        await using var db = tdb.NewContext();
        var t = await db.Toilets.Include(x => x.Schedules).SingleAsync();
        Assert.Equal("고친 이름", t.Name);
        Assert.Equal("02-1234-5678", t.Phone);
        Assert.Equal(ScheduleStatus.Scheduled, t.ScheduleStatus);
        Assert.Contains(t.Schedules, s => s.DayType == DayType.Sun && s.IsClosed);
        Assert.Contains(t.Schedules, s => s.DayType == DayType.Mon && s.OpenMinute == 540 && s.CloseMinute == 1080);
    }

    [DbFact]
    public async Task Edit_address_resets_geocode_and_explicit_location_wins()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await ImportOne(tdb, Files.Of("K1"));
        await using (var db = tdb.NewContext())
        {
            db.ToiletOverrides.Add(new ToiletOverride
            {
                ToiletId = id, FieldName = OverrideFields.Location, ValueJson = OverrideApplier.LocationJson(37.5, 127.0),
                Source = OverrideSource.Geocode, Priority = (int)OverrideSource.Geocode,
            });
            await db.SaveChangesAsync();
        }

        var (admin, cr) = await Request(tdb, ChangeRequestKind.Edit, id, """{"roadAddress":"서울특별시 종로구 새주소로 5"}""");
        await Apply(tdb, cr, admin);
        await using (var db = tdb.NewContext())
        {
            var t = await db.Toilets.SingleAsync();
            Assert.Null(t.Location); // 주소가 바뀌어 지오코딩 좌표 무효화 → 다시 지오코딩 대상
            Assert.Equal(CoordinateSource.Unknown, t.CoordinateSource);
        }

        var (admin2, cr2) = await Request(tdb, ChangeRequestKind.Edit, id, """{"latitude":37.58,"longitude":126.99}""");
        await Apply(tdb, cr2, admin2);
        await using var check = tdb.NewContext();
        Assert.Equal(37.58, (await check.Toilets.SingleAsync()).Latitude);
    }

    [DbFact]
    public async Task Delete_marks_removed_and_reimport_keeps_it_removed()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var file = Files.Of("K1");
        var id = await ImportOne(tdb, file);

        var (admin, cr) = await Request(tdb, ChangeRequestKind.Delete, id, """{"reason":"철거됨"}""");
        await Apply(tdb, cr, admin);
        await ImportOne(tdb, file);

        await using var db = tdb.NewContext();
        Assert.Equal(ToiletStatus.Removed, (await db.Toilets.SingleAsync()).Status);
    }

    [DbFact]
    public async Task Guest_request_without_requester_is_saved_and_applied()
    {
        // 비회원 제보 (2026-11-18): 요청자가 없어도 저장되고 승인하면 반영된다
        await using var tdb = await TestDatabase.CreateAsync();
        Guid admin;
        ChangeRequest cr;
        await using (var db = tdb.NewContext())
        {
            var reviewer = new UserProfile { Email = $"{Guid.NewGuid():N}@example.invalid", Role = UserRole.Admin };
            db.UserProfiles.Add(reviewer);
            cr = new ChangeRequest { RequesterId = null, Kind = ChangeRequestKind.Add, PayloadJson = """{"name":"비회원 제보 화장실","roadAddress":"서울특별시 종로구 테스트로 1"}""" };
            db.ChangeRequests.Add(cr);
            await db.SaveChangesAsync();
            admin = reviewer.Id;
        }
        var r = await Apply(tdb, cr, admin);

        await using var check = tdb.NewContext();
        Assert.Null((await check.ChangeRequests.SingleAsync(x => x.Id == cr.Id)).RequesterId);
        Assert.Equal("비회원 제보 화장실", (await check.Toilets.SingleAsync(x => x.Id == r.ToiletId)).Name);
    }

    [DbFact]
    public async Task Add_creates_user_submitted_toilet_with_schedule()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var (admin, cr) = await Request(tdb, ChangeRequestKind.Add, null,
            """{"name":"새 화장실","kind":"개방화장실","roadAddress":"서울특별시 종로구 테스트로 9","openHours":"정시","openHoursDetail":"09:00~22:00","phone":"0317654321"}""");
        var r = await Apply(tdb, cr, admin);

        await using var db = tdb.NewContext();
        var t = await db.Toilets.Include(x => x.Schedules).SingleAsync(x => x.Id == r.ToiletId);
        Assert.Equal($"USR-{cr.Id}", t.ManagementNo);
        Assert.Equal(ToiletOrigin.UserSubmitted, t.Origin);
        Assert.Equal(ToiletKind.Open, t.KindCode);
        Assert.Equal("031-765-4321", t.Phone);
        Assert.Equal(7, t.Schedules.Count);
        Assert.Null(t.Location); // 좌표 없이 추가 → 지오코딩 대상
    }

    [DbFact]
    public async Task Add_without_name_is_rejected()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var (admin, cr) = await Request(tdb, ChangeRequestKind.Add, null, """{"kind":"공중화장실"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(tdb, cr, admin));
    }
}
