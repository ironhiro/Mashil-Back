namespace Mashil.Domain;

/// <summary>임포트 1회 실행 기록 (DATA-02).</summary>
public sealed class ImportRun
{
    public long Id { get; set; }

    public int SourceId { get; set; }
    public DataSource? Source { get; set; }

    public ImportTrigger Trigger { get; set; }
    public ImportRunStatus Status { get; set; } = ImportRunStatus.Running;

    public string? FileName { get; set; }
    public required string FileSha256 { get; set; }
    public long FileSizeBytes { get; set; }

    /// <summary>실제 판별된 인코딩 (UTF-8 / UTF-8-BOM / CP949). 직전 실행과 달라지면 원본 형식 변화 신호.</summary>
    public string? DetectedEncoding { get; set; }

    /// <summary>원본 → 표준 변환 로직 버전. 올라가면 저장된 원본으로 재정규화 대상.</summary>
    public required string NormalizerVersion { get; set; }

    public int TotalRows { get; set; }
    public int InsertedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int UnchangedRows { get; set; }
    public int SkippedRows { get; set; }

    /// <summary>실패·거부 사유.</summary>
    public string? Message { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    /// <summary>진행 중 주기적으로 갱신. 오래 멈춘 Running 실행을 Abandoned 로 정리할 때 사용 (DATA-22).</summary>
    public DateTime? HeartbeatAt { get; set; }
}

public enum ImportTrigger
{
    Manual = 0,
    Scheduled = 1,
    Upload = 2,
    Renormalize = 3,
}

public enum ImportRunStatus
{
    Running = 0,
    Succeeded = 1,
    /// <summary>직전 성공과 같은 파일이라 처리하지 않음.</summary>
    Skipped = 2,
    /// <summary>사전 검증 실패 — DB 에 쓰지 않음.</summary>
    Rejected = 3,
    Failed = 4,
    Abandoned = 5,
}
