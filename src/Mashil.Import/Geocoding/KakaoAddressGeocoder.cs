using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mashil.Import.Geocoding;

/// <summary>주소 → 좌표 후보.</summary>
public interface IAddressGeocoder
{
    Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct);
}

/// <summary>Kakao 가 호출 한도 초과(429) 또는 키 오류(401/403)를 돌려줌 — 그날 지오코딩을 멈춰야 함.</summary>
public sealed class GeocoderUnavailableException(string message) : Exception(message);

/// <summary>
/// Kakao Local API 주소 검색 (<c>/v2/local/search/address.json</c>).
/// 인증 헤더는 <c>Authorization: KakaoAK {REST 키}</c> (Bearer 아님).
/// </summary>
public sealed class KakaoAddressGeocoder(HttpClient http, string restApiKey) : IAddressGeocoder
{
    public static readonly Uri BaseAddress = new("https://dapi.kakao.com");

    public async Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, "/v2/local/search/address.json?query=" + Uri.EscapeDataString(query)));
        req.Headers.Authorization = new AuthenticationHeaderValue("KakaoAK", restApiKey);
        using var res = await http.SendAsync(req, ct);

        if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 본문에 이유가 온다 (예: 허용 IP 밖에서 부르면 IP 불일치). 키는 들어 있지 않다
            var reason = await res.Content.ReadAsStringAsync(ct);
            throw new GeocoderUnavailableException($"Kakao 주소 검색 HTTP {(int)res.StatusCode} {reason[..Math.Min(reason.Length, 200)]}");
        }
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<Response>(ct);
        return body?.Documents.Select(ToCandidate).OfType<GeocodeCandidate>().ToList() ?? [];
    }

    private static GeocodeCandidate? ToCandidate(Doc d)
    {
        if (!double.TryParse(d.Y, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(d.X, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
            return null;
        var region1 = d.Road?.Region1 ?? d.Address?.Region1;
        return new GeocodeCandidate(
            lat, lng, d.AddressName,
            SidoNames.Canonical(region1) ?? region1, d.Road?.Region2 ?? d.Address?.Region2,
            d.Road?.RoadName, d.Road?.MainBuildingNo,
            d.Address?.AddressName, d.Address?.MainAddressNo);
    }

    private sealed class Response { [JsonPropertyName("documents")] public List<Doc> Documents { get; set; } = []; }

    private sealed class Doc
    {
        [JsonPropertyName("address_name")] public string? AddressName { get; set; }
        [JsonPropertyName("x")] public string? X { get; set; }
        [JsonPropertyName("y")] public string? Y { get; set; }
        [JsonPropertyName("address")] public Lot? Address { get; set; }
        [JsonPropertyName("road_address")] public Road? Road { get; set; }
    }

    private sealed class Lot
    {
        [JsonPropertyName("address_name")] public string? AddressName { get; set; }
        [JsonPropertyName("region_1depth_name")] public string? Region1 { get; set; }
        [JsonPropertyName("region_2depth_name")] public string? Region2 { get; set; }
        [JsonPropertyName("main_address_no")] public string? MainAddressNo { get; set; }
    }

    private sealed class Road
    {
        [JsonPropertyName("region_1depth_name")] public string? Region1 { get; set; }
        [JsonPropertyName("region_2depth_name")] public string? Region2 { get; set; }
        [JsonPropertyName("road_name")] public string? RoadName { get; set; }
        [JsonPropertyName("main_building_no")] public string? MainBuildingNo { get; set; }
    }
}
