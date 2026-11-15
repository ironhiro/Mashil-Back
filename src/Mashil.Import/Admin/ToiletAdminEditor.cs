using System.Text.Json;
using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using H = Mashil.Import.LocalData.LocalDataPublicRestroomAdapter.H;

namespace Mashil.Import.Admin;

public sealed record AdminEditResult(long ToiletId, IReadOnlyList<string> AppliedFields, IReadOnlyList<string> ClearedFields, IReadOnlyList<string> IgnoredKeys);

public sealed record OverrideHistoryRow(
    long Id, string FieldName, string ValueJson, OverrideSource Source, int Priority, bool IsActive,
    long? ChangeRequestId, Guid? CreatedById, string? CreatedByName, DateTime CreatedAt);

public sealed record ToiletChangeRequestRow(long Id, ChangeRequestKind Kind, ChangeRequestStatus Status, string PayloadJson, DateTime CreatedAt, DateTime? ReviewedAt, string? ReviewNote);

/// <summary>관리자 편집 화면에 쓰는 지금 값. 원본 값(<see cref="ToiletAdminView.Source"/>)과 나란히 보여 준다.</summary>
public sealed record ToiletAdminValues(
    string? Name, string? RoadAddress, string? LotAddress, double? Latitude, double? Longitude,
    string? Phone, string? OpenHours, string? OpenHoursDetail, string? Status);

public sealed record ToiletAdminView(
    long Id, string ManagementNo, ToiletOrigin Origin, ToiletStatus Status, CoordinateSource CoordinateSource, ScheduleStatus ScheduleStatus,
    DateTime UpdatedAt,
    ToiletAdminValues Current,
    /// <summary>원본(LOCALDATA) 값. 제보로 추가된 화장실은 null.</summary>
    ToiletAdminValues? Source,
    IReadOnlyList<OverrideHistoryRow> Overrides,
    IReadOnlyList<ToiletChangeRequestRow> ChangeRequests,
    int OpenIssues);

/// <summary>
/// 관리자 직접 수정 (ADM-04). 원본은 고치지 않고 <c>toilet_overrides(source = Admin)</c> 를 남긴다 — 우선순위가 가장 높아
/// 변경 요청·지오코딩·원본보다 먼저 쓰이고 재임포트해도 유지된다 (DATA-11, <see cref="OverrideApplier"/>).
/// 저장한 뒤 원본부터 다시 계산해서(원본 → 정규화 → 오버라이드) 오버라이드를 끄면 원본 값으로 돌아간다.
/// <para>payload 키는 변경 요청과 같다: name, roadAddress, lotAddress, phone, openHours, openHoursDetail, latitude+longitude, 그리고 status.
/// 값이 null 이면 그 필드의 관리자 오버라이드를 끈다 (원본 값으로 되돌리기). name 은 비울 수 없다.</para>
/// </summary>
public static class ToiletAdminEditor
{
    private static readonly HashSet<string> AdminStatuses = [nameof(ToiletStatus.Active), nameof(ToiletStatus.Removed)];

    public static async Task<AdminEditResult?> ApplyAsync(MashilDbContext db, long toiletId, JsonElement p, Guid adminId, DateTime now, CancellationToken ct)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new FormatException("수정 내용은 JSON 객체여야 합니다");
        var t = await ImportRunner.WithNormalizationGraph(db.Toilets).SingleOrDefaultAsync(x => x.Id == toiletId, ct);
        if (t is null) return null;

        var applied = new List<string>();
        var cleared = new List<string>();
        var ignored = new List<string>();

