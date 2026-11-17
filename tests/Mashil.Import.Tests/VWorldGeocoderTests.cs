using Mashil.Import.Geocoding;

namespace Mashil.Import.Tests;

/// <summary>브이월드 getcoord 응답 해석. 본문은 2026-11-17 실제 응답에서 (좌표·코드만 줄임).</summary>
public class VWorldGeocoderTests
{
    private static string Ok(string text, string l1, string l2, string l4, string l5) =>
        $$$$$"""{"response":{"status":"OK","refined":{"text":"{{{{{text}}}}}","structure":{"level1":"{{{{{l1}}}}}","level2":"{{{{{l2}}}}}","level4L":"{{{{{l4}}}}}","level5":"{{{{{l5}}}}}"}},"result":{"point":{"x":"127.08","y":"37.58"}}}}""";

    [Fact]
    public void Road_result_is_verified_like_kakao()
    {
        const string query = "경기도 양평군 강상면 강남로 664";
        var c = Assert.Single(VWorldGeocoder.ParseJson(Ok("경기도 양평군 강상면 강남로 664-14", "경기도", "양평군", "강남로", "664-14"), "road", query));
        Assert.Equal(("강남로", "664", "경기도"), (c.RoadName, c.MainBuildingNo, c.Sido));
        Assert.True(GeocodeQueryBuilder.Trim(query).Matches(c));
    }

    [Fact]
    public void Refined_to_another_lot_number_fails_verification()
    {
        // 중도리 103-6 → refine 이 188-103 으로 바꿈
        const string query = "충청남도 금산군 금산읍 중도리 103-6";
        var c = Assert.Single(VWorldGeocoder.ParseJson(Ok("충청남도 금산군 금산읍 중도리 188-103", "충청남도", "금산군", "금산읍", "188-103"), "parcel", query));
        Assert.False(GeocodeQueryBuilder.Trim(query).Matches(c));
    }

    [Fact]
    public void Legal_dong_lot_matches()
    {
        const string query = "서울특별시 중랑구 면목동 1545-3";
        var c = Assert.Single(VWorldGeocoder.ParseJson(Ok("서울특별시 중랑구 면목동 1545-3", "서울특별시", "중랑구", "면목동", "1545-3"), "parcel", query));
        Assert.True(GeocodeQueryBuilder.Trim(query).Matches(c));
    }

    [Fact]
    public void Moving_to_another_district_is_rejected() =>
        Assert.Empty(VWorldGeocoder.ParseJson(Ok("경상남도 거창군 위천면 강동1길 13", "경상남도", "거창군", "강동1길", "13"), "road", "경상남도 위천면 강동1길 13"));

    [Fact]
    public void Plain_lot_turned_into_mountain_lot_is_rejected() =>
        Assert.Empty(VWorldGeocoder.ParseJson(Ok("경상북도 봉화군 봉성면 외삼리 산 103-21", "경상북도", "봉화군", "봉성면", "103-21"), "parcel", "경상북도 봉화군 봉성면 외삼리 103-21"));

    [Fact]
    public void Mountain_lot_stays_mountain()
    {
        const string query = "경상북도 영양군 석보면 삼의리 산31";
        Assert.Single(VWorldGeocoder.ParseJson(Ok("경상북도 영양군 석보면 삼의리 산 31-1", "경상북도", "영양군", "석보면", "31-1"), "parcel", query));
    }

    [Fact]
    public void Not_found_is_empty() =>
        Assert.Empty(VWorldGeocoder.ParseJson("""{"response":{"status":"NOT_FOUND"}}""", "road", "서울특별시 노원구 동일로 1008"));

    [Fact]
    public void Key_error_stops_the_day()
    {
        var ex = Assert.Throws<GeocoderUnavailableException>(() => VWorldGeocoder.ParseJson(
            """{"response":{"status":"ERROR","error":{"level":"2","code":"INVALID_KEY","text":"등록되지 않은 인증키입니다."}}}""", "road", "q"));
        Assert.Contains("INVALID_KEY", ex.Message);
    }
}
