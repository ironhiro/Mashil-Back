namespace Mashil.Domain;

/// <summary>
/// 화장실 데이터 소스 (예: LOCALDATA 공중화장실정보 전국파일).
/// 헤더 매핑은 소스별 어댑터 코드(<c>Mashil.Import</c>)가 가진다.
/// </summary>
public sealed class DataSource
{
    public int Id { get; set; }

    /// <summary>어댑터를 고르는 키. 예: <see cref="DataSourceCodes.LocalDataPublicRestroom"/>.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public required string Provider { get; set; }

    /// <summary>CSV 등.</summary>
    public required string Format { get; set; }

    /// <summary>Auto / UTF-8 / CP949. Auto 면 내용으로 판별 (응답 헤더의 charset 은 믿지 않음).</summary>
    public string Encoding { get; set; } = "Auto";

    public string? InfoUrl { get; set; }
    public string? DownloadUrl { get; set; }

    /// <summary>이용 조건 / 출처 표시 문구.</summary>
    public string? License { get; set; }

    public bool IsActive { get; set; } = true;
}

public static class DataSourceCodes
{
    public const string LocalDataPublicRestroom = "LOCALDATA_PUBLIC_RESTROOM";
}
