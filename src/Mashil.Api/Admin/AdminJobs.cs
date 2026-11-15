using System.Collections.Concurrent;
using System.Threading.Channels;
using Mashil.Domain;
using Mashil.Import;

namespace Mashil.Api.Admin;

public enum AdminJobKind { Upload, Fetch, Renormalize, MappingRenormalize }

public enum AdminJobState { Queued, Running, Succeeded, Failed, Busy }

/// <summary>관리자 화면에 보여 주는 작업 한 건.</summary>
public sealed record AdminJobView(
    Guid Id, AdminJobKind Kind, AdminJobState State, string Label, Guid? RequestedById,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, long? RunId, ImportRunStatus? RunStatus, string? Message, int Attempts);

/// <summary>큐에 넣는 작업. <see cref="Work"/> 는 새 DI 스코프에서 실행되고, 만든 임포트 실행(import_runs)을 돌려준다.</summary>
public sealed class AdminJob(AdminJobKind kind, string label, Guid? requestedById, Func<IServiceProvider, CancellationToken, Task<ImportRun?>> work)
{
    public Guid Id { get; } = Guid.NewGuid();
    public AdminJobKind Kind { get; } = kind;
    public string Label { get; } = label;
    public Guid? RequestedById { get; } = requestedById;
    public Func<IServiceProvider, CancellationToken, Task<ImportRun?>> Work { get; } = work;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public volatile AdminJobState State = AdminJobState.Queued;
    public DateTime? StartedAt;
    public DateTime? FinishedAt;
    public long? RunId;
    public ImportRunStatus? RunStatus;
    public string? Message;
    public int Attempts;

    public bool IsActive => State is AdminJobState.Queued or AdminJobState.Running;

    public AdminJobView View() => new(Id, Kind, State, Label, RequestedById, CreatedAt, StartedAt, FinishedAt, RunId, RunStatus, Message, Attempts);
}

/// <summary>
/// 관리자가 시킨 긴 데이터 작업(파일 업로드 임포트, 즉시 다운로드, 재정규화)의 큐 (ADM-05).
/// <para>
/// <b>설계:</b> API 프로세스 안의 단일 소비자 큐(<see cref="AdminJobRunner"/>). 요청은 바로 202 와 작업 id 를 받고, 진행·결과는
/// <c>import_runs</c>(heartbeat 포함)와 <c>GET /api/admin/jobs</c> 로 본다. 한 번에 하나만 실행하고, 실제 배타는 임포트와 같은
/// advisory lock(<see cref="ImportLock"/>)이 맡는다 — Worker 의 일일 임포트나 다른 API 인스턴스와 겹치면 <see cref="AdminJobState.Busy"/>.
/// </para>
/// <para>
/// 작업 상태는 이 인스턴스 메모리에만 있다 (최근 50건). API 를 재배포하면 진행 중이던 작업은 끊기지만, 실행 행은 다음 실행이
/// Abandoned 로 정리하고 임포트·재정규화는 멱등이라 다시 시키면 같은 결과가 된다 (DATA-22). 매핑 사전 변경분은 대상 화장실의
/// 정규화 버전을 비워 두므로, 작업이 끊겨도 "재정규화 (바뀐 것만)"이나 다음 일일 임포트가 마저 처리한다.
/// </para>
/// </summary>
public sealed class AdminJobQueue
{
    private const int Keep = 50;
    private readonly Channel<AdminJob> _channel = Channel.CreateUnbounded<AdminJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, AdminJob> _jobs = new();
    private readonly Lock _gate = new();

    public ChannelReader<AdminJob> Reader => _channel.Reader;

    /// <summary>
    /// 큐에 넣는다. <paramref name="exclusive"/> 면 이미 대기·실행 중인 작업이 있을 때 넣지 않는다 (업로드·다운로드·전체 재정규화).
    /// 매핑 재정규화처럼 이미 저장된 변경을 마저 반영하는 작업은 뒤에 줄을 세운다.
    /// </summary>
    public bool TryEnqueue(AdminJob job, bool exclusive, out AdminJob? blocking)
    {
        lock (_gate)
        {
            blocking = exclusive ? _jobs.Values.Where(j => j.IsActive).OrderBy(j => j.CreatedAt).FirstOrDefault() : null;
            if (blocking is not null) return false;
            _jobs[job.Id] = job;
            Trim();
        }
        _channel.Writer.TryWrite(job);
        return true;
    }

    /// <summary>잠시 뒤 다시 줄 세우기 (락이 잡혀 있어 못 한 매핑 재정규화).</summary>
    public void Requeue(AdminJob job)
    {
        job.State = AdminJobState.Queued;
        _channel.Writer.TryWrite(job);
    }

    public AdminJob? Find(Guid id) => _jobs.GetValueOrDefault(id);

    public IReadOnlyList<AdminJobView> Recent() => _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(j => j.View()).ToList();

    private void Trim()
    {
        foreach (var old in _jobs.Values.Where(j => !j.IsActive).OrderByDescending(j => j.CreatedAt).Skip(Keep).ToList())
            _jobs.TryRemove(old.Id, out _);
    }
}

/// <summary>큐의 작업을 하나씩 실행하는 백그라운드 서비스.</summary>
public sealed class AdminJobRunner(AdminJobQueue queue, IServiceScopeFactory scopes, ILogger<AdminJobRunner> logger) : BackgroundService
{
    /// <summary>매핑 재정규화가 락 때문에 못 돌면 이만큼 기다렸다 다시 (최대 <see cref="MaxMappingAttempts"/>회).</summary>
    public static TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public const int MaxMappingAttempts = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            job.State = AdminJobState.Running;
            job.Attempts++;
            job.StartedAt = DateTime.UtcNow;
            job.Message = null;
            try
            {
                using var scope = scopes.CreateScope();
                var run = await job.Work(scope.ServiceProvider, stoppingToken);
                job.RunId = run?.Id;
                job.RunStatus = run?.Status;
                job.Message = run?.Message;
                job.State = run is null || run.Status is ImportRunStatus.Succeeded or ImportRunStatus.Skipped or ImportRunStatus.PendingApproval
                    ? AdminJobState.Succeeded
                    : AdminJobState.Failed;
                logger.LogInformation("관리자 작업 {Kind} {Label}: {State} (run #{RunId} {RunStatus})", job.Kind, job.Label, job.State, job.RunId, job.RunStatus);
            }
            catch (ImportBusyException ex)
            {
                job.Message = ex.Message;
                if (job.Kind == AdminJobKind.MappingRenormalize && job.Attempts < MaxMappingAttempts)
                {
                    job.State = AdminJobState.Queued;
                    job.Message += $" · {BusyRetryDelay.TotalSeconds:N0}초 뒤 다시 시도";
                    _ = RetryLaterAsync(job, stoppingToken);
                }
                else job.State = AdminJobState.Busy;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.State = AdminJobState.Failed;
                job.Message = "서버가 종료돼 중단됨. 다시 실행하면 같은 결과가 됩니다.";
                break;
            }
            catch (Exception ex)
            {
                job.State = AdminJobState.Failed;
                job.Message = ex.GetType().Name + ": " + ex.Message;
                logger.LogError(ex, "관리자 작업 {Kind} {Label} 실패", job.Kind, job.Label);
            }
            finally
            {
                if (job.State != AdminJobState.Queued) job.FinishedAt = DateTime.UtcNow;
            }
        }
    }

    private async Task RetryLaterAsync(AdminJob job, CancellationToken ct)
    {
        try
        {
            await Task.Delay(BusyRetryDelay, ct);
            queue.Requeue(job);
        }
        catch (OperationCanceledException) { }
    }
}
