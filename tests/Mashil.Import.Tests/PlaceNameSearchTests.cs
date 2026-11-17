using Mashil.Domain;
using Mashil.Import.Geocoding;

namespace Mashil.Import.Tests;

/// <summary>
/// 이름으로 찾기. 후보는 2026-11-17 Kakao 키워드 검색 실제 응답에서 (운영의 지역 대표 좌표 595곳 표본).
/// </summary>
public class PlaceNameSearchTests
{
    [Theory]
    [InlineData("망우3동 공영주차장", "서울특별시 중랑구 겸재로 61길, 주차장내 2층", null, "중랑구 망우3동 공영주차장", "망우3동공영주차장")]
    [InlineData("용굴암(우)", null, "서울특별시 노원구", "노원구 용굴암", "용굴암")]
    [InlineData("진관사입구 공중화장실", "서울특별시 은평구", "서울특별시 은평구 진관동 125-43", "은평구 진관사입구", "진관사입구")]
    [InlineData("광덕사 일대", null, "서울특별시 노원구 상계동", "노원구 광덕사", "광덕사")]
    [InlineData("팔달공원", "경기도 수원시 팔달구 정조로", null, "수원시 팔달구 팔달공원", "팔달공원")]
    public void Builds_region_and_name(string name, string? road, string? lot, string query, string core)
    {
        var q = PlaceNameSearch.Build(name, road, lot);
        Assert.NotNull(q);
        Assert.Equal(query, q.Query);
        Assert.Equal(core, q.Core);
    }

    [Theory]
    [InlineData("공원", "서울특별시 노원구")]         // 너무 짧아 어디든 맞는다
    [InlineData("공중화장실", "서울특별시 노원구")]   // 꼬리를 떼면 남는 게 없다
    [InlineData("국궁장", "세종특별자치시")]          // 시군구가 없다
    [InlineData("", "서울특별시 노원구")]
    public void Skips_when_name_or_region_is_too_vague(string name, string road) =>
        Assert.Null(PlaceNameSearch.Build(name, road, null));

    private static PlaceCandidate P(string name, string address, double lat, double lng) => new(lat, lng, name, address, null);

    [Fact]
    public void Exact_name_wins_over_lookalikes()
    {
        var q = PlaceNameSearch.Build("망우3동 공영주차장", "서울특별시 중랑구 겸재로 61길", null)!;
        var r = PlaceNameSearch.Choose(q, [
            P("망우3동공영주차장", "서울 중랑구 망우동 529-2", 37.5920, 127.1010),
            P("망우3동주차장", "서울 중랑구 망우동 423-8", 37.5990, 127.0950),
            P("G car zone 망우3동 공영주차장", "서울 중랑구 망우동 529-2", 37.5920, 127.1010),
        ], "서울특별시");
        Assert.Equal(GeocodeCacheStatus.Matched, r.Status);
        Assert.Equal(37.5920, r.Latitude);
        Assert.Equal("place:중랑구 망우3동 공영주차장", r.QueryKey);
    }

    [Fact]
    public void Unrelated_places_are_rejected()
    {
        // "노원구 상계배드민턴장" → Kakao 는 근처 체육시설을 준다. 이름이 안 맞으니 못 찾음이 맞다
        var q = PlaceNameSearch.Build("상계배드민턴장", null, "서울특별시 노원구")!;
        var r = PlaceNameSearch.Choose(q, [
            P("시립노원청소년센터", "서울 노원구 상계동 772", 37.65, 127.06),
            P("월계배드민턴전용구장", "서울 노원구 월계동 산 63-1", 37.63, 127.05),
        ], "서울특별시");
        Assert.Equal(GeocodeCacheStatus.NoMatch, r.Status);
    }

    [Fact]
    public void Name_prefix_counts_as_same_place()
    {
        var q = PlaceNameSearch.Build("성산대교남단", "서울특별시 영등포구 양평2동", null)!;
        var r = PlaceNameSearch.Choose(q, [P("성산대교남단교차로", "서울 영등포구 양화동 1-22", 37.5440, 126.8960)], "서울특별시");
        Assert.Equal(GeocodeCacheStatus.Matched, r.Status);
    }

    [Fact]
    public void Same_name_in_another_district_is_rejected()
    {
        var q = PlaceNameSearch.Build("천병약수터", null, "서울특별시 노원구")!;
        var r = PlaceNameSearch.Choose(q, [P("천병약수터", "서울 도봉구 도봉동 산 1", 37.68, 127.03)], "서울특별시");
        Assert.Equal(GeocodeCacheStatus.NoMatch, r.Status);
    }

    [Fact]
    public void Same_name_far_apart_is_ambiguous()
    {
        var q = PlaceNameSearch.Build("배드민턴장", null, "서울특별시 노원구")!;
        var r = PlaceNameSearch.Choose(q, [
            P("배드민턴장", "서울 노원구 상계동 1", 37.660, 127.060),
            P("배드민턴장", "서울 노원구 월계동 2", 37.620, 127.050), // 4km 떨어짐
        ], "서울특별시");
        Assert.Equal(GeocodeCacheStatus.NoMatch, r.Status);
    }

    [Fact]
    public void Documents_of_one_place_cluster_together()
    {
        var q = PlaceNameSearch.Build("사당종합체육관", "서울특별시 동작구 사당로", null)!;
        var r = PlaceNameSearch.Choose(q, [
            P("사당종합체육관", "서울 동작구 사당동 산 21-9", 37.4850, 126.9780),
            P("사당종합체육관 주차장", "서울 동작구 사당동 산 21-9", 37.4852, 126.9783),
        ], "서울특별시");
        Assert.Equal(GeocodeCacheStatus.Matched, r.Status);
        Assert.Equal(1, r.CandidateCount); // 이름이 똑같은 것만 남는다
    }
}
