using Mashil.Infrastructure.Identity;

namespace Mashil.Worker;

/// <summary>
/// 탈퇴 시 바로 끊지 못한 IdP 연결(Kakao unlink, Apple revoke)을 재시도한다 (SD-08).
/// 대부분은 API 가 탈퇴 직후 처리하므로 대기열은 보통 비어 있다.
/// </summary>
public sealed class IdpRevocationWorker(
    ILogger<IdpRevocationWorker> logger,
    IServiceScopeFactory scopeFactory,
    IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("OAuth:RevocationIntervalMinutes", 10)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IdpRevocationProcessor>();
                var n = await processor.ProcessDueAsync(50, stoppingToken);
                if (n > 0) logger.LogInformation("IdP 연결 해제 재시도 {Count}건 처리", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "IdP 연결 해제 재시도 실패");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
