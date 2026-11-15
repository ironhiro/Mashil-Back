using Mashil.Domain;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.Admin;

public sealed record ValueMappingView(long Id, string FieldName, string RawKey, string CanonicalJson, bool IsActive, Guid? CreatedById, DateTime CreatedAt);

/// <summary>매핑을 추가·해제한 결과. 영향받는 화장실은 이어서 다시 정규화해야 한다.</summary>
public sealed record MappingChange(ValueMappingView Mapping, int IssuesChanged, IReadOnlyList<long> AffectedToiletIds);

/// <summary>
/// 매핑 사전 (DATA-13): 관리자가 "필드 + 원본 문자열 → 표준 값" 규칙을 추가·해제한다.
/// 지금 매핑을 읽는 정규화기는 개방시간뿐이라 <see cref="IssueFields.OpeningHours"/> 만 받는다.
/// 키는 <see cref="OpeningHoursParser.MappingKey"/> (= 개방시간 이슈의 원본 값), 값은 <see cref="HoursOutcome.ToJson"/> 형식.
/// </summary>
public static class ValueMappingService
{
    public static readonly IReadOnlySet<string> SupportedFields = new HashSet<string> { IssueFields.OpeningHours };

    public static ValueMappingView View(ValueMapping m) => new(m.Id, m.FieldName, m.RawKey, m.CanonicalJson, m.IsActive, m.CreatedById, m.CreatedAt);

    public static async Task<IReadOnlyList<ValueMappingView>> ListAsync(MashilDbContext db, string? field, bool includeInactive, CancellationToken ct)
    {
        var q = db.ValueMappings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(field)) q = q.Where(m => m.FieldName == field);
        if (!includeInactive) q = q.Where(m => m.IsActive);
        return (await q.OrderByDescending(m => m.Id).Take(500).ToListAsync(ct)).Select(View).ToList();
    }

    /// <summary>표준 값 JSON 을 검사하고 저장 형식으로 맞춘다. 틀리면 <see cref="FormatException"/>.</summary>
    public static string NormalizeCanonical(string field, string canonicalJson)
    {
        if (!SupportedFields.Contains(field)) throw new FormatException($"매핑을 지원하지 않는 필드입니다: {field}");
        HoursOutcome outcome;
        try
        {
            outcome = HoursOutcome.FromJson(canonicalJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FormatException("개방시간 값 형식이 맞지 않습니다: " + ex.Message);
        }
        if (outcome.Status == ScheduleStatus.Scheduled && outcome.Entries.Count == 0)
            throw new FormatException("시간대(Scheduled)는 요일별 시간이 하나 이상 있어야 합니다");
        if (outcome.Status != ScheduleStatus.Scheduled && outcome.Entries.Count > 0)
            throw new FormatException("요일별 시간은 Scheduled 일 때만 적습니다");
        foreach (var e in outcome.Entries)
        {
            if (e.Closed) continue;
            if (e.Open is not (>= 0 and <= 1440) || e.Close is not (>= 0 and <= 1440))
                throw new FormatException($"{e.Day} 시간 범위가 맞지 않습니다 (분 단위 0~1440, 닫는 시각이 여는 시각보다 이르면 자정을 넘김)");
        }
        return new HoursOutcome(outcome.Status, outcome.Entries).ToJson();
    }

    /// <summary>
    /// 매핑을 추가한다. 같은 키의 활성 매핑은 끈다. 같은 원본 값의 Open 이슈는 이 매핑으로 해결(Resolved) 처리한다.
    /// </summary>
    public static async Task<MappingChange> CreateAsync(MashilDbContext db, string field, string rawKey, string canonicalJson,
        Guid? adminId, DateTime now, CancellationToken ct)
    {
        var canonical = NormalizeCanonical(field, canonicalJson);
        if (string.IsNullOrWhiteSpace(rawKey)) throw new FormatException("원본 값(rawKey)이 비어 있습니다");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ValueMappings.Where(m => m.FieldName == field && m.RawKey == rawKey && m.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false), ct);

        var mapping = new ValueMapping { FieldName = field, RawKey = rawKey, CanonicalJson = canonical, CreatedById = adminId, CreatedAt = now };
        db.ValueMappings.Add(mapping);
        await db.SaveChangesAsync(ct);

        var resolved = await db.NormalizationIssues
            .Where(i => i.FieldName == field && i.RawValue == rawKey && i.Status == IssueStatus.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IssueStatus.Resolved)
                .SetProperty(i => i.ResolvedByMappingId, mapping.Id)
                .SetProperty(i => i.ResolvedById, adminId)
                .SetProperty(i => i.ResolvedAt, now), ct);
        await tx.CommitAsync(ct);

        return new MappingChange(View(mapping), resolved, await MarkOutdatedAsync(db, await AffectedToiletIdsAsync(db, field, rawKey, ct), ct));
    }

    /// <summary>매핑을 끈다. 이 매핑으로 해결된 이슈는 다시 Open 으로 (재정규화가 다시 판단한다).</summary>
    public static async Task<MappingChange?> DeactivateAsync(MashilDbContext db, long id, CancellationToken ct)
    {
        var mapping = await db.ValueMappings.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (mapping is null) return null;
        mapping.IsActive = false;
        await db.SaveChangesAsync(ct);
        var reopened = await db.NormalizationIssues
            .Where(i => i.ResolvedByMappingId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IssueStatus.Open)
                .SetProperty(i => i.ResolvedByMappingId, (long?)null)
                .SetProperty(i => i.ResolvedById, (Guid?)null)
                .SetProperty(i => i.ResolvedAt, (DateTime?)null), ct);
        return new MappingChange(View(mapping), reopened, await MarkOutdatedAsync(db, await AffectedToiletIdsAsync(db, mapping.FieldName, mapping.RawKey, ct), ct));
    }

    /// <summary>
    /// 대상 화장실의 정규화 버전을 비운다. 바로 이어지는 재정규화 작업이 끊겨도(API 재배포 등)
    /// "재정규화 (바뀐 것만)"이나 다음 임포트가 이 화장실들을 다시 정규화하게 하는 표시.
    /// </summary>
    private static async Task<IReadOnlyList<long>> MarkOutdatedAsync(MashilDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(1000))
            await db.Toilets.Where(t => chunk.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.NormalizerVersion, (string?)null), ct);
        return ids;
    }

    /// <summary>
    /// 이 키로 정규화되는 화장실: 같은 원본 값의 이슈가 있었던 곳 + 지금 개방시간 문장이 같은 키가 되는 곳.
    /// 키는 공백을 정리한 문자열이라 SQL 로 바로 비교하기 어려워 개방시간 두 칸만 읽어 메모리에서 비교한다 (약 5만 행, 짧은 문자열).
    /// </summary>
    public static async Task<IReadOnlyList<long>> AffectedToiletIdsAsync(MashilDbContext db, string field, string rawKey, CancellationToken ct)
    {
        if (field != IssueFields.OpeningHours) return [];
        var ids = new HashSet<long>(await db.NormalizationIssues
            .Where(i => i.FieldName == field && i.RawValue == rawKey)
            .Select(i => i.ToiletId).Distinct().ToListAsync(ct));

        await foreach (var t in db.Toilets.AsNoTracking()
                           .Where(t => t.OpenHours != null || t.OpenHoursDetail != null)
                           .Select(t => new { t.Id, t.OpenHours, t.OpenHoursDetail })
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            if (OpeningHoursParser.MappingKey(t.OpenHours, t.OpenHoursDetail) == rawKey) ids.Add(t.Id);
        }
        return ids.Order().ToList();
    }
}
