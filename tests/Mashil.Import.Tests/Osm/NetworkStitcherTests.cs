using Mashil.OsmImport;

namespace Mashil.Import.Tests.Osm;

public class NetworkStitcherTests
{
    // 서울시청 근처. 위도 0.00001° ≈ 1.1m
    private static StitchNode N(long id, double dLatM, double dLngM = 0) =>
        new(id, 126.9779 + dLngM / 88_200.0, 37.5663 + dLatM / 111_000.0);

    [Fact]
    public void Connects_to_the_nearest_seoul_node_within_the_gap()
    {
        var links = NetworkStitcher.Stitch([N(1, 0)], [N(100, 9), N(101, 4), N(102, 30)]);
        var l = Assert.Single(links);
        Assert.Equal(1, l.Osm.Id);
        Assert.Equal(101, l.Seoul.Id);
        Assert.InRange(l.LengthM, 3.5, 4.5);
    }

    [Fact]
    public void Does_not_jump_further_than_the_gap()
    {
        // 차도 건너편 보도 (20m) 로는 건너뛰지 않는다
        Assert.Empty(NetworkStitcher.Stitch([N(1, 0)], [N(100, 20), N(101, 0, 15)]));
    }

    [Fact]
    public void One_link_per_osm_node_and_minimum_length()
    {
        var links = NetworkStitcher.Stitch([N(1, 0), N(1, 0), N(2, 50)], [N(100, 0), N(101, 51)]);
        Assert.Equal(2, links.Count);
        Assert.Equal(0.5, links.Single(l => l.Osm.Id == 1).LengthM); // 같은 자리여도 길이 0 은 아니게
        Assert.Equal(101, links.Single(l => l.Osm.Id == 2).Seoul.Id);
    }

    [Fact]
    public void Finds_neighbours_across_grid_cells()
    {
        // 칸 경계(0.0002°)를 사이에 둔 두 점도 찾는다
        var a = new StitchNode(1, 127.00019999, 37.5);
        var b = new StitchNode(100, 127.00020001, 37.5);
        Assert.Single(NetworkStitcher.Stitch([a], [b]));
    }
}
