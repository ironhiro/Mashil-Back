using Mashil.Domain;

namespace Mashil.Import.Tests;

public class OverrideApplierTests
{
    private static Toilet NewToilet() => new() { ManagementNo = "1", Kind = "공중화장실", Name = "원본 이름" };

    private static ToiletOverride Ov(string field, string json, OverrideSource src, bool active = true, int id = 0) => new()
    {
        Id = id, FieldName = field, ValueJson = json, Source = src, Priority = (int)src, IsActive = active,
    };

    [Fact]
    public void Admin_beats_change_request_beats_original()
    {
        var t = NewToilet();
        OverrideApplier.Apply(t,
        [
            Ov(OverrideFields.Name, OverrideApplier.StringJson("요청 이름"), OverrideSource.ChangeRequest),
            Ov(OverrideFields.Name, OverrideApplier.StringJson("관리자 이름"), OverrideSource.Admin),
        ]);
        Assert.Equal("관리자 이름", t.Name);
    }

    [Fact]
    public void Inactive_override_is_ignored()
    {
        var t = NewToilet();
        OverrideApplier.Apply(t, [Ov(OverrideFields.Name, OverrideApplier.StringJson("비활성"), OverrideSource.Admin, active: false)]);
        Assert.Equal("원본 이름", t.Name);
    }

    [Fact]
    public void Geocode_location_sets_point_and_source()
    {
        var t = NewToilet();
        OverrideApplier.Apply(t, [Ov(OverrideFields.Location, OverrideApplier.LocationJson(37.5, 127.0), OverrideSource.Geocode)]);

        Assert.NotNull(t.Location);
        Assert.Equal(127.0, t.Location!.X);
        Assert.Equal(37.5, t.Location.Y);
        Assert.Equal(4326, t.Location.SRID);
        Assert.Equal(CoordinateSource.KakaoGeocoded, t.CoordinateSource);
    }

    [Fact]
    public void Admin_location_wins_over_geocode()
    {
        var t = NewToilet();
        OverrideApplier.Apply(t,
        [
            Ov(OverrideFields.Location, OverrideApplier.LocationJson(37.5, 127.0), OverrideSource.Geocode),
            Ov(OverrideFields.Location, OverrideApplier.LocationJson(37.6, 127.1), OverrideSource.Admin),
        ]);
        Assert.Equal(37.6, t.Latitude);
        Assert.Equal(CoordinateSource.AdminManual, t.CoordinateSource);
    }

    [Fact]
    public void Status_override_removes_toilet()
    {
        var t = NewToilet();
        OverrideApplier.Apply(t, [Ov(OverrideFields.Status, OverrideApplier.StringJson(nameof(ToiletStatus.Removed)), OverrideSource.ChangeRequest)]);
        Assert.Equal(ToiletStatus.Removed, t.Status);
    }

    [Fact]
    public void Null_value_clears_optional_field_but_not_name()
    {
        var t = NewToilet();
        t.Phone = "0212345678";
        OverrideApplier.Apply(t,
        [
            Ov(OverrideFields.Phone, "null", OverrideSource.Admin),
            Ov(OverrideFields.Name, "null", OverrideSource.Admin),
        ]);
        Assert.Null(t.Phone);
        Assert.Equal("원본 이름", t.Name);
    }

    [Fact]
    public void Same_priority_latest_wins()
    {
        var t = NewToilet();
        var older = Ov(OverrideFields.Name, OverrideApplier.StringJson("예전"), OverrideSource.Admin, id: 1);
        older.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = Ov(OverrideFields.Name, OverrideApplier.StringJson("최신"), OverrideSource.Admin, id: 2);
        newer.CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        OverrideApplier.Apply(t, [newer, older]);
        Assert.Equal("최신", t.Name);
    }
}
