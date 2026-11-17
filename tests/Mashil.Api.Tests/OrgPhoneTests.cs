using Mashil.Api.Endpoints;

namespace Mashil.Api.Tests;

/// <summary>관리기관 대표 번호: 과반이 쓰는 번호만.</summary>
public class OrgPhoneTests
{
    [Fact]
    public void Majority_number_is_picked() =>
        Assert.Equal("02-438-7209", OrgPhone.Pick([("02-438-7209", 5), ("02-438-7000", 1)]));

    [Fact]
    public void Single_number_is_picked() =>
        Assert.Equal("031-8075-4329", OrgPhone.Pick([("031-8075-4329", 1)]));

    [Fact]
    public void No_majority_means_no_number() =>
        Assert.Null(OrgPhone.Pick([("02-1", 2), ("02-2", 2)])); // 화장실마다 번호가 다른 기관

    [Fact]
    public void Nothing_to_pick_from() => Assert.Null(OrgPhone.Pick([]));
}
