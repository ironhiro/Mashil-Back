using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import;

/// <summary>
/// 성공한 실행 직후에만 호출한다 (DATA-15). 검증을 통과한 "전체" 파일 기준이라야 누락을 믿을 수 있다.
/// <list type="number">
/// <item>이번 파일에 없던 원본 행: 연속 누락 +1</item>
/// <item>다시 나타난 화장실: Missing → Active</item>
/// <item>원본에서 빠진 화장실과, 원본이 아예 없는 이전 임포트 행: Active → Missing (지도에는 계속 보임)</item>
/// <item>연속 누락이 기준 이상: Removed. 단 한 번에 기준 비율을 넘으면 보류 (PendingApproval)</item>
/// </list>
/// 이전 임포트 행(원본 없음)은 자동으로 Removed 하지 않는다 — 출처가 달라 판단은 관리자 몫.
/// 상태 오버라이드가 있는 화장실은 건드리지 않는다.
/// </summary>
public static class MissingRecordProcessor
{
    public static async Task ProcessAsync(MashilDbContext db, int sourceId, ImportRun run, ImportPolicy policy, CancellationToken ct)
    {
        var runId = run.Id;

        await db.RawToiletRecords
            .Where(r => r.SourceId == sourceId && r.LastImportRunId != runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsecutiveMissing, r => r.ConsecutiveMissing + 1), ct);

        var noStatusOverride = NoStatusOverride(db);

        run.RestoredRows = await db.Toilets
            .Where(t => t.Status == ToiletStatus.Missing)
            .Where(t => db.RawToiletRecords.Any(r => r.ToiletId == t.Id && r.SourceId == sourceId && r.LastImportRunId == runId))
            .Where(noStatusOverride)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ToiletStatus.Active), ct);

        var missingFromSource = await db.Toilets
            .Where(t => t.Status == ToiletStatus.Active && t.Origin == ToiletOrigin.PublicData)
            .Where(t => db.RawToiletRecords.Any(r => r.ToiletId == t.Id && r.SourceId == sourceId && r.ConsecutiveMissing >= 1))
            .Where(noStatusOverride)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ToiletStatus.Missing), ct);

        var legacy = await db.Toilets
            .Where(t => t.Status == ToiletStatus.Active && t.Origin == ToiletOrigin.PublicData)
            .Where(t => !db.RawToiletRecords.Any(r => r.ToiletId == t.Id))
            .Where(noStatusOverride)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ToiletStatus.Missing), ct);

        run.MissingRows = missingFromSource + legacy;

        var candidates = RemovalCandidates(db, sourceId, policy);
        run.RemovalCandidates = await candidates.CountAsync(ct);

        var sourceRows = Math.Max(1, run.TotalRows - run.SkippedRows);
        if (run.RemovalCandidates > sourceRows * policy.MaxRemovalRatio)
        {
            run.Status = ImportRunStatus.PendingApproval;
            run.Message = $"삭제 대상 {run.RemovalCandidates:N0}건이 기준({policy.MaxRemovalRatio:P0})을 넘어 관리자 승인 대기";
            return;
        }

        if (run.RemovalCandidates > 0)
            run.RemovedRows = await candidates.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ToiletStatus.Removed), ct);
    }

    /// <summary>보류된 삭제를 관리자가 승인. 가장 최근 실행이 PendingApproval 일 때만.</summary>
    public static async Task<int> ApprovePendingRemovalsAsync(MashilDbContext db, long runId, ImportPolicy policy, CancellationToken ct)
    {
        var run = await db.ImportRuns.SingleAsync(r => r.Id == runId, ct);
        if (run.Status != ImportRunStatus.PendingApproval)
            throw new InvalidOperationException($"run #{runId} 는 승인 대기 상태가 아닙니다 ({run.Status})");

        var newer = await db.ImportRuns.AnyAsync(r => r.SourceId == run.SourceId && r.Id > run.Id
            && (r.Status == ImportRunStatus.Succeeded || r.Status == ImportRunStatus.PendingApproval || r.Status == ImportRunStatus.Running), ct);
        if (newer)
            throw new InvalidOperationException($"run #{runId} 이후 새 실행이 있어 승인할 수 없습니다. 최신 실행을 확인하세요.");

        var removed = await RemovalCandidates(db, run.SourceId, policy)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ToiletStatus.Removed), ct);

        run.RemovedRows = removed;
        run.Status = ImportRunStatus.Succeeded;
        run.Message = $"관리자 승인으로 {removed:N0}건 Removed";
        await db.SaveChangesAsync(ct);
        return removed;
    }

    private static IQueryable<Toilet> RemovalCandidates(MashilDbContext db, int sourceId, ImportPolicy policy) =>
        db.Toilets
            .Where(t => t.Status != ToiletStatus.Removed && t.Origin == ToiletOrigin.PublicData)
            .Where(t => db.RawToiletRecords.Any(r => r.ToiletId == t.Id && r.SourceId == sourceId && r.ConsecutiveMissing >= policy.RemoveAfterMisses))
            .Where(NoStatusOverride(db));

    private static System.Linq.Expressions.Expression<Func<Toilet, bool>> NoStatusOverride(MashilDbContext db) =>
        t => !db.ToiletOverrides.Any(o => o.ToiletId == t.Id && o.IsActive && o.FieldName == OverrideFields.Status);
}
