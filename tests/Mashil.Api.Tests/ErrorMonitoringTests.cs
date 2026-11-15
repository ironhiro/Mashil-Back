using Mashil.Api.Ops;
using Sentry;

namespace Mashil.Api.Tests;

/// <summary>Sentry 가 통신을 깨지 않게 하는 설정 (추적 헤더 미전파, 잘못된 DSN 무시).</summary>
public class ErrorMonitoringTests
{
    [Theory]
    [InlineData("https://abc123@o1.ingest.sentry.io/456", true)]
    [InlineData(" https://abc123@o1.ingest.sentry.io/456 ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc123", false)]
    [InlineData("https://o1.ingest.sentry.io/456", false)]   // 공개 키 없음
    [InlineData("https://abc123@o1.ingest.sentry.io/", false)] // 프로젝트 id 없음
    public void Only_well_formed_dsn_turns_sentry_on(string? dsn, bool expected) =>
        Assert.Equal(expected, ErrorMonitoring.IsValidDsn(dsn));

    [Fact]
    public void Outgoing_requests_get_no_trace_headers()
    {
        var o = new SentryOptions();
        Assert.NotEmpty(o.TracePropagationTargets); // 기본은 모든 요청에 붙인다 (.*)
        ErrorMonitoring.KeepOutOfTraffic(o);
        Assert.Empty(o.TracePropagationTargets);
        Assert.True(o.DisableSentryHttpMessageHandler);
        Assert.False(o.PropagateTraceparent);
        Assert.Equal(0, o.TracesSampleRate);
    }
}
