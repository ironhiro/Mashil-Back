using System.Text.RegularExpressions;
using Mashil.Domain;

namespace Mashil.Import.Geocoding;

/// <summary>장소 이름 → 좌표 후보 (Kakao 키워드 검색).</summary>
public interface IPlaceSearcher
{
    Task<IReadOnlyList<PlaceCandidate>> SearchPlacesAsync(string query, CancellationToken ct);
}

/// <summary>키워드 검색 문서 1건. <see cref="AddressName"/> 은 지번 주소 ("서울 중랑구 망우동 529-2").</summary>
public sealed record PlaceCandidate(double Latitude, double Longitude, string PlaceName, string? AddressName, string? RoadAddressName);

/// <summary>이름 검색 질의. <see cref="Query"/> = "시군구 이름", <see cref="Core"/> = 비교용 이름.</summary>
public sealed record PlaceQuery(string Query, string Core, string Sigungu)
{
    /// <summary>캐시 키. 주소 질의와 겹치지 않게 접두어를 붙인다.</summary>
    public string CacheKey => "place:" + Query;
}

/// <summary>
/// 주소로 못 찾을 때 화장실 이름으로 찾는다. 공중화장실 이름은 보통 그 자리의 대표 시설
/// ("망우3동 공영주차장", "천병약수터")이라 주소보다 정확할 때가 많다.
/// <para>
/// 잘못 찍히는 것보다 못 찾는 게 낫다 (#14). 그래서 셋 다 맞아야 채택한다:
/// 같은 시군구, 이름이 같거나 한쪽이 다른 쪽으로 시작 ("성산대교남단" ↔ "성산대교남단교차로"),
/// 남은 후보들이 한곳(300m 안)에 모임. "상계배드민턴장"으로 엉뚱한 체육센터가 나오는 경우는 이름에서 걸러진다.
/// </para>
/// </summary>
public static partial class PlaceNameSearch
{
    /// <summary>이 거리 안의 후보들은 같은 장소로 본다 (주차장·입구·시설 문서가 한 장소에 여러 개).</summary>
    public const double ClusterMeters = 300;

    /// <summary>이름이 이보다 짧으면 ("공원", "화장실") 어디든 맞아서 찾지 않는다.</summary>
    private const int MinCoreLength = 3;

    public static PlaceQuery? Build(string? name, string? roadAddress, string? lotAddress)
    {
        var core = Core(name);
        var sigungu = Sigungu(roadAddress) ?? Sigungu(lotAddress);
        if (core is null || sigungu is null) return null;
        var display = SpacesRe().Replace(ParenRe().Replace(name!, " "), " ").Trim();
        display = SuffixRe().Replace(display, "").Trim();
        return new PlaceQuery($"{sigungu} {display}", core, sigungu);
    }

    /// <summary>비교용 이름: 괄호·띄어쓰기를 빼고 "공중화장실", "일대" 같은 꼬리를 뗀다.</summary>
    internal static string? Core(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = ParenRe().Replace(name.Normalize(System.Text.NormalizationForm.FormKC), " ");
        s = SuffixRe().Replace(SpacesRe().Replace(s, " ").Trim(), "");
        s = SpacesRe().Replace(s, "");
        return s.Length >= MinCoreLength ? s : null;
    }

    /// <summary>주소에서 시군구 ("중랑구", "수원시 장안구"). 시도 다음 토큰들 중 시·군·구로 끝나는 것.</summary>
    internal static string? Sigungu(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var tokens = SpacesRe().Split(address.Trim());
        var parts = new List<string>();
        foreach (var tok in tokens.Skip(1))
        {
            if (SigunguRe().IsMatch(tok)) { parts.Add(tok); if (tok.EndsWith('구') || tok.EndsWith('군')) break; }
            else break;
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>후보 중 채택할 하나 (없으면 NoMatch 캐시 행).</summary>
    public static GeocodeCacheEntry Choose(PlaceQuery q, IReadOnlyList<PlaceCandidate> candidates, string? expectedSido)
    {
        var lastSigungu = q.Sigungu.Split(' ')[^1];
        var inRegion = candidates.Where(c =>
            c.AddressName is { } a
            && a.Split(' ').Contains(lastSigungu)
            && (expectedSido is null || SidoNames.Canonical(a) is not { } sido || sido == expectedSido)).ToList();

        // 이름이 똑같은 것이 있으면 그것만, 없으면 한쪽이 다른 쪽으로 시작하는 것
        var named = inRegion.Where(c => Core(c.PlaceName) == q.Core).ToList();
        if (named.Count == 0)
            named = inRegion.Where(c => Core(c.PlaceName) is { } p && (p.StartsWith(q.Core, StringComparison.Ordinal) || q.Core.StartsWith(p, StringComparison.Ordinal))).ToList();

        if (named.Count == 0 || named.Any(c => Meters(c, named[0]) > ClusterMeters))
            return new GeocodeCacheEntry { QueryKey = q.CacheKey, Status = GeocodeCacheStatus.NoMatch, CandidateCount = named.Count };

        var pick = named[0]; // Kakao 정확도 순
        return new GeocodeCacheEntry
        {
            QueryKey = q.CacheKey, Status = GeocodeCacheStatus.Matched,
            Latitude = pick.Latitude, Longitude = pick.Longitude,
            MatchedAddress = $"{pick.PlaceName} · {pick.RoadAddressName ?? pick.AddressName}",
            MatchedSido = SidoNames.Canonical(pick.AddressName), CandidateCount = named.Count,
        };
    }

    private static double Meters(PlaceCandidate a, PlaceCandidate b)
    {
        const double r = 6_371_000;
        var dLat = (b.Latitude - a.Latitude) * Math.PI / 180;
        var dLng = (b.Longitude - a.Longitude) * Math.PI / 180 * Math.Cos(a.Latitude * Math.PI / 180);
        return r * Math.Sqrt(dLat * dLat + dLng * dLng);
    }

    [GeneratedRegex(@"\(.*?\)|\[.*?\]")] private static partial Regex ParenRe();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRe();
    [GeneratedRegex(@"(\s*(개방\s*화장실|공중\s*화장실|공용\s*화장실|화장실)|\s+(일대|앞|내))$")] private static partial Regex SuffixRe();
    [GeneratedRegex(@"^[가-힣]+(시|군|구)$")] private static partial Regex SigunguRe();
}
