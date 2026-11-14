using System.Text.RegularExpressions;

namespace Mashil.Import.Geocoding;

/// <summary>
/// 지오코딩 질의 1개. <see cref="RoadName"/> 또는 <see cref="LotDong"/> 와 <see cref="MainNo"/> 로 결과를 검증한다.
/// </summary>
public sealed record GeocodeQuery(string Query, string? RoadName, string? LotDong, string? MainNo)
{
    /// <summary>
    /// 자른 질의로 찾은 후보가 원문과 같은 주소인지. 잘라서 다른 실제 주소에 조용히 매칭되는 것을 막는다
    /// (예: "장안로 65번길268" 을 "장안로 65" 로 잘라 다른 건물에 매칭, 06-data-profile §9.3).
    /// 검증할 근거(도로명·본번)가 없으면 통과시킨다.
    /// </summary>
    public bool Matches(GeocodeCandidate c)
    {
        if (MainNo is null) return true;
        if (RoadName is not null && c.RoadName is not null)
            return c.RoadName == RoadName && c.MainBuildingNo == MainNo;
        if (LotDong is not null && c.LotAddress is not null)
            return c.LotAddress.Contains(LotDong, StringComparison.Ordinal) && c.MainAddressNo == MainNo;
        return true;
    }
}

/// <summary>지오코더 후보 (Kakao 주소 검색 문서 1건).</summary>
public sealed record GeocodeCandidate(
    double Latitude, double Longitude, string? AddressName,
    string? Sido, string? Sigungu,
    string? RoadName, string? MainBuildingNo,
    string? LotAddress, string? MainAddressNo);

/// <summary>
/// 원문 주소 → 지오코딩 질의 (DATA-24). Kakao 권장대로 상세 주소(건물명, 층, 호, 출입구, 쉼표 뒤 정보)를 뺀다.
/// 규칙은 2026-11-14 샘플 테스트 결과에서 정했다 (06-data-profile §9.3).
/// </summary>
public static partial class GeocodeQueryBuilder
{
    /// <summary>도로명 → 지번 순서로 질의를 만든다. 같은 질의는 한 번만.</summary>
    public static IReadOnlyList<GeocodeQuery> Build(string? roadAddress, string? lotAddress)
    {
        var list = new List<GeocodeQuery>();
        foreach (var raw in new[] { roadAddress, lotAddress })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var q = Trim(raw);
            if (q.Query.Length > 0 && list.All(x => x.Query != q.Query)) list.Add(q);
        }
        return list;
    }

    public static GeocodeQuery Trim(string raw)
    {
        var s = raw.Normalize(System.Text.NormalizationForm.FormKC);
        foreach (var (typo, fix) in SidoTypos) s = s.Replace(typo, fix);
        s = ParenRe().Replace(s, " ");
        s = LotHoRe().Replace(s, "-$1");                   // 407번지 2호 → 407-2
        s = s.Replace("번지", " ");
        s = ZeroSubRe().Replace(s, "$1");                  // 71-0 → 71
        s = s.Split(',')[0];                               // 쉼표 뒤 부가 정보
        s = SpacesRe().Replace(s, " ").Trim();

        if (s.Length >= 12 && !s[..12].Contains(' '))      // 띄어쓰기 없는 주소
            s = SpacesRe().Replace(AdminSuffixRe().Replace(s, "$1 "), " ").Trim();

        s = RoadNumberedRe().Replace(s, "$1$2");           // 장안로 65번길 → 장안로65번길 (도로명 한 토큰)
        s = RoadNoGlueRe().Replace(s, "$1 $2");            // 길주로623 → 길주로 623
        s = LotGlueRe().Replace(s, "$1 $2");               // 관천리75 → 관천리 75

        var road = RoadRe().Match(s);
        if (road.Success)
        {
            var name = road.Groups[1].Value.Split(' ')[^1];
            var query = SpacesRe().Replace($"{road.Groups[1].Value} {road.Groups[2].Value}{road.Groups[3].Value}", " ").Trim();
            return new GeocodeQuery(query, name, null, road.Groups[3].Value.Split('-')[0]);
        }

        var lot = LotRe().Match(s);
        if (lot.Success)
        {
            var dong = lot.Groups[1].Value.Split(' ')[^1];
            var query = SpacesRe().Replace($"{lot.Groups[1].Value} {lot.Groups[2].Value}{lot.Groups[3].Value}", " ").Trim();
            return new GeocodeQuery(query, null, dong, lot.Groups[3].Value.Split('-')[0]);
        }

        return new GeocodeQuery(s, null, null, null);
    }

    private static readonly (string, string)[] SidoTypos = [("서을", "서울"), ("경기두", "경기도"), ("강원드", "강원도")];

    [GeneratedRegex(@"\(.*?\)")] private static partial Regex ParenRe();
    [GeneratedRegex(@"번지\s*(\d+)\s*호")] private static partial Regex LotHoRe();
    [GeneratedRegex(@"(\d+)-0(?!\d)")] private static partial Regex ZeroSubRe();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRe();
    [GeneratedRegex(@"(특별시|광역시|특별자치시|특별자치도|도|시|군|구|읍|면)(?=[가-힣])")] private static partial Regex AdminSuffixRe();
    [GeneratedRegex(@"([가-힣0-9]+(?:로|길))\s+(\d+(?:[가-힣]?번?길))")] private static partial Regex RoadNumberedRe();
    [GeneratedRegex(@"(길|로)(\d+)(?![\d가-힣-])")] private static partial Regex RoadNoGlueRe();
    [GeneratedRegex(@"((?:[가-힣]+)(?:리|동|가))(\d)")] private static partial Regex LotGlueRe();
    [GeneratedRegex(@"^(.*\S(?:로|길))\s+(지하\s*)?(\d+(?:-\d+)?)(?!\d)")] private static partial Regex RoadRe();
    [GeneratedRegex(@"^(.*?(?:동|리|가))\s+(산\s*)?(\d+(?:-\d+)?)(?!\d)")] private static partial Regex LotRe();
}
