using System.Globalization;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace Mashil.Import.Geocoding;

/// <summary>
/// 브이월드(국토교통부) 주소 → 좌표 (<c>/req/address</c>, getcoord 2.0, EPSG:4326). Kakao 가 못 찾을 때만 부른다.
/// <para>
/// 정부 주소 DB 라 Kakao 에 없는 지번("면목동 1545-3", "고성리 301")을 찾는 경우가 있다.
/// 대신 <c>refine</c> 이 비슷한 번지로 바꿔 주기도 해서(중도리 103-6 → 188-103), 결과는 Kakao 와 같은
/// 도로명·동 + 본번 검증(<see cref="GeocodeQuery.Matches"/>)을 거친다. 2026-11-17 운영 NoMatch 1,256곳 중
/// 363곳에 결과가 나왔고, 검증을 통과하는 건 약 220곳.
/// </para>
/// 결과는 1건뿐이고 무료 한도는 하루 40,000건.
/// </summary>
public sealed partial class VWorldGeocoder(HttpClient http, string apiKey) : IAddressGeocoder
{
    public static readonly Uri BaseAddress = new("https://api.vworld.kr");

    /// <summary>캐시 키 접두어. Kakao 질의와 같은 문자열이어도 결과가 다르므로 나눠 저장한다.</summary>
    public const string CachePrefix = "vworld:";

    /// <summary>
    /// 질의 1건. 도로명 질의(<see cref="GeocodeQuery.RoadName"/> 있음)는 type=road, 아니면 parcel.
    /// <see cref="IAddressGeocoder"/> 는 질의 문자열만 받으므로 도로명 여부를 다시 판단한다.
    /// </summary>
    public async Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
    {
        var type = GeocodeQueryBuilder.Trim(query).RoadName is not null ? "road" : "parcel";
        var url = "/req/address?service=address&request=getcoord&version=2.0&crs=epsg:4326&refine=true&simple=false&format=json"
                  + $"&type={type}&address={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}";
        using var res = await http.GetAsync(new Uri(BaseAddress, url), ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<Envelope>(ct);
        return Parse(body?.Response, type, query);
    }

    internal static IReadOnlyList<GeocodeCandidate> ParseJson(string json, string type, string query) =>
        Parse(System.Text.Json.JsonSerializer.Deserialize<Envelope>(json)?.Response, type, query);

    internal static IReadOnlyList<GeocodeCandidate> Parse(Body? r, string type, string query)
    {
        if (r is null || r.Status == "NOT_FOUND") return [];
        if (r.Status != "OK")
        {
            // 키 오류·한도 초과는 그날 멈춘다. 메시지에 키는 없다
            var code = r.Error?.Code ?? r.Status ?? "?";
            throw new GeocoderUnavailableException($"VWorld {code} {r.Error?.Text}");
        }
        var s = r.Refined?.Structure;
        var p = r.Result?.Point;
        if (s is null || p is null
            || !double.TryParse(p.Y, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(p.X, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
            return [];

        // refine 이 다른 시군구로 옮기거나("강동1길 13" → 거창군) 산 지번을 일반 지번으로 바꾸면 다른 곳이다
        var sigungu = s.Level2?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (sigungu is null || !query.Split(' ').Contains(sigungu)) return [];
        if (type == "parcel" && IsMountain(query) != IsMountain(r.Refined!.Text)) return [];

        // level5: 도로명이면 건물번호 "664-14", 지번이면 "1545-3" / "산 31-1"
        var mainNo = MainNo(s.Level5);
        var text = r.Refined!.Text;
        var sido = SidoNames.Canonical(s.Level1) ?? s.Level1;
        return type == "road"
            ? [new GeocodeCandidate(lat, lng, text, sido, s.Level2, Blank(s.Level4L), mainNo, null, null)]
            : [new GeocodeCandidate(lat, lng, text, sido, s.Level2, null, null, text, mainNo)];
    }

    private static string? MainNo(string? level5)
    {
        if (string.IsNullOrWhiteSpace(level5)) return null;
        var digits = level5.Replace("산", "").Trim().TrimStart('-').Split('-')[0].Trim();
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private static bool IsMountain(string? lot) => lot is not null && MountainRe().IsMatch(lot);

    [System.Text.RegularExpressions.GeneratedRegex(@"(^|\s)산\s*\d")] private static partial System.Text.RegularExpressions.Regex MountainRe();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed class Envelope { [JsonPropertyName("response")] public Body? Response { get; set; } }

    internal sealed class Body
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("error")] public Err? Error { get; set; }
        [JsonPropertyName("refined")] public Refined? Refined { get; set; }
        [JsonPropertyName("result")] public Result? Result { get; set; }
    }

    internal sealed class Err
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    internal sealed class Refined
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("structure")] public Structure? Structure { get; set; }
    }

    internal sealed class Structure
    {
        [JsonPropertyName("level1")] public string? Level1 { get; set; }
        [JsonPropertyName("level2")] public string? Level2 { get; set; }
        [JsonPropertyName("level4L")] public string? Level4L { get; set; }
        [JsonPropertyName("level5")] public string? Level5 { get; set; }
    }

    internal sealed class Result { [JsonPropertyName("point")] public Point? Point { get; set; } }

    internal sealed class Point
    {
        [JsonPropertyName("x")] public string? X { get; set; }
        [JsonPropertyName("y")] public string? Y { get; set; }
    }
}
