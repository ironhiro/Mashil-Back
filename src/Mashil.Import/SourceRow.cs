using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mashil.Import;

/// <summary>
/// 소스 파일의 한 행. <see cref="Fields"/> 는 원본 헤더 이름 → 원본 값 (변형 없음).
/// </summary>
public sealed record SourceRow(string SourceKey, IReadOnlyDictionary<string, string?> Fields, int LineNumber)
{
    public string? this[string header] => Fields.TryGetValue(header, out var v) ? v : null;

    /// <summary>
    /// 헤더 이름 순(ordinal)으로 정렬한 (헤더, 값) 쌍의 SHA-256. 컬럼 순서가 바뀌어도 같은 값이면 같은 해시.
    /// </summary>
    public string ComputeHash()
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append('\u001F').Append(v ?? "\u0000").Append('\u001E');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToPayloadJson() => JsonSerializer.Serialize(Fields, PayloadJsonOptions);
}
