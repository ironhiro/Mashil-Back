using Mashil.Domain;
using Mashil.Import.Normalization;

namespace Mashil.Import.Tests;

public class NormalizerTests
{
    [Theory]
    [InlineData("0221484057", "02-2148-4057")]
    [InlineData("022148405", "02-214-8405")]
    [InlineData("0317654321", "031-765-4321")]
    [InlineData("01012345678", "010-1234-5678")]
    [InlineData("07012345678", "070-1234-5678")]
    [InlineData("15881234", "1588-1234")]
    [InlineData("02-2148-4057", "02-2148-4057")]
    public void Phone_is_formatted(string raw, string expected)
    {
        var (phone, issue) = PhoneNormalizer.Normalize(raw);
        Assert.Equal(expected, phone);
        Assert.Null(issue);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("내선 123")]
    public void Odd_phone_is_kept_with_issue(string raw)
    {
        var (phone, issue) = PhoneNormalizer.Normalize(raw);
        Assert.Equal(raw, phone);
        Assert.Equal(IssueType.InvalidPhone, issue!.Type);
    }

    [Fact]
    public void Blank_phone_is_null_without_issue() => Assert.Equal((null, null), PhoneNormalizer.Normalize("  "));

    [Theory]
    [InlineData("공중화장실", ToiletKind.Public)]
    [InlineData("개방화장실", ToiletKind.Open)]
    [InlineData("간이화장실", ToiletKind.Temporary)]
    [InlineData("이동화장실", ToiletKind.Mobile)]
    [InlineData("공중 화장실", ToiletKind.Public)]
    public void Kind_is_coded(string raw, ToiletKind expected) => Assert.Equal(expected, KindNormalizer.Normalize(raw).Code);

    [Fact]
    public void Blank_kind_is_other_with_issue()
    {
        var (code, issue) = KindNormalizer.Normalize("");
        Assert.Equal(ToiletKind.Other, code);
        Assert.Equal(IssueType.UnknownCode, issue!.Type);
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData(" y ", true)]
    [InlineData("예", true)]
    [InlineData("유", true)]
    [InlineData("O", true)]
    [InlineData("설치", true)]
    [InlineData("N", false)]
    [InlineData("아니오", false)]
    [InlineData("무", false)]
    [InlineData("x", false)]
    [InlineData("미설치", false)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("모름", null)]
    public void YesNo_accepts_common_variants(string? raw, bool? expected) =>
        Assert.Equal(expected, Mashil.Import.LocalData.LocalDataToiletMapper.YesNo(raw));
}
