using System.Text.Json;
using Mashil.Api.Endpoints;
using Mashil.Domain;

namespace Mashil.Api.Tests;

/// <summary>전국 화장실 목록 (`/api/toilets/snapshot`): 형식, ETag 비교, 압축 고르기.</summary>
public class ToiletSnapshotTests
{
    private static ToiletSnapshotProvider.SnapshotRow Row(long id, string road, string? lot, double? lat, List<ScheduleRow>? schedules = null) => new(
        id, "시청 화장실", "공중화장실", ToiletKind.Public, road, lot, lat, lat is null ? null : 126.9780123456,
        1, 2, 3, 4, 5, 6, 7, 8, 9, "09:00~18:00", null, "02-120", ScheduleStatus.Scheduled, schedules ?? []);

    [Fact]
    public void Rows_follow_the_field_order()
    {
        var json = ToiletSnapshotProvider.Serialize(
            [Row(7, "서울 중구 세종대로 110", "서울 중구 태평로1가 31", 37.5662952, [new ScheduleRow(DayType.Mon, 540, 1080, false), new ScheduleRow(DayType.Sun, null, null, true)])],
            [new DateOnly(2026, 12, 25)]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(ToiletSnapshotProvider.Version, root.GetProperty("version").GetInt32());
        Assert.Equal("2026-12-25", root.GetProperty("holidays")[0].GetString());

        var fields = root.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
        var row = root.GetProperty("rows")[0];
        Assert.Equal(fields.Count, row.GetArrayLength());
        JsonElement F(string name) => row[fields.IndexOf(name)];

        Assert.Equal(7, F("id").GetInt64());
        Assert.Equal("시청 화장실", F("name").GetString()); // 한글을 \uXXXX 로 늘리지 않는다
        Assert.Equal("Public", F("kindCode").GetString());
        Assert.Equal("서울 중구 태평로1가 31", F("lotAddress").GetString());
        Assert.Equal(37.566295, F("lat").GetDouble()); // 여섯째 자리까지
        Assert.Equal(126.978012, F("lng").GetDouble());
        Assert.Equal(1, F("maleStall").GetInt32());
        Assert.Equal(9, F("femaleChildStall").GetInt32());
        Assert.Equal("Scheduled", F("scheduleStatus").GetString());
        Assert.Equal("[[1,540,1080,0],[7,null,null,1]]", F("schedules").GetRawText());
        Assert.Contains("\"시청 화장실\"", System.Text.Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void Lot_address_equal_to_road_is_dropped_and_missing_coords_are_null()
    {
        var json = ToiletSnapshotProvider.Serialize([Row(1, "같은 주소 1", "같은 주소 1", null)], []);
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.Null, row[fields.IndexOf("lotAddress")].ValueKind);
        Assert.Equal(JsonValueKind.Null, row[fields.IndexOf("lat")].ValueKind);
        Assert.Equal(JsonValueKind.Null, row[fields.IndexOf("lng")].ValueKind);
        Assert.Equal(JsonValueKind.Null, row[fields.IndexOf("schedules")].ValueKind);
    }

    [Fact]
    public void Same_content_gives_same_bytes() =>
        Assert.Equal(
            ToiletSnapshotProvider.Serialize([Row(1, "a", null, 37.5)], []),
            ToiletSnapshotProvider.Serialize([Row(1, "a", null, 37.5)], []));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"x\", \"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("\"abd\"", false)]
    [InlineData("abc", false)]
    public void If_none_match(string? header, bool matches) =>
        Assert.Equal(matches, ToiletSnapshotProvider.Matches(header, "\"abc\""));

    [Theory]
    [InlineData(null, null)]
    [InlineData("identity", null)]
    [InlineData("gzip, deflate, br", "br")]
    [InlineData("gzip, deflate", "gzip")]
    [InlineData("br;q=0, gzip", "gzip")]
    [InlineData("GZIP", "gzip")]
    [InlineData("br; q=0.5", "br")]
    public void Encoding_choice(string? header, string? expected) =>
        Assert.Equal(expected, ToiletSnapshotProvider.PickEncoding(header));
}
