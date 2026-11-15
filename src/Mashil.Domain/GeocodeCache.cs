namespace Mashil.Domain;

/// <summary>
/// 지오코딩 질의 → 결과 캐시 (DATA-25). 같은 질의는 API 를 다시 부르지 않는다.
/// 실패(NoMatch)도 캐시해서 무한 재시도를 막는다.
/// </summary>
public sealed class GeocodeCacheEntry
{
    /// <summary>상세 주소를 제거한 질의 문자열 (06-data-profile §9.3).</summary>
    public required string QueryKey { get; set; }

    public GeocodeCacheStatus Status { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>선택된 후보의 주소 (검증·표시용).</summary>
    public string? MatchedAddress { get; set; }
    public string? MatchedSido { get; set; }
    public int CandidateCount { get; set; }

    public DateTime GeocodedAt { get; set; } = DateTime.UtcNow;
}

public enum GeocodeCacheStatus
{
    Matched = 0,
    /// <summary>후보가 없거나, 원문과 도로명·본번이 같은 후보가 없음.</summary>
    NoMatch = 1,
}

/// <summary>Kakao Local API 일일 호출 수 (KST 날짜 기준, 무료 한도 일 100,000건).</summary>
public sealed class GeocodeDailyUsage
{
    public DateOnly UsageDate { get; set; }
    public int Calls { get; set; }
}
