using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.Admin;

/// <summary>정규화 이슈 큐 필터. 비운 항목은 조건 없음.</summary>
/// <param name="NullRawValue">true 면 원본 값이 비어 있는(null) 이슈만 — <paramref name="RawValue"/> null 은 "조건 없음"이라 따로 둔다.</param>
public sealed record IssueFilter(string? FieldName = null, IssueType? Type = null, IssueStatus? Status = null, string? RawValue = null, long? ToiletId = null, bool NullRawValue = false);

public sealed record IssueRow(
    long Id, long ToiletId, string ToiletName, string FieldName, IssueType Type, IssueSeverity Severity,
    string? RawValue, string? Note, IssueStatus Status, long? ResolvedByMappingId, DateTime CreatedAt, DateTime? ResolvedAt);

/// <summary>같은 (필드, 유형, 원본 값) 묶음. 일괄 처리와 매핑 사전 등록의 단위.</summary>
public sealed record IssueGroup(string FieldName, IssueType Type, string? RawValue, int Count, string? Note, long SampleToiletId, DateTime FirstSeenAt);

public sealed record IssueTypeCount(string FieldName, IssueType Type, IssueStatus Status, int Count);

public sealed record Paged<T>(int Total, IReadOnlyList<T> Items);

/// <summary>
/// 정규화 이슈 큐 (DATA-12, ADM-03): 유형·상태 필터, 같은 원본 값 묶음, 일괄 해결·무시.
/// 해결·무시한 이슈는 재임포트·재정규화가 다시 만들지 않는다 (<see cref="Normalization.IssueSync"/>).
/// </summary>
public static class IssueQueue
{
    private static IQueryable<NormalizationIssue> Apply(IQueryable<NormalizationIssue> q, IssueFilter f)
    {
        if (!string.IsNullOrWhiteSpace(f.FieldName)) q = q.Where(i => i.FieldName == f.FieldName);
        if (f.Type is { } type) q = q.Where(i => i.Type == type);
        if (f.Status is { } status) q = q.Where(i => i.Status == status);
        if (f.RawValue is not null) q = q.Where(i => i.RawValue == f.RawValue);
        else if (f.NullRawValue) q = q.Where(i => i.RawValue == null);
        if (f.ToiletId is { } id) q = q.Where(i => i.ToiletId == id);
        return q;
    }

    public static async Task<Paged<IssueRow>> ListAsync(MashilDbContext db, IssueFilter f, int offset, int limit, CancellationToken ct)
    {
        var q = Apply(db.NormalizationIssues.AsNoTracking(), f);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Skip(offset).Take(limit)
            .Select(i => new IssueRow(i.Id, i.ToiletId, i.Toilet!.Name, i.FieldName, i.Type, i.Severity, i.RawValue, i.Note,
                i.Status, i.ResolvedByMappingId, i.CreatedAt, i.ResolvedAt))
            .ToListAsync(ct);
        return new Paged<IssueRow>(total, items);
    }

    /// <summary>같은 원본 값 묶음, 건수 많은 순 (DATA-16 의 상위 미해결 원본 값도 이것).</summary>
    public static async Task<Paged<IssueGroup>> GroupsAsync(MashilDbContext db, IssueFilter f, int offset, int limit, CancellationToken ct)
    {
        var grouped = Apply(db.NormalizationIssues.AsNoTracking(), f)
            .GroupBy(i => new { i.FieldName, i.Type, i.RawValue });
        var total = await grouped.CountAsync(ct);
        var rows = await grouped
            .Select(g => new
            {
                g.Key.FieldName, g.Key.Type, g.Key.RawValue,
                Count = g.Count(), Note = g.Max(i => i.Note), Sample = g.Min(i => i.ToiletId), First = g.Min(i => i.CreatedAt),
            })
            .OrderByDescending(g => g.Count).ThenBy(g => g.RawValue)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);
        return new Paged<IssueGroup>(total, rows.Select(g => new IssueGroup(g.FieldName, g.Type, g.RawValue, g.Count, g.Note, g.Sample, g.First)).ToList());
    }

    /// <summary>필드·유형·상태별 건수 (필터 칸에 숫자를 붙이려고).</summary>
    public static async Task<IReadOnlyList<IssueTypeCount>> CountsAsync(MashilDbContext db, CancellationToken ct) =>
        (await db.NormalizationIssues.AsNoTracking()
            .GroupBy(i => new { i.FieldName, i.Type, i.Status })
            .Select(g => new { g.Key.FieldName, g.Key.Type, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct))
        .Select(x => new IssueTypeCount(x.FieldName, x.Type, x.Status, x.Count))
        .OrderBy(x => x.FieldName).ThenBy(x => x.Type).ThenBy(x => x.Status)
        .ToList();

    /// <summary>
    /// 이슈 상태를 한꺼번에 바꾼다. <paramref name="ids"/> 가 있으면 그 이슈들, 없으면 필터에 맞는 전부
    /// (같은 원본 값 묶음 처리: 필드·유형·원본 값과 현재 상태를 필터로 준다).
    /// Open 으로 되돌리면 처리자·처리 시각·매핑 연결을 지운다.
    /// </summary>
    public static Task<int> SetStatusAsync(MashilDbContext db, IReadOnlyCollection<long>? ids, IssueFilter? filter,
        IssueStatus status, Guid? adminId, DateTime now, CancellationToken ct)
    {
        IQueryable<NormalizationIssue> q = db.NormalizationIssues;
        if (ids is { Count: > 0 }) q = q.Where(i => ids.Contains(i.Id));
        else if (filter is not null && (filter.RawValue is not null || filter.NullRawValue || filter.ToiletId is not null))
            q = Apply(q, filter);
        else throw new ArgumentException("ids, 또는 원본 값(묶음)이나 화장실을 정한 필터가 필요합니다");

        q = q.Where(i => i.Status != status);
        return status == IssueStatus.Open
            ? q.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, status)
                .SetProperty(i => i.ResolvedById, (Guid?)null)
                .SetProperty(i => i.ResolvedAt, (DateTime?)null)
                .SetProperty(i => i.ResolvedByMappingId, (long?)null), ct)
            : q.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, status)
                .SetProperty(i => i.ResolvedById, adminId)
                .SetProperty(i => i.ResolvedAt, now), ct);
    }
}