        foreach (var prop in p.EnumerateObject())
        {
            if (ChangeRequestApplier.FieldMap.TryGetValue(prop.Name, out var field))
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    if (field == OverrideFields.Name) { ignored.Add(prop.Name); continue; }
                    if (Deactivate(t, field)) cleared.Add(field);
                    continue;
                }
                var value = prop.Value.ToString().Trim();
                if (field == OverrideFields.Name && value.Length == 0) { ignored.Add(prop.Name); continue; }
                if (field == OverrideFields.Phone) value = PhoneNormalizer.Normalize(value).Phone ?? "";
                Upsert(t, field, OverrideApplier.StringJson(value.Length == 0 ? null : value), adminId, now);
                applied.Add(field);
            }
            else if (prop.Name == "status")
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    if (Deactivate(t, OverrideFields.Status)) cleared.Add(OverrideFields.Status);
                }
                else if (prop.Value.ValueKind == JsonValueKind.String && AdminStatuses.Contains(prop.Value.GetString()!))
                {
                    Upsert(t, OverrideFields.Status, OverrideApplier.StringJson(prop.Value.GetString()), adminId, now);
                    applied.Add(OverrideFields.Status);
                }
                else ignored.Add("status");
            }
            else if (prop.Name is not ("latitude" or "longitude" or "note")) ignored.Add(prop.Name);
        }

        var hasLat = p.TryGetProperty("latitude", out var la);
        var hasLng = p.TryGetProperty("longitude", out var lo);
        if (hasLat || hasLng)
        {
            if (hasLat && hasLng && la.ValueKind == JsonValueKind.Null && lo.ValueKind == JsonValueKind.Null)
            {
                if (Deactivate(t, OverrideFields.Location)) cleared.Add(OverrideFields.Location);
            }
            else if (hasLat && hasLng && la.TryGetDouble(out var lat) && lo.TryGetDouble(out var lng) && lat is >= 33 and <= 39 && lng is >= 124 and <= 132)
            {
                Upsert(t, OverrideFields.Location, OverrideApplier.LocationJson(lat, lng), adminId, now);
                applied.Add(OverrideFields.Location);
            }
            else ignored.Add("latitude/longitude");
        }

        if (applied.Count + cleared.Count > 0)
        {
            await RecomputeAsync(db, t, await ImportRunner.LoadContextAsync(db, ct), cleared, now, ct);
            await db.SaveChangesAsync(ct);
        }
        return new AdminEditResult(t.Id, applied, cleared, ignored);
    }

    /// <summary>오버라이드 하나를 끈다 (출처 상관없음: 잘못 승인한 변경 요청, 틀린 지오코딩 좌표도). 끈 뒤 원본부터 다시 계산.</summary>
    public static async Task<bool> DeactivateOverrideAsync(MashilDbContext db, long toiletId, long overrideId, DateTime now, CancellationToken ct)
    {
        var t = await ImportRunner.WithNormalizationGraph(db.Toilets).SingleOrDefaultAsync(x => x.Id == toiletId, ct);
        var o = t?.Overrides.SingleOrDefault(x => x.Id == overrideId);
        if (t is null || o is null) return false;
        o.IsActive = false;
        await RecomputeAsync(db, t, await ImportRunner.LoadContextAsync(db, ct), [o.FieldName], now, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>원본이 있으면 원본부터(정규화 → 오버라이드), 없으면(제보로 추가) 지금 값 위에 오버라이드만 다시 적용.</summary>
    private static async Task RecomputeAsync(MashilDbContext db, Toilet t, NormalizationContext ctx, IReadOnlyCollection<string> cleared, DateTime now, CancellationToken ct)
    {
        var raw = await db.RawToiletRecords.AsNoTracking().Where(r => r.ToiletId == t.Id).OrderByDescending(r => r.LastSeenAt).FirstOrDefaultAsync(ct);
        if (raw is not null)
        {
            ImportRunner.NormalizeRow(ImportRunner.RowOf(raw), t, ctx, now);
        }
        else
        {
            ToiletRecompute.AfterOverrides(t, t.RoadAddress, t.LotAddress, ctx, now);
            var drafts = new List<IssueDraft>();
            LocalDataToiletMapper.ApplyHours(t, OpeningHoursParser.Parse(t.OpenHours, t.OpenHoursDetail, ctx.For(IssueFields.OpeningHours)), drafts);
            IssueSync.Replace(t, drafts, i => i.FieldName == IssueFields.OpeningHours, now);
            t.UpdatedAt = now;
        }

        bool Active(string field) => t.Overrides.Any(o => o.IsActive && o.FieldName == field);

        // 오버라이드가 모두 꺼진 필드 중 원본 정규화가 되돌려 주지 않는 것: 상태와 좌표
        if (cleared.Contains(OverrideFields.Status) && !Active(OverrideFields.Status))
            t.Status = raw is null || raw.ConsecutiveMissing == 0 ? ToiletStatus.Active : ToiletStatus.Missing;
        if (cleared.Contains(OverrideFields.Location) && !Active(OverrideFields.Location))
        {
            // 좌표가 없어졌으니 다시 지오코딩 대상
            t.Location = null;
            t.Latitude = t.Longitude = null;
            t.CoordinateSource = CoordinateSource.Unknown;
        }
    }

    private static bool Deactivate(Toilet t, string field)
    {
        var any = false;
        foreach (var o in t.Overrides.Where(o => o.IsActive && o.FieldName == field && o.Source == OverrideSource.Admin))
        {
            o.IsActive = false;
            any = true;
        }
        return any;
    }

    private static void Upsert(Toilet t, string field, string valueJson, Guid adminId, DateTime now)
    {
        Deactivate(t, field);
        t.Overrides.Add(new ToiletOverride
        {
            FieldName = field, ValueJson = valueJson, Source = OverrideSource.Admin,
            Priority = (int)OverrideSource.Admin, CreatedById = adminId, CreatedAt = now,
        });
    }

    /// <summary>편집 화면: 지금 값, 원본 값, 오버라이드 이력(꺼진 것 포함), 이 화장실에 온 변경 요청.</summary>
    public static async Task<ToiletAdminView?> ViewAsync(MashilDbContext db, long toiletId, CancellationToken ct)
    {
        var t = await db.Toilets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == toiletId, ct);
        if (t is null) return null;

        var overrides = await db.ToiletOverrides.AsNoTracking().Where(o => o.ToiletId == toiletId)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .GroupJoin(db.UserProfiles, o => o.CreatedById, u => (Guid?)u.Id, (o, us) => new { o, us })
            .SelectMany(x => x.us.DefaultIfEmpty(), (x, u) => new OverrideHistoryRow(
                x.o.Id, x.o.FieldName, x.o.ValueJson, x.o.Source, x.o.Priority, x.o.IsActive, x.o.ChangeRequestId,
                x.o.CreatedById, u == null ? null : u.DisplayName, x.o.CreatedAt))
            .ToListAsync(ct);

        var addedBy = t.ManagementNo.StartsWith("USR-", StringComparison.Ordinal) && long.TryParse(t.ManagementNo[4..], out var crId) ? crId : (long?)null;
        var requests = await db.ChangeRequests.AsNoTracking()
            .Where(c => c.TargetToiletId == toiletId || (addedBy != null && c.Id == addedBy))
            .OrderByDescending(c => c.CreatedAt).Take(100)
            .Select(c => new ToiletChangeRequestRow(c.Id, c.Kind, c.Status, c.PayloadJson, c.CreatedAt, c.ReviewedAt, c.ReviewNote))
            .ToListAsync(ct);

        var raw = await db.RawToiletRecords.AsNoTracking().Where(r => r.ToiletId == toiletId).OrderByDescending(r => r.LastSeenAt).FirstOrDefaultAsync(ct);
        ToiletAdminValues? source = null;
        if (raw is not null)
        {
            var r = ImportRunner.RowOf(raw);
            string? S(string h) => LocalDataToiletMapper.Trim(r[h]);
            source = new ToiletAdminValues(S(H.Name), S(H.RoadAddress), S(H.LotAddress), null, null,
                PhoneNormalizer.Normalize(r[H.Phone]).Phone, S(H.OpenHours), S(H.OpenHoursDetail), null);
        }

        var openIssues = await db.NormalizationIssues.CountAsync(i => i.ToiletId == toiletId && i.Status == IssueStatus.Open, ct);
        return new ToiletAdminView(t.Id, t.ManagementNo, t.Origin, t.Status, t.CoordinateSource, t.ScheduleStatus, t.UpdatedAt,
            new ToiletAdminValues(t.Name, t.RoadAddress, t.LotAddress, t.Latitude, t.Longitude, t.Phone, t.OpenHours, t.OpenHoursDetail, t.Status.ToString()),
            source, overrides, requests, openIssues);
    }
}
