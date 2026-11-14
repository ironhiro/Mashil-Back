using System.Text.RegularExpressions;

namespace Mashil.Import.Geocoding;

/// <summary>
/// 시도 이름을 정식 명칭으로 맞춘다. Kakao 는 "충북", "경남" 같은 약칭을 돌려주고
/// 원본에는 "울산", "경남" 같은 약칭과 정식 명칭이 섞여 있다.
/// </summary>
public static partial class SidoNames
{
    private static readonly (string Canonical, Regex Pattern)[] Table =
    [
        ("서울특별시", Re("^서울(특별시|시)?$")),
        ("부산광역시", Re("^부산(광역시|시)?$")),
        ("대구광역시", Re("^대구(광역시|시)?$")),
        ("인천광역시", Re("^인천(광역시|시)?$")),
        ("광주광역시", Re("^광주(광역시)?$")),
        ("대전광역시", Re("^대전(광역시|시)?$")),
        ("울산광역시", Re("^울산(광역시|시)?$")),
        ("세종특별자치시", Re("^세종(특별자치시|시)?$")),
        ("경기도", Re("^경기(도)?$")),
        ("강원특별자치도", Re("^강원(특별자치도|도)?$")),
        ("충청북도", Re("^(충청북도|충북)$")),
        ("충청남도", Re("^(충청남도|충남)$")),
        ("전북특별자치도", Re("^(전북특별자치도|전라북도|전북)$")),
        ("전라남도", Re("^(전라남도|전남)$")),
        ("전남광주통합특별시", Re("^전남광주통합특별시$")),
        ("경상북도", Re("^(경상북도|경북)$")),
        ("경상남도", Re("^(경상남도|경남)$")),
        ("제주특별자치도", Re("^제주(특별자치도|도)?$")),
    ];

    private static Regex Re(string p) => new(p, RegexOptions.Compiled);

    /// <summary>주소의 첫 단어(또는 시도 이름)를 정식 명칭으로. 모르면 null.</summary>
    public static string? Canonical(string? addressOrSido)
    {
        if (string.IsNullOrWhiteSpace(addressOrSido)) return null;
        var first = addressOrSido.Trim().Split(' ')[0];
        foreach (var (canonical, pattern) in Table)
            if (pattern.IsMatch(first)) return canonical;
        return null;
    }
}
