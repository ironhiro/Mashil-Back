namespace Mashil.Import;

/// <summary>소스별 헤더·형식 차이를 흡수해서 공통 <see cref="SourceRow"/> 로 바꾼다 (DATA-04).</summary>
public interface ISourceAdapter
{
    string SourceCode { get; }

    /// <summary>없으면 임포트를 거부하는 헤더.</summary>
    IReadOnlyList<string> RequiredHeaders { get; }

    /// <summary>
    /// 헤더를 검증한 뒤 행을 순서대로 낸다. 필수 헤더가 없으면 <see cref="SourceSchemaException"/>.
    /// 컬럼 수가 헤더와 다른 행은 <paramref name="onSkipped"/> 로 알리고 건너뛴다.
    /// </summary>
    IEnumerable<SourceRow> Read(TextReader reader, Action<int, string>? onSkipped = null);
}

public sealed class SourceSchemaException(IReadOnlyList<string> missingHeaders)
    : Exception($"필수 헤더가 없습니다: {string.Join(", ", missingHeaders)}")
{
    public IReadOnlyList<string> MissingHeaders { get; } = missingHeaders;
}
