using System.Text.RegularExpressions;
using Mashil.Domain;

namespace Mashil.Import.Normalization;

/// <summary>정규화 중 발견한 문제 (DB 의 <see cref="NormalizationIssue"/> 로 저장됨).</summary>
public sealed record IssueDraft(string Field, IssueType Type, IssueSeverity Severity, string? RawValue, string? Note = null);

/// <summary>정규화에 필요한 외부 입력. 지금은 관리자 매핑 사전만.</summary>
public sealed class NormalizationContext
{
    public static readonly NormalizationContext Empty = new();

    /// <summary>field → (raw key → canonical json).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Mappings { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    public IReadOnlyDictionary<string, string>? For(string field) => Mappings.TryGetValue(field, out var m) ? m : null;

    public static NormalizationContext FromMappings(IEnumerable<ValueMapping> mappings) => new()
    {
        Mappings = mappings.Where(m => m.IsActive)
            .GroupBy(m => m.FieldName)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g
                .GroupBy(m => m.RawKey).ToDictionary(x => x.Key, x => x.OrderByDescending(m => m.Id).First().CanonicalJson)),
    };
}

/// <summary>구분명 → 코드 (요구사항 §6.2). 원본 값은 4종 + 빈값뿐 (06-data-profile §4).</summary>
public static class KindNormalizer
{
    public static (ToiletKind Code, IssueDraft? Issue) Normalize(string? raw)
    {
        var s = raw?.Replace(" ", "").Trim() ?? "";
        return s switch
        {
            "공중화장실" => (ToiletKind.Public, null),
            "개방화장실" => (ToiletKind.Open, null),
            "간이화장실" => (ToiletKind.Temporary, null),
            "이동화장실" => (ToiletKind.Mobile, null),
            _ => (ToiletKind.Other, new IssueDraft(IssueFields.Kind, IssueType.UnknownCode, IssueSeverity.Info, raw)),
        };
    }
}

/// <summary>
/// 전화번호를 국내 표기로 (DATA-07). 원본은 하이픈 없는 숫자 (8~11자리).
/// 02 지역번호 9·10자리, 그 외 0으로 시작 10·11자리, 15xx·16xx·18xx 대표번호 8자리.
/// </summary>
public static partial class PhoneNormalizer
{
    public static (string? Phone, IssueDraft? Issue) Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var d = NonDigitRe().Replace(raw, "");

        string? formatted = d.Length switch
        {
            9 when d.StartsWith("02") => $"02-{d[2..5]}-{d[5..]}",
            10 when d.StartsWith("02") => $"02-{d[2..6]}-{d[6..]}",
            10 when d.StartsWith('0') => $"{d[..3]}-{d[3..6]}-{d[6..]}",
            11 when d.StartsWith('0') => $"{d[..3]}-{d[3..7]}-{d[7..]}",
            12 when d.StartsWith("0505") => $"{d[..4]}-{d[4..8]}-{d[8..]}",
            8 when d[0] == '1' => $"{d[..4]}-{d[4..]}",
            _ => null,
        };

        return formatted is null
            ? (raw.Trim(), new IssueDraft(IssueFields.Phone, IssueType.InvalidPhone, IssueSeverity.Info, raw))
            : (formatted, null);
    }

    [GeneratedRegex(@"\D")] private static partial Regex NonDigitRe();
}
