using System.Text.Json;
using Mashil.Domain;
using NetTopologySuite.Geometries;

namespace Mashil.Import;

/// <summary>
/// 원본으로 채운 <see cref="Toilet"/> 위에 활성 오버라이드를 필드별 최고 우선순위로 덮는다 (DATA-11).
/// 같은 우선순위면 나중에 만든 것이 이긴다.
/// </summary>
public static class OverrideApplier
{
    public static void Apply(Toilet t, IEnumerable<ToiletOverride> overrides)
    {
        var winners = overrides
            .Where(o => o.IsActive && OverrideFields.All.Contains(o.FieldName))
            .GroupBy(o => o.FieldName)
            .Select(g => g.OrderByDescending(o => o.Priority).ThenByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).First());

        foreach (var o in winners)
        {
            using var doc = JsonDocument.Parse(o.ValueJson);
            var v = doc.RootElement;
            switch (o.FieldName)
            {
                case OverrideFields.Name: t.Name = Str(v) ?? t.Name; break;
                case OverrideFields.RoadAddress: t.RoadAddress = Str(v); break;
                case OverrideFields.LotAddress: t.LotAddress = Str(v); break;
                case OverrideFields.Phone: t.Phone = Str(v); break;
                case OverrideFields.OpenHours: t.OpenHours = Str(v); break;
                case OverrideFields.OpenHoursDetail: t.OpenHoursDetail = Str(v); break;
                case OverrideFields.Status:
                    if (Enum.TryParse<ToiletStatus>(Str(v), out var status)) t.Status = status;
                    break;
                case OverrideFields.Location:
                    ApplyLocation(t, v, o.Source);
                    break;
            }
        }
    }

    private static string? Str(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void ApplyLocation(Toilet t, JsonElement v, OverrideSource source)
    {
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("lat", out var latEl) || !latEl.TryGetDouble(out var lat)
            || !v.TryGetProperty("lng", out var lngEl) || !lngEl.TryGetDouble(out var lng))
            return;

        t.Latitude = lat;
        t.Longitude = lng;
        t.Location = new Point(lng, lat) { SRID = 4326 };
        t.CoordinateSource = source == OverrideSource.Geocode ? CoordinateSource.KakaoGeocoded : CoordinateSource.AdminManual;
    }

    /// <summary>오버라이드 값 JSON 을 만드는 도우미.</summary>
    public static string LocationJson(double lat, double lng) =>
        JsonSerializer.Serialize(new { lat, lng });

    public static string StringJson(string? value) => JsonSerializer.Serialize(value);
}
