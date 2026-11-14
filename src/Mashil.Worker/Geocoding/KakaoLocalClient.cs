using System.Text.Json.Serialization;
using YhLibrary.Http;

namespace Mashil.Worker.Geocoding;

/// <summary>
/// Kakao Local API (https://dapi.kakao.com) 의 주소↔좌표 변환만 담당하는 얇은 typed wrapper.
/// 인증 헤더(<c>Authorization: KakaoAK ...</c>) 는 Program.cs 의 HttpClient 등록 시점에 박힘.
/// 응답 JSON 은 snake_case 라 properties 마다 [JsonPropertyName] 명시 필요
/// (YhLibrary.Serialization.JsonSerializer 기본 정책이 camelCase 라 자동 매칭 안 됨).
/// </summary>
public sealed class KakaoLocalClient
{
    private readonly ApiClient _api;

    public KakaoLocalClient(ApiClient api) => _api = api;

    public Task<AddressSearchResponse?> SearchAddressAsync(string query, CancellationToken ct = default)
    {
        var url = $"/v2/local/search/address.json?query={Uri.EscapeDataString(query)}";
        return _api.GetAsync<AddressSearchResponse>(url, ct);
    }

    public Task<Coord2AddressResponse?> Coord2AddressAsync(double longitude, double latitude, CancellationToken ct = default)
    {
        var url = $"/v2/local/geo/coord2address.json?x={longitude}&y={latitude}";
        return _api.GetAsync<Coord2AddressResponse>(url, ct);
    }
}

public sealed class AddressSearchResponse
{
    [JsonPropertyName("documents")]
    public List<AddressDocument> Documents { get; set; } = [];
}

public sealed class AddressDocument
{
    [JsonPropertyName("address_name")] public string? AddressName { get; set; }
    [JsonPropertyName("address_type")] public string? AddressType { get; set; }
    [JsonPropertyName("x")] public string? X { get; set; }   // longitude (string)
    [JsonPropertyName("y")] public string? Y { get; set; }   // latitude (string)
    [JsonPropertyName("address")] public AddressDetail? Address { get; set; }
    [JsonPropertyName("road_address")] public RoadAddressDetail? RoadAddress { get; set; }
}

public sealed class AddressDetail
{
    [JsonPropertyName("address_name")] public string? AddressName { get; set; }
}

public sealed class RoadAddressDetail
{
    [JsonPropertyName("address_name")] public string? AddressName { get; set; }
    [JsonPropertyName("building_name")] public string? BuildingName { get; set; }
}

public sealed class Coord2AddressResponse
{
    [JsonPropertyName("documents")]
    public List<Coord2AddressDocument> Documents { get; set; } = [];
}

public sealed class Coord2AddressDocument
{
    [JsonPropertyName("address")] public AddressDetail? Address { get; set; }
    [JsonPropertyName("road_address")] public RoadAddressDetail? RoadAddress { get; set; }
}
