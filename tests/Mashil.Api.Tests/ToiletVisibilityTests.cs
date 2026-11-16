using Mashil.Api.Endpoints;
using Mashil.Domain;

namespace Mashil.Api.Tests;

/// <summary>삭제 승인된 화장실과 제보로 추가된 화장실의 공개 표시 (#18).</summary>
public class ToiletVisibilityTests
{
    private static Toilet T(long id, ToiletStatus status) => new() { Id = id, ManagementNo = $"K{id}", Name = "x", Kind = "x", Status = status };

    [Fact]
    public void Removed_toilets_are_hidden_but_missing_ones_stay_visible()
    {
        var toilets = new[] { T(1, ToiletStatus.Active), T(2, ToiletStatus.Missing), T(3, ToiletStatus.Removed) };
        var visible = toilets.AsQueryable().Where(ToiletEndpoints.IsVisible).Select(t => t.Id).ToList();
        Assert.Equal([1L, 2L], visible);
    }

    [Theory]
    [InlineData(ToiletOrigin.PublicData, "LOCALDATA")]
    [InlineData(ToiletOrigin.UserSubmitted, "USER_SUBMITTED")]
    public void Source_provider_follows_origin(ToiletOrigin origin, string provider) =>
        Assert.Equal(provider, ToiletEndpoints.SourceProvider(origin));
}
