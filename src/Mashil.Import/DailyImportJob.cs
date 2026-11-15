using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mashil.Import;

/// <summary>
/// 일일 갱신 1회 (DATA-18, SD-12): 다운로드 → 임포트 → 결과별 알림 → 신선도 확인.
/// 스케줄은 호스트(Mashil.Worker)가 정한다. 이 클래스는 한 번 실행만 책임진다.
/// </summary>
public sealed class DailyImportJob(
    MashilDbContext db,
    LocalDataFileFetcher fetcher,
    IAlertNotifier alerts,
    ImportPolicy policy,
    ILogger<DailyImportJob> logger,
    TimeProvider? clock = null)
{
    /// <summary>마지막 성공이 이보다 오래되면 경고 (NFR-14).</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(48);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ImportRun?> ExecuteOnceAsync(string sourceCode, ImportTrigger trigger, CancellationToken ct)
    {
        var source = await db.DataSources.AsNoTracking().SingleAsync(s => s.Code == sourceCode, ct);

        SourceFile file;
        try
        {
            logger.LogInformation("{Source} 다운로드 시작", sourceCode);
            file = await fetcher.FetchAsync(source, ct);
            logger.LogInformation("다운로드 완료: {Name} {Bytes:N0} bytes, {Encoding}", file.FileName, file.Bytes.LongLength, file.Encoding.ToLabel());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var run = await RecordFetchFailureAsync(source.Id, trigger, ex, ct);
            await alerts.NotifyAsync(AlertLevel.Error, "공중화장실 데이터 다운로드 실패",
                $"{ex.Message}\n기존 데이터를 유지합니다. 관리자 화면에서 파일을 직접 올려 처리할 수 있습니다.", ct);
            await CheckStalenessAsync(source.Id, ct);
            return run;
        }

        ImportRun result;
        try
        {
            result = await new ImportRunner(db, policy, _clock).RunAsync(sourceCode, file, trigger, ct: ct);
        }
        catch (ImportBusyException ex)
        {
            logger.LogInformation("{Message} — 이번 회차는 건너뜀", ex.Message);
            return null;
        }

        await NotifyResultAsync(result, ct);
        await CheckStalenessAsync(source.Id, ct);
        return result;
    }

    private async Task NotifyResultAsync(ImportRun r, CancellationToken ct)
    {
        var summary = $"run #{r.Id} · 전체 {r.TotalRows:N0} · 신규 {r.InsertedRows:N0} · 변경 {r.UpdatedRows:N0} · 동일 {r.UnchangedRows:N0}"
            + $" · 누락 {r.MissingRows:N0} · 복귀 {r.RestoredRows:N0} · 삭제 {r.RemovedRows:N0}/{r.RemovalCandidates:N0}";

        switch (r.Status)
        {
            case ImportRunStatus.Succeeded:
                logger.LogInformation("임포트 성공: {Summary}", summary);
                if (r.InsertedRows + r.UpdatedRows + r.MissingRows + r.RemovedRows > 0)
                    await alerts.NotifyAsync(AlertLevel.Info, "공중화장실 데이터 갱신", summary, ct); // DATA-23 일일 변경 리포트
                break;
            case ImportRunStatus.Skipped:
                logger.LogInformation("변경 없음 (같은 파일): run #{Id}", r.Id);
                break;
            case ImportRunStatus.PendingApproval:
                await alerts.NotifyAsync(AlertLevel.Warning, "대량 삭제 보류 — 관리자 승인 필요", $"{r.Message}\n{summary}", ct);
                break;
            case ImportRunStatus.Rejected:
                await alerts.NotifyAsync(AlertLevel.Warning, "사전 검증 거부 — 기존 데이터 유지", $"{r.Message}\n{summary}", ct);
                break;
            default:
                await alerts.NotifyAsync(AlertLevel.Error, "임포트 실패", $"{r.Status}: {r.Message}\n{summary}", ct);
                break;
        }
    }

    private async Task<ImportRun> RecordFetchFailureAsync(int sourceId, ImportTrigger trigger, Exception ex, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var run = new ImportRun
        {
            SourceId = sourceId,
            Trigger = trigger,
            Status = ImportRunStatus.Failed,
            FileSha256 = "",
            NormalizerVersion = ImportRunner.NormalizerVersion,
            Message = "다운로드 실패: " + ex.Message,
            StartedAt = now,
            FinishedAt = now,
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task CheckStalenessAsync(int sourceId, CancellationToken ct)
    {
        var last = await ImportRunner.LastAppliedAsync(db, sourceId, ct);
        var lastOk = await db.ImportRuns.AsNoTracking()
            .Where(r => r.SourceId == sourceId && (r.Status == ImportRunStatus.Succeeded || r.Status == ImportRunStatus.Skipped || r.Status == ImportRunStatus.PendingApproval))
            .MaxAsync(r => (DateTime?)r.StartedAt, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        if (lastOk is null || now - lastOk > StaleAfter)
        {
            var when = lastOk is null ? "기록 없음" : $"{(now - lastOk.Value).TotalHours:N0}시간 전";
            await alerts.NotifyAsync(AlertLevel.Warning, "공중화장실 데이터가 오래됨",
                $"마지막 정상 갱신: {when} (기준 {StaleAfter.TotalHours:N0}시간). 마지막 반영 run #{last?.Id.ToString() ?? "-"}", ct);
        }
    }
}
