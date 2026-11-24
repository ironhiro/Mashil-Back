using Mashil.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace Mashil.Api.Tests;

public class DeviceKeyTests
{
    private static HttpRequest With(string? value)
    {
        var ctx = new DefaultHttpContext();
        if (value is not null) ctx.Request.Headers[DeviceKey.Header] = value;
        return ctx.Request;
    }

    [Fact]
    public void Uuid_header_becomes_a_stable_hash_not_the_raw_key()
    {
        const string key = "5f0c1e8a-3b9d-4c2e-9a71-0d6f2b4c8e13";
        var a = DeviceKey.TryReadHash(With(key));
        var b = DeviceKey.TryReadHash(With(key.ToUpperInvariant()));
        Assert.NotNull(a);
        Assert.Equal(64, a!.Length);
        Assert.Equal(a, b); // 대소문자만 다른 같은 키
        Assert.DoesNotContain("5f0c1e8a", a);
        Assert.NotEqual(a, DeviceKey.TryReadHash(With("6f0c1e8a-3b9d-4c2e-9a71-0d6f2b4c8e13")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Missing_or_malformed_header_is_no_owner(string? value) =>
        Assert.Null(DeviceKey.TryReadHash(With(value)));
}
