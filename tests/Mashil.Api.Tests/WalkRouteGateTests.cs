using Mashil.Infrastructure.Routing;
using Microsoft.Extensions.Options;

namespace Mashil.Api.Tests;

public class WalkRouteGateTests
{
    private static WalkRouteGate Gate(int max, int waitMs) =>
        new(Options.Create(new WalkRoutingOptions { MaxConcurrent = max, QueueWaitMs = waitMs }));

    [Fact]
    public async Task Refuses_when_full_and_frees_on_dispose()
    {
        var g = Gate(1, 0);
        var a = await g.EnterAsync(default);
        Assert.NotNull(a);
        Assert.Null(await g.EnterAsync(default)); // 가득 참
        a!.Dispose();
        a.Dispose(); // 두 번 돌려줘도 자리가 늘지 않는다
        var b = await g.EnterAsync(default);
        Assert.NotNull(b);
        Assert.Null(await g.EnterAsync(default));
        b!.Dispose();
    }

    [Fact]
    public async Task Waits_for_a_slot_within_the_queue_time()
    {
        var g = Gate(1, 2_000);
        var a = await g.EnterAsync(default);
        var waiting = g.EnterAsync(default);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        a!.Dispose();
        Assert.NotNull(await waiting);
    }
}
