namespace Mashil.OsmImport;

/// <summary>새 도로망으로 바꿔도 되는지. 받은 파일이 잘렸거나 분류가 깨지면 간선이 크게 줄어든다.</summary>
public static class WalkSwap
{
    /// <summary>지금 대비 이 비율 아래로 줄면 멈춘다.</summary>
    public const double MinRatio = 0.8;

    /// <summary>바꿔도 되면 null, 아니면 이유.</summary>
    public static string? Check(long current, long next, bool force)
    {
        if (next <= 0) return "새 간선이 0개";
        if (force || current == 0) return null;
        var ratio = (double)next / current;
        return ratio < MinRatio
            ? $"간선이 {current:N0} → {next:N0} ({ratio:P0}). 의도한 것이면 --force"
            : null;
    }
}
