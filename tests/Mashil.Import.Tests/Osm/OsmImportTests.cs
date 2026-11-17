using Mashil.Infrastructure.Routing;
using Mashil.OsmImport;
using OsmSharp.Tags;

namespace Mashil.Import.Tests.Osm;

public class OsmImportTests
{
    [Theory]
    [InlineData(0, 3_700_000, false, true)]          // 처음: 비어 있으면 그냥 넣는다
    [InlineData(3_700_000, 3_650_000, false, true)]  // 조금 줄어듦
    [InlineData(3_700_000, 4_100_000, false, true)]  // 늘어남
    [InlineData(3_700_000, 2_000_000, false, false)] // 크게 줄면 멈춤
    [InlineData(3_700_000, 2_000_000, true, true)]   // --force
    [InlineData(3_700_000, 0, true, false)]          // 0개는 --force 여도 안 됨
    public void Swap_refuses_a_big_drop(long current, long next, bool force, bool ok) =>
        Assert.Equal(ok, WalkSwap.Check(current, next, force) is null);

    [Theory]
    [InlineData("https://download.geofabrik.de/asia/south-korea-latest.osm.pbf", true)]
    [InlineData("http://download.geofabrik.de/asia/south-korea-latest.osm.pbf", false)]
    [InlineData("https://download.geofabrik.de/asia/south-korea-latest.osm.pbf?x=1", false)]
    [InlineData("https://example.com/evil.sh", false)]
    [InlineData("file:///etc/passwd", false)]
    public void Download_only_takes_https_pbf(string url, bool ok)
    {
        var u = new Uri(url);
        if (ok) PbfDownload.Validate(u);
        else Assert.Throws<ArgumentException>(() => PbfDownload.Validate(u));
    }

    [Fact]
    public void Steps_cost_matches_their_walking_time()
    {
        var c = WalkableWays.Classify(new TagsCollection(new Tag("highway", "steps")));
        Assert.True(c.Walkable);
        Assert.True(c.Steps);
        Assert.Equal(WalkPathFinder.StepsTimeFactor, c.CostFactor, 3);
        Assert.Equal(1.0f, WalkableWays.Classify(new TagsCollection(new Tag("highway", "footway"))).CostFactor);
    }
}
