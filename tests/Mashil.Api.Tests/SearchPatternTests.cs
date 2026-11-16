using Mashil.Api.Endpoints;

namespace Mashil.Api.Tests;

/// <summary>검색어 ILIKE 패턴 (#19). %, _, \ 는 글자 그대로 찾는다 (이스케이프 문자 \).</summary>
public class SearchPatternTests
{
    [Theory]
    [InlineData("화장실", "%화장실%")]
    [InlineData("  시청  ", "%시청%")]
    [InlineData("%", @"%\%%")]
    [InlineData("_", @"%\_%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData(@"a\b", @"%a\\b%")]
    [InlineData(@"\%", @"%\\\%%")]
    public void Wildcards_and_escape_are_escaped(string q, string pattern) =>
        Assert.Equal(pattern, ToiletEndpoints.ContainsPattern(q));

    [Fact]
    public void Escape_character_is_a_single_backslash() => Assert.Equal(@"\", ToiletEndpoints.LikeEscape);
}
