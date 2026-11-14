namespace Mashil.Domain;

/// <summary>
/// 소스 파일의 한 행을 변형 없이 보존 (DATA-03). 재정규화는 CSV 를 다시 받지 않고 이 원본으로 한다.
/// </summary>
public sealed class RawToiletRecord
{
    public long Id { get; set; }

    public int SourceId { get; set; }
    public DataSource? Source { get; set; }

    /// <summary>소스 내 자연 키 (LOCALDATA: 관리번호).</summary>
    public required string SourceKey { get; set; }

    /// <summary>원본 헤더 → 값 (jsonb). 헤더 이름 그대로, 값도 손대지 않음.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>필드 값 기준 SHA-256. 같으면 변경 없음으로 보고 정규화를 건너뜀.</summary>
    public required string RowHash { get; set; }

    public long? ToiletId { get; set; }
    public Toilet? Toilet { get; set; }

    public long LastImportRunId { get; set; }

    /// <summary>성공한 실행에서 연속으로 빠진 횟수 (DATA-15, P1.5 에서 사용).</summary>
    public int ConsecutiveMissing { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
