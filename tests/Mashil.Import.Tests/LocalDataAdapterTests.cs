using Mashil.Domain;
using Mashil.Import.LocalData;
using NetTopologySuite.Geometries;
using H = Mashil.Import.LocalData.LocalDataPublicRestroomAdapter.H;

namespace Mashil.Import.Tests;

public class LocalDataAdapterTests
{
    private static List<SourceRow> Read(params string[] lines)
    {
        using var reader = TestCsv.File(lines).OpenText();
        return new LocalDataPublicRestroomAdapter().Read(reader).ToList();
    }

    [Fact]
    public void Reads_rows_keyed_by_management_no_and_keeps_original_values()
    {
        var rows = Read(TestCsv.Header, TestCsv.Row1, TestCsv.Row2);

        Assert.Equal(2, rows.Count);
        Assert.Equal("202530000000100840", rows[0].SourceKey);
        Assert.Equal(34, rows[0].Fields.Count);
        Assert.Equal("월-금06:00~22:00 토-일09:00~18:00 공휴일 휴관", rows[0][H.OpenHoursDetail]);
        Assert.Equal("", rows[1][H.RoadAddress]); // 원본 그대로 (빈 문자열 유지)
    }

    [Fact]
    public void Missing_required_header_is_rejected_with_names()
    {
        // 관리번호 컬럼이 없는 헤더
        var header = TestCsv.Header.Replace("관리번호,", "");
        var ex = Assert.Throws<SourceSchemaException>(() => Read(header));
        Assert.Contains(H.ManagementNo, ex.MissingHeaders);
    }

    [Fact]
    public void Old_standard_data_headers_with_coordinates_are_still_accepted()
    {
        // 기존 공공데이터포털 형식처럼 WGS84 컬럼이 더 있어도 필수 헤더만 맞으면 통과 (G12 의 역방향)
        var header = TestCsv.Header + ",WGS84위도,WGS84경도";
        var row = TestCsv.Row1 + ",37.59,126.99";
        var rows = Read(header, row);
        Assert.Single(rows);
        Assert.Equal("37.59", rows[0]["WGS84위도"]);
    }

    [Fact]
    public void Row_with_wrong_column_count_is_skipped()
    {
        var skipped = new List<int>();
        using var reader = TestCsv.File(TestCsv.Header, "3000000,123,공중화장실", TestCsv.Row2).OpenText();
        var rows = new LocalDataPublicRestroomAdapter().Read(reader, (line, _) => skipped.Add(line)).ToList();

        Assert.Single(rows);
        Assert.Single(skipped);
    }

    [Fact]
    public void Row_hash_ignores_column_order_but_not_values()
    {
        var a = new SourceRow("1", new Dictionary<string, string?> { ["x"] = "1", ["y"] = "2" }, 2);
        var b = new SourceRow("1", new Dictionary<string, string?> { ["y"] = "2", ["x"] = "1" }, 2);
        var c = new SourceRow("1", new Dictionary<string, string?> { ["x"] = "1", ["y"] = "3" }, 2);

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
    }

    [Fact]
    public void Payload_json_keeps_korean_readable()
    {
        var row = Read(TestCsv.Header, TestCsv.Row1)[0];
        Assert.Contains("\"화장실명\":\"올림픽기념국민생활관\"", row.ToPayloadJson());
    }

    [Fact]
    public void Mapper_converts_types_and_does_not_touch_coordinates()
    {
        var row = Read(TestCsv.Header, TestCsv.Row1)[0];
        var geocoded = new Point(126.99885, 37.59036) { SRID = 4326 };
        var t = new Toilet
        {
            ManagementNo = row.SourceKey, Kind = "", Name = "",
            Location = geocoded, Latitude = 37.59036, Longitude = 126.99885,
            CoordinateSource = CoordinateSource.KakaoGeocoded,
        };

        LocalDataToiletMapper.Apply(row, t);

        Assert.Equal(3000000, t.LocalGovCode);
        Assert.Equal("올림픽기념국민생활관", t.Name);
        Assert.Equal(AddressSource.CsvRoad, t.AddressSource);
        Assert.Equal(2, t.MaleStallCount);
        Assert.Equal(3, t.FemaleStallCount);
        Assert.True(t.SafetyFacilityRequired);
        Assert.False(t.EntranceCctv);
        Assert.Equal(new DateOnly(2026, 11, 10), t.DataReferenceDate);
        Assert.Equal(new DateTime(2026, 11, 11, 13, 27, 30, DateTimeKind.Utc), t.DataUpdatedAt); // KST 22:27:30 → UTC
        Assert.Equal("0221484057", t.Phone); // 전화번호는 원본 그대로 (정규화는 P2)

        // 원본에 좌표가 없으므로 지오코딩 결과가 유지돼야 한다 (G1)
        Assert.Same(geocoded, t.Location);
        Assert.Equal(CoordinateSource.KakaoGeocoded, t.CoordinateSource);
    }

    [Fact]
    public void Mapper_uses_lot_address_source_when_road_is_blank()
    {
        var row = Read(TestCsv.Header, TestCsv.Row2)[0];
        var t = new Toilet { ManagementNo = row.SourceKey, Kind = "", Name = "" };
        LocalDataToiletMapper.Apply(row, t);

        Assert.Null(t.RoadAddress);
        Assert.Equal("서울특별시 종로구 사직동 1-28", t.LotAddress);
        Assert.Equal(AddressSource.CsvLot, t.AddressSource);
        Assert.Null(t.Phone);
    }
}
