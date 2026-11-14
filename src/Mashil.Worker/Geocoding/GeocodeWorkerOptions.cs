namespace Mashil.Worker.Geocoding;

/// <summary>설정 섹션 <c>Geocode</c>.</summary>
public sealed class GeocodeWorkerOptions
{
    /// <summary>전체 워커 활성화 여부. dev / 키 미발급 환경에서 false 로 두면 호출 안 함.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>한 배치에 처리할 화장실 수.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>각 API 호출 사이 대기 (ms). Kakao Local API rate limit 보호.</summary>
    public int RequestDelayMs { get; set; } = 200;

    /// <summary>하루 최대 API 호출 수 (무료 한도 100,000 중 여유를 남김).</summary>
    public int DailyBudget { get; set; } = 80_000;

    /// <summary>한 배치 처리 후 다음 배치까지 대기 (s).</summary>
    public int BatchSleepSeconds { get; set; } = 10;

    /// <summary>대상이 없을 때 폴링 간격 (s).</summary>
    public int IdleSleepSeconds { get; set; } = 300;
}
