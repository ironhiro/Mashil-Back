using Mashil.Domain;

namespace Mashil.Import.Tests;

public class ToiletRecommenderTests
{
    private static RecommendInput Input(
        double distance = 100, double? avg = 4, int ratings = 3, int up = 0, int down = 0,
        OpenStatusKind open = OpenStatusKind.Open) =>
        new(distance, 1000, avg, ratings, up, down, open);

    [Fact]
    public void Closed_is_never_recommended()
    {
        Assert.Null(ToiletRecommender.Score(Input(open: OpenStatusKind.Closed, avg: 5, ratings: 50, up: 30)));
    }

    [Fact]
    public void No_ratings_and_no_votes_means_no_evidence()
    {
        Assert.Null(ToiletRecommender.Score(Input(avg: null, ratings: 0)));
    }

    [Fact]
    public void Votes_alone_are_enough_evidence()
    {
        var s = ToiletRecommender.Score(Input(avg: null, ratings: 0, up: 3));
        Assert.NotNull(s);
        Assert.Equal(0.5, s!.Quality, 3); // 평점이 없으면 품질은 중간값(3점)
    }

    [Fact]
    public void Helpful_votes_raise_the_score_and_unhelpful_lower_it()
    {
        var none = ToiletRecommender.Score(Input())!.Score;
        var helpful = ToiletRecommender.Score(Input(up: 8))!.Score;
        var unhelpful = ToiletRecommender.Score(Input(down: 8))!.Score;
        Assert.True(helpful > none);
        Assert.True(unhelpful < none);
    }

    [Fact]
    public void One_five_star_rating_does_not_beat_many_four_star_ratings()
    {
        // 베이지안 평균: 평점 한 개짜리 5점은 중간값 쪽으로 당겨진다
        var single = ToiletRecommender.Score(Input(avg: 5, ratings: 1))!.Quality;
        var many = ToiletRecommender.Score(Input(avg: 4.5, ratings: 40))!.Quality;
        Assert.True(many > single);
    }

    [Fact]
    public void Closer_is_better_when_everything_else_is_equal()
    {
        var near = ToiletRecommender.Score(Input(distance: 50))!.Score;
        var far = ToiletRecommender.Score(Input(distance: 900))!.Score;
        Assert.True(near > far);
    }

    [Fact]
    public void Unknown_hours_are_discounted()
    {
        var open = ToiletRecommender.Score(Input(open: OpenStatusKind.AlwaysOpen))!.Score;
        var unknown = ToiletRecommender.Score(Input(open: OpenStatusKind.Unknown))!.Score;
        Assert.Equal(open * ToiletRecommender.UnknownOpenFactor, unknown, 3);
    }

    [Fact]
    public void Score_stays_between_zero_and_one()
    {
        var best = ToiletRecommender.Score(Input(distance: 0, avg: 5, ratings: 1000, up: 1000))!.Score;
        var worst = ToiletRecommender.Score(Input(distance: 5000, avg: 1, ratings: 1000, down: 1000, open: OpenStatusKind.Unknown))!.Score;
        Assert.InRange(best, 0, 1);
        Assert.InRange(worst, 0, 1);
    }
}
