using System.Text.Json;
using System.Text.Json.Serialization;
using Mashil.Domain;

namespace Mashil.Api;

/// <summary>
/// 변경 요청 종류. 웹은 "Update", 백엔드 enum 은 Edit 이라 어긋나 있었다 (G3, CR-05).
/// 표준 이름은 Edit 으로 두고, 이전 클라이언트를 위해 한동안 "Update" 도 받는다.
/// </summary>
public sealed class ChangeRequestKindJsonConverter : JsonConverter<ChangeRequestKind>
{
    public override ChangeRequestKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(ChangeRequestKind), n))
            return (ChangeRequestKind)n;

        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "add" => ChangeRequestKind.Add,
            "edit" or "update" => ChangeRequestKind.Edit,
            "delete" => ChangeRequestKind.Delete,
            var other => throw new JsonException($"알 수 없는 변경 요청 종류: {other}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ChangeRequestKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
