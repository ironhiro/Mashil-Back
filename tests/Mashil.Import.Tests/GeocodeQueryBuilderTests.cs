using Mashil.Import.Geocoding;

namespace Mashil.Import.Tests;

/// <summary>2026-11-14 Kakao 샘플 재검증(06-data-profile §9.3)의 실제 주소로 만든 코퍼스.</summary>
public class GeocodeQueryBuilderTests
{
    [Theory]
    [InlineData("인천광역시 제물포구 참외전로 121, 동인천역", "인천광역시 제물포구 참외전로 121", "참외전로", "121")]
    [InlineData("부산광역시 사하구 낙동남로 1240-10, 낙동강하구에코센터탐방체험장", "부산광역시 사하구 낙동남로 1240-10", "낙동남로", "1240")]
    [InlineData("제주특별자치도 제주시 중앙로 지하 60, 5번·7번 출입구", "제주특별자치도 제주시 중앙로 지하 60", "중앙로", "60")]
    [InlineData("경상남도 진주시 이반성면 장안로 65번길268", "경상남도 진주시 이반성면 장안로65번길 268", "장안로65번길", "268")]
    [InlineData("인천광역시 부평구 길주로623", "인천광역시 부평구 길주로 623", "길주로", "623")]
    [InlineData("서울특별시 중랑구 동일로136나길14", "서울특별시 중랑구 동일로136나길 14", "동일로136나길", "14")]
    [InlineData("서울특별시 종로구 성균관로 91 (명륜동)", "서울특별시 종로구 성균관로 91", "성균관로", "91")]
    public void Road_addresses_keep_road_and_building_number_only(string raw, string query, string road, string mainNo)
    {
        var q = GeocodeQueryBuilder.Trim(raw);
        Assert.Equal(query, q.Query);
        Assert.Equal(road, q.RoadName);
        Assert.Equal(mainNo, q.MainNo);
    }

    [Theory]
    [InlineData("경상북도 영양군 일월면 문암리 71번지 3호", "경상북도 영양군 일월면 문암리 71-3", "문암리", "71")]
    [InlineData("제주특별자치도 서귀포시 서귀동 305-1, 300-19", "제주특별자치도 서귀포시 서귀동 305-1", "서귀동", "305")]
    [InlineData("충청남도 금산군 부리면 관천리75번지", "충청남도 금산군 부리면 관천리 75", "관천리", "75")]
    [InlineData("부산광역시 남구 대연5동 산71-0번지", "부산광역시 남구 대연5동 산71", "대연5동", "71")]
    public void Lot_addresses_keep_dong_and_number(string raw, string query, string dong, string mainNo)
    {
        var q = GeocodeQueryBuilder.Trim(raw);
        Assert.Equal(query, q.Query);
        Assert.Equal(dong, q.LotDong);
        Assert.Equal(mainNo, q.MainNo);
    }

    [Fact]
    public void Sido_typo_is_fixed()
    {
        Assert.StartsWith("서울특별시 광진구", GeocodeQueryBuilder.Trim("서을특별시 광진구 용마도시자연공원").Query);
    }

    [Fact]
    public void Build_yields_road_then_lot_without_duplicates()
    {
        var qs = GeocodeQueryBuilder.Build("서울특별시 종로구 성균관로 91", "서울특별시 종로구 혜화동 1-21");
        Assert.Equal(["서울특별시 종로구 성균관로 91", "서울특별시 종로구 혜화동 1-21"], qs.Select(q => q.Query));
        Assert.Single(GeocodeQueryBuilder.Build("서울 종로구 성균관로 91", "서울 종로구 성균관로 91"));
    }

    private static GeocodeCandidate Road(string road, string no, string sido = "경상남도", double lat = 35.1) =>
        new(lat, 128.1, $"{road} {no}", sido, "진주시", road, no, null, null);

    [Fact]
    public void Truncated_query_must_not_match_a_different_building()
    {
        // 예전 규칙이 "장안로 65" 로 잘라 다른 건물에 조용히 매칭됐던 사례
        var q = GeocodeQueryBuilder.Trim("경상남도 진주시 이반성면 장안로 65번길268");
        Assert.False(q.Matches(Road("장안로", "65")));
        Assert.True(q.Matches(Road("장안로65번길", "268")));
    }

    [Fact]
    public void Choose_prefers_candidate_in_expected_sido_and_flags_ambiguity()
    {
        var q = new GeocodeQuery("용월리 239-3", null, "용월리", "239");
        GeocodeCandidate Lot(string sido, double lat) => new(lat, 127, $"{sido} 용월리 239-3", sido, null, null, null, $"{sido} 용월리 239-3", "239");

        var c = GeocodingService.Choose(q, [Lot("경기도", 37.0), Lot("전남광주통합특별시", 35.0), Lot("충청북도", 36.5)], "전남광주통합특별시");
        Assert.Equal(35.0, c.Latitude);
        Assert.Equal(3, c.CandidateCount);
    }

    [Fact]
    public void Same_point_returned_twice_is_not_ambiguous()
    {
        var q = GeocodeQueryBuilder.Trim("인천광역시 부평구 길주로623");
        var c = GeocodingService.Choose(q, [Road("길주로", "623", "인천광역시", 37.5), Road("길주로", "623", "인천광역시", 37.50001)], "인천광역시");
        Assert.Equal(1, c.CandidateCount);
    }

    [Fact]
    public void No_verified_candidate_is_no_match()
    {
        var q = GeocodeQueryBuilder.Trim("경상남도 진주시 이반성면 장안로 65번길268");
        Assert.Equal(Mashil.Domain.GeocodeCacheStatus.NoMatch, GeocodingService.Choose(q, [Road("장안로", "65")], null).Status);
    }
}
