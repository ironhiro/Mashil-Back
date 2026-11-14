using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Mashil.Import;

public enum AlertLevel { Info, Warning, Error }

/// <summary>관리자 알림 (DATA-21).</summary>
public interface IAlertNotifier
{
    Task NotifyAsync(AlertLevel level, string title, string body, CancellationToken ct = default);
}

public sealed class LoggingAlertNotifier(ILogger<LoggingAlertNotifier> logger) : IAlertNotifier
{
    public Task NotifyAsync(AlertLevel level, string title, string body, CancellationToken ct = default)
    {
        var logLevel = level switch { AlertLevel.Error => LogLevel.Error, AlertLevel.Warning => LogLevel.Warning, _ => LogLevel.Information };
        logger.Log(logLevel, "[알림] {Title}\n{Body}", title, body);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 웹훅으로 보낸다. Slack(<c>text</c>)과 Discord(<c>content</c>) 양쪽이 읽는 키를 함께 넣는다.
/// 설정: <c>Alerts:WebhookUrl</c>. 실패해도 예외를 올리지 않는다 (알림 때문에 임포트가 실패하면 안 됨).
/// </summary>
public sealed class WebhookAlertNotifier(HttpClient http, Uri webhook, ILogger<WebhookAlertNotifier> logger) : IAlertNotifier
{
    public async Task NotifyAsync(AlertLevel level, string title, string body, CancellationToken ct = default)
    {
        var text = $"[마실 {level}] {title}\n{body}";
        try
        {
            using var res = await http.PostAsJsonAsync(webhook, new { text, content = text }, ct);
            if (!res.IsSuccessStatusCode)
                logger.LogWarning("웹훅 알림 실패: HTTP {Status}", (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "웹훅 알림 실패");
        }
    }
}

public sealed class CompositeAlertNotifier(IEnumerable<IAlertNotifier> notifiers) : IAlertNotifier
{
    public Task NotifyAsync(AlertLevel level, string title, string body, CancellationToken ct = default) =>
        Task.WhenAll(notifiers.Select(n => n.NotifyAsync(level, title, body, ct)));
}
