using Mashil.Api.Endpoints;
using Mashil.Domain;
using static Mashil.Api.Endpoints.InteractionEndpoints;

namespace Mashil.Api.Tests;

/// <summary>변경 요청 입력 검증 (#16). 잘못된 입력이 500 이 아니라 400 이 되는지.</summary>
public class ChangeRequestValidationTests
{
    private static string? Validate(ChangeRequestKind kind, long? target, string? payload) =>
        ValidateChangeRequest(new ChangeRequestInput(kind, target, payload), out _);

    [Theory]
    [InlineData(ChangeRequestKind.Edit, 271L, "{\"name\":\"x\"}")]
    [InlineData(ChangeRequestKind.Edit, 271L, "{\"latitude\":37.5,\"longitude\":127.0}")]
    [InlineData(ChangeRequestKind.Delete, 271L, "{\"reason\":\"없어짐\"}")]
    [InlineData(ChangeRequestKind.Delete, 271L, null)]
    [InlineData(ChangeRequestKind.Add, null, "{\"name\":\"새 화장실\",\"roadAddress\":\"서울특별시 중구 세종대로 110\"}")]
    public void Valid_requests_pass(ChangeRequestKind kind, long? target, string? payload) =>
        Assert.Null(Validate(kind, target, payload));

    [Fact]
    public void Null_or_blank_payload_is_stored_as_empty_object()
    {
        Assert.Null(ValidateChangeRequest(new ChangeRequestInput(ChangeRequestKind.Delete, 1, "  "), out var payload));
        Assert.Equal("{}", payload);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"name\":")]
    [InlineData("[1,2]")]
    [InlineData("\"name\"")]
    [InlineData("42")]
    [InlineData("{\"name\":\"a\u0000b\"}")]
    public void Invalid_payload_is_rejected(string payload) =>
        Assert.NotNull(Validate(ChangeRequestKind.Edit, 271, payload));

    [Fact]
    public void Oversized_payload_is_rejected()
    {
        var big = "{\"note\":\"" + new string('가', MaxChangeRequestPayloadLength) + "\"}";
        Assert.Contains("at most", Validate(ChangeRequestKind.Edit, 1, big));
    }

    [Theory]
    [InlineData(ChangeRequestKind.Edit)]
    [InlineData(ChangeRequestKind.Delete)]
    public void Edit_and_delete_require_target(ChangeRequestKind kind) =>
        Assert.Contains("targetToiletId", Validate(kind, null, "{\"name\":\"x\"}"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"  \"}")]
    [InlineData("{\"name\":1}")]
    public void Add_requires_name(string payload) =>
        Assert.Contains("name", Validate(ChangeRequestKind.Add, null, payload));

    [Fact]
    public void Add_must_not_have_target() =>
        Assert.Contains("targetToiletId", Validate(ChangeRequestKind.Add, 5, "{\"name\":\"x\"}"));

    [Fact]
    public void Undefined_kind_is_rejected() =>
        Assert.Contains("kind", Validate((ChangeRequestKind)9, 1, "{}"));
}
