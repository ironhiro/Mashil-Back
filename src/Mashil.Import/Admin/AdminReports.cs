using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.Admin;

/// <summary>
/// 승인한 변경 요청이 실제로 남긴 것 (ADM-01). 오버라이드의 change_request_id 와 추가 요청의 관리번호(USR-{id})로 찾는다.
/// <see cref="ActiveFields"/> 는 지금도 쓰이는 필드 (뒤의 요청이나 관리자 수정으로 꺼졌으면 빠짐).
/// </summary>
public sealed record ChangeRequestOutcome(long ChangeRequestId, long? ToiletId, bool CreatedToilet, bool MarkedRemoved,
    IReadOnlyList<string> Fields, IReadOnlyList<string> ActiveFields);

public sealed record FieldRate(string Field, int Total, int Ok, double Rate, IReadOnlyDictionary<string, int> Breakdown);

/// <summary>정규화 리포트 (DATA-16): 필드별 성공률과 상위 미해결 원본 값.</summary>
public sealed record NormalizationReport(int Toilets, IReadOnlyList<FieldRate> Fields, IReadOnlyList<IssueGroup> TopUnresolved, DateTime GeneratedAt);

/// <summary>실행 1회 리포트 (DATA-23): 실행 행 + 그 실행 동안 새로 생긴 이슈의 유형별 건수.</summary>
public sealed record ImportRunReport(ImportRun Run, IReadOnlyList<IssueTypeCount> NewIssuesByType);

public static class AdminReports
{
    public static async Task<IReadOnlyDictionary<long, ChangeRequestOutcome>> ChangeRequestOutcomesAsync(
        MashilDbContext db, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<long, ChangeRequestOutcome>();
        var list = ids.ToList();
        var overrides = await db.ToiletOverrides.AsNoTracking()
            .Where(o => o.ChangeRequestId != null && list.Contains(o.ChangeRequestId!.Value))
            .Select(o => new { CrId = o.ChangeRequestId!.Value, o.ToiletId, o.FieldName, o.ValueJson, o.IsActive })
            .ToListAsync(ct);
        var keys = list.Select(id => $"USR-{id}").ToList();
        var added = await db.Toilets.AsNoTracking().Where(t => keys.Contains(t.ManagementNo))
            .Select(t => new { t.Id, t.ManagementNo }).ToListAsync(ct);

        var result = new Dictionary<long, ChangeRequestOutcome>();
        foreach (var g in overrides.GroupBy(o => o.CrId))
        {
            var removed = g.Any(o => o.FieldName == OverrideFields.Status && o.ValueJson.Contains(nameof(ToiletStatus.Removed)));
            result[g.Key] = new ChangeRequestOutcome(g.Key, g.First().ToiletId, false, removed,
                g.Select(o => o.FieldName).Distinct().ToList(), g.Where(o => o.IsActive).Select(o => o.FieldName).Distinct().ToList());
        }
        foreach (var t in added)
        {
            var crId = long.Parse(t.ManagementNo[4..]);
            var fields = result.TryGetValue(crId, out var r) ? r.Fields : [];
            var active = r?.ActiveFields ?? [];
            result[crId] = new ChangeRequestOutcome(crId, t.Id, true, false, fields, active);
        }
        return result;
    }

    public static async Task<ImportRunReport?> RunReportAsync(MashilDbContext db, long runId, CancellationToken ct)
    {
        var run = await db.ImportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return null;
        var end = run.FinishedAt ?? DateTime.UtcNow;
        // 이슈는 다시 계산돼도 같은 것이면 접수 시각을 유지하므로(IssueSync), 실행 시간 안에 생긴 것 = 새 이슈.
        // 지오코딩 이슈는 Worker 가 따로 만들므로 뺀다.
        var byType = await db.NormalizationIssues.AsNoTracking()
            .Where(i => i.CreatedAt >= run.StartedAt && i.CreatedAt <= end && i.FieldName != IssueFields.Geocode)
            .GroupBy(i => new { i.FieldName, i.Type, i.Status })
            .Select(g => new { g.Key.FieldName, g.Key.Type, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return new ImportRunReport(run, byType.Select(x => new IssueTypeCount(x.FieldName, x.Type, x.Status, x.Count))
            .OrderByDescending(x => x.Count).ToList());
    }

    /// <summary>Removed 를 뺀 화장실 기준 필드별 성공률 + Open 이슈의 상위 원본 값 50개.</summary>
    public static async Task<NormalizationReport> NormalizationReportAsync(MashilDbContext db, DateTime now, CancellationToken ct)
    {
        var live = db.Toilets.AsNoTracking().Where(t => t.Status != ToiletStatus.Removed);
        var total = await live.CountAsync(ct);

        var hours = (await live.GroupBy(t => t.ScheduleStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key.ToString(), x => x.Count);
        var hoursOk = hours.Where(kv => kv.Key != nameof(ScheduleStatus.Unknown)).Sum(kv => kv.Value);

        var kinds = (await live.GroupBy(t => t.KindCode).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key.ToString(), x => x.Count);
        var kindOk = kinds.Where(kv => kv.Key != nameof(ToiletKind.Other)).Sum(kv => kv.Value);

        var phones = await live.CountAsync(t => t.Phone != null, ct);
        var badPhones = await live.CountAsync(t => t.Phone != null && t.Issues.Any(i => i.Type == IssueType.InvalidPhone && i.Status == IssueStatus.Open), ct);

        var coords = (await live.GroupBy(t => t.CoordinateSource).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key.ToString(), x => x.Count);
        var located = await live.CountAsync(t => t.Location != null, ct);

        static FieldRate Rate(string field, int n, int ok, IReadOnlyDictionary<string, int> breakdown) =>
            new(field, n, ok, n == 0 ? 0 : Math.Round(ok * 100.0 / n, 1), breakdown);

        var top = await IssueQueue.GroupsAsync(db, new IssueFilter(Status: IssueStatus.Open), 0, 50, ct);
        return new NormalizationReport(total,
        [
            Rate(IssueFields.OpeningHours, total, hoursOk, hours),
            Rate(IssueFields.Kind, total, kindOk, kinds),
            Rate(IssueFields.Phone, phones, phones - badPhones, new Dictionary<string, int>()),
            Rate("location", total, located, coords),
        ], top.Items, now);
    }
}
