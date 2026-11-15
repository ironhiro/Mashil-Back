namespace Mashil.Domain;

/// <summary>추천 점수에 넣는 한 화장실의 사실들.</summary>
/// <param name="DistanceM">현재 위치에서의 거리 (m).</param>
/// <param name="RadiusM">검색 반경 (m). 거리 점수의 기준.</param>
/// <param name="AverageRating">평균 별점 (1~5). 평점이 없으면 null.</param>
/// <param name="RatingCount">평점 수.</param>
/// <param name="HelpfulUp">이 화장실의 (가려지지 않은) 댓글들이 받은 도움돼요 합.</param>
/// <param name="HelpfulDown">같은 댓글들이 받은 아니에요 합.</param>
/// <param name="Open">지금 열려 있는지 (서버 판정).</param>
public sealed record RecommendInput(
    double DistanceM, double RadiusM, double? AverageRating, int RatingCount,
    int HelpfulUp, int HelpfulDown, OpenStatusKind Open);

/// <summary>추천 점수 (0~1) 와 그 구성.</summary>
public sealed record RecommendScore(double Score, double Quality, double Trust, double Proximity);

/// <summary>
/// "지금 여기서 가기 좋은 화장실" 점수. 세 가지를 섞는다.
/// <list type="bullet">
/// <item><b>품질</b> (50%): 별점 평균을 베이지안으로 다듬은 값. 평점이 적으면 중간값(3점) 쪽으로 당긴다.</item>
/// <item><b>믿음</b> (30%): 이 화장실 댓글들이 받은 도움돼요/아니에요 비율 (라플라스 보정, 표가 없으면 0.5).
/// 사람들이 "맞는 말"이라고 한 댓글이 많은 곳은 정보가 검증됐다고 본다.</item>
/// <item><b>가까움</b> (20%): 반경 끝이면 0, 바로 옆이면 1.</item>
/// </list>
/// 닫혀 있으면 추천하지 않는다. 열려 있는지 모르면 0.85 배. 평점도 표도 없으면 근거가 없어 추천하지 않는다.
/// </summary>
public static class ToiletRecommender
{
    public const double PriorMean = 3.0;
    public const double PriorWeight = 2.0;
    public const double QualityWeight = 0.5;
    public const double TrustWeight = 0.3;
    public const double ProximityWeight = 0.2;
    public const double UnknownOpenFactor = 0.85;

    /// <summary>추천할 수 없으면 null (닫힘, 또는 근거 없음).</summary>
    public static RecommendScore? Score(RecommendInput x)
    {
        if (x.Open == OpenStatusKind.Closed) return null;
        var hasRatings = x.RatingCount > 0 && x.AverageRating is not null;
        var votes = Math.Max(0, x.HelpfulUp) + Math.Max(0, x.HelpfulDown);
        if (!hasRatings && votes == 0) return null;

        var n = hasRatings ? x.RatingCount : 0;
        var avg = hasRatings ? Math.Clamp(x.AverageRating!.Value, 1, 5) : PriorMean;
        var bayes = (PriorWeight * PriorMean + avg * n) / (PriorWeight + n);
        var quality = (bayes - 1) / 4;

        var trust = (Math.Max(0, x.HelpfulUp) + 1.0) / (votes + 2.0);

        var proximity = x.RadiusM <= 0 ? 0 : 1 - Math.Clamp(x.DistanceM / x.RadiusM, 0, 1);

        var score = QualityWeight * quality + TrustWeight * trust + ProximityWeight * proximity;
        if (x.Open == OpenStatusKind.Unknown) score *= UnknownOpenFactor;
        return new RecommendScore(Math.Round(score, 4), Math.Round(quality, 4), Math.Round(trust, 4), Math.Round(proximity, 4));
    }
}
