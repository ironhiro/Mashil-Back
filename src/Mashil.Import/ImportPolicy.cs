namespace Mashil.Import;

/// <summary>임포트 안전장치 기준값 (05-operations §2.2). 설정 섹션 <c>Import:Policy</c>.</summary>
public sealed record ImportPolicy
{
    /// <summary>직전 성공 대비 파일 크기 변화 허용 비율.</summary>
    public double MaxSizeChangeRatio { get; init; } = 0.30;

    /// <summary>직전 성공 대비 행 수 감소 허용 비율.</summary>
    public double MaxRowDropRatio { get; init; } = 0.05;

    /// <summary>손상(건너뛴) 행 허용 비율.</summary>
    public double MaxSkippedRatio { get; init; } = 0.01;

    /// <summary>성공 실행에서 연속으로 이만큼 빠지면 Removed 대상.</summary>
    public int RemoveAfterMisses { get; init; } = 3;

    /// <summary>한 번에 Removed 될 수가 이 비율(원본 행 수 기준)을 넘으면 관리자 승인까지 보류.</summary>
    public double MaxRemovalRatio { get; init; } = 0.01;

    public static ImportPolicy Default { get; } = new();
}

/// <summary>실행 옵션.</summary>
public sealed record ImportOptions
{
    /// <summary>직전 성공과 같은 파일이어도 처리.</summary>
    public bool Force { get; init; }

    /// <summary>크기·행 수 사전 검증을 건너뜀 (헤더 검증은 항상 함). 원본 형식이 정상적으로 크게 바뀐 경우 관리자가 사용.</summary>
    public bool SkipValidation { get; init; }
}
