using System.Text.Json;
using System.Text.Json.Serialization;
using Mashil.Domain;
using static Mashil.Api.Endpoints.ModerationEndpoints;

namespace Mashil.Api.Tests;

/// <summary>댓글 신고 사유 검증 (#17). API 와 같은 JSON 설정(웹 기본값 + 문자열 enum)으로 역직렬화한다.</summary>
public class ReportReasonValidationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static ReportInput Parse(string body) => JsonSerializer.Deserialize<ReportInput>(body, Json)!;

    [Theory]
    [InlineData("{\"reason\":\"Spam\"}")]
    [InlineData("{\"reason\":\"Other\",\"detail\":\"x\"}")]
    [InlineData("{\"reason\":0}")]
    [InlineData("{\"reason\":3}")]
    public void Defined_reasons_are_accepted(string body) => Assert.True(Parse(body).HasValidReason);

    [Theory]
    [InlineData("{\"reason\":9}")]
    [InlineData("{\"reason\":-1}")]
    [InlineData("{\"reason\":4}")]
    public void Undefined_numeric_reason_is_rejected(string body) => Assert.False(Parse(body).HasValidReason);

    [Fact]
    public void Unknown_reason_name_fails_binding() =>
        Assert.Throws<JsonException>(() => Parse("{\"reason\":\"Foo\"}"));
}
