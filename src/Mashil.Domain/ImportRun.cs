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

    // --- 누락 처리 (DATA-15) ---
    /// <summary>이번 실행에서 Missing 으로 바뀐 화장실 수 (원본에서 빠짐 + 원본이 없는 이전 임포트 행).</summary>
    public int MissingRows { get; set; }
    /// <summary>Missing → Active 로 돌아온 화장실 수.</summary>
    public int RestoredRows { get; set; }
    /// <summary>연속 누락 기준을 넘어 Removed 대상이 된 수.</summary>
    public int RemovalCandidates { get; set; }
    /// <summary>실제로 Removed 처리된 수. 대량 삭제 보류(PendingApproval)면 0.</summary>
    public int RemovedRows { get; set; }

    /// <summary>이번 실행에서 새로 생긴 정규화 이슈 수 (DATA-23). 같은 원본 값으로 다시 계산된 이슈는 세지 않는다.</summary>
    public int NewIssues { get; set; }

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
    /// <summary>Running 인 채로 프로세스가 사라진 실행. 다음 실행이 정리한다.</summary>
    Abandoned = 5,
    /// <summary>반영은 끝났지만 삭제 대상이 기준(기본 1%)을 넘어 Removed 처리를 관리자 승인까지 보류.</summary>
    PendingApproval = 6,
}
