using Mashil.Infrastructure;
using Mashil.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mashil.Api.Ops;

/// <summary>
/// 도보 경로가 켜져 있는데 도로망(walk_edges)이 비어 있으면 Error 로그 (Sentry 를 켜면 경보).
/// 비어 있으면 모든 경로 요청이 422 far_from_road 가 되고 웹은 조용히 직선 안내로 바뀌어서, 이 로그가 없으면 아무도 모른다.
/// 시작 1분 뒤, 그다음 하루에 한 번. 행 하나만 보므로 가볍다.
/// </summary>
public sealed class WalkNetworkCheck(IServiceScopeFactory scopes, IOptions<WalkRoutingOptions> options, ILogger<WalkNetworkCheck> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stop);
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
            do
            {
                await CheckAsync(stop);
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
            if (!await db.WalkEdges.AnyAsync(ct))
                logger.LogError("도보 도로망(walk_edges)이 비어 있다. 경로 요청이 모두 직선 안내로 바뀐다 — 임포트 Job 을 확인 (docs/AZURE.md)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "도보 도로망 확인 실패");
        }
    }
}
