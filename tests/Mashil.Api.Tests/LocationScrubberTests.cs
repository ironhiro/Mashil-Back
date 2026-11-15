using Mashil.Api.Ops;
using Sentry;

namespace Mashil.Api.Tests;

public class LocationScrubberTests
{
    [Theory]
    [InlineData("?lat=37.5665&lng=126.978&radiusM=500", "?lat=[Filtered]&lng=[Filtered]&radiusM=500")]
    [InlineData("radiusM=500&LAT=37.5&Lng=127", "radiusM=500&LAT=[Filtered]&Lng=[Filtered]")]
    [InlineData("?north=37.6&south=37.5&east=127.1&west=126.9&limit=50", "?north=[Filtered]&south=[Filtered]&east=[Filtered]&west=[Filtered]&limit=50")]
    [InlineData("?latitude=1&slat=2", "?latitude=1&slat=2")] // 이름이 정확히 같을 때만
    [InlineData("", "")]
    public void Query(string input, string expected) => Assert.Equal(expected, LocationScrubber.ScrubQuery(input));

    [Fact]
    public void Url_keeps_the_path()
    {
        Assert.Equal("https://api.example.com/api/toilets/near?lat=[Filtered]&lng=[Filtered]",
            LocationScrubber.ScrubUrl("https://api.example.com/api/toilets/near?lat=37.1&lng=127.2"));
        Assert.Equal("/api/toilets/1", LocationScrubber.ScrubUrl("/api/toilets/1"));
    }

    [Fact]
    public void Breadcrumb_url_and_message_are_scrubbed()
    {
        var b = new Breadcrumb("GET /api/toilets/near?lat=37.1&lng=127.2", "http",
            new Dictionary<string, string> { ["url"] = "https://x/api/toilets/near?lat=37.1&lng=127.2", ["method"] = "GET" });
        var s = LocationScrubber.Scrub(b);
        Assert.DoesNotContain("37.1", s.Message);
        Assert.DoesNotContain("127.2", s.Data!["url"]);
        Assert.Equal("GET", s.Data["method"]);
    }
}
