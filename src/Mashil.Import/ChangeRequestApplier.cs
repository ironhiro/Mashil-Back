using System.Text.Json;
using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import;

/// <summary>
/// 승인된 변경 요청을 반영한다 (CR-03, SD-06). 원본을 직접 고치지 않고 오버라이드로 남겨서 재임포트해도 유지된다.
/// <list type="bullet">
/// <item>Edit: 바뀐 필드마다 <c>toilet_overrides(source = ChangeRequest)</c>. 같은 필드의 이전 변경 요청 오버라이드는 끈다.
///   위도·경도는 둘 다 있을 때만 location 오버라이드. 구분(kind)은 원본 코드라 바꾸지 않는다</item>
/// <item>Delete: status = Removed 오버라이드</item>
/// <item>Add: 새 화장실 (Origin = UserSubmitted, 관리번호 USR-{요청 id}). 좌표가 없으면 지오코딩 대상</item>
/// </list>
/// 웹이 보내는 payload 키: name, kind, roadAddress, lotAddress, latitude, longitude, openHours, openHoursDetail, phone, reason.
/// </summary>
public static class ChangeRequestApplier
{
    /// <summary>payload 키 → 오버라이드 필드. 관리자 직접 수정(<see cref="Admin.ToiletAdminEditor"/>)도 같은 키를 쓴다.</summary>
    internal static readonly IReadOnlyDictionary<string, string> FieldMap = new Dictionary<string, string>
    {
        ["name"] = OverrideFields.Name,
        ["roadAddress"] = OverrideFields.RoadAddress,
        ["lotAddress"] = OverrideFields.LotAddress,
        ["phone"] = OverrideFields.Phone,
        ["openHours"] = OverrideFields.OpenHours,
        ["openHoursDetail"] = OverrideFields.OpenHoursDetail,
    };

    public sealed record Result(long ToiletId, IReadOnlyList<string> AppliedFields, IReadOnlyList<string> IgnoredKeys);

    public static async Task<Result> ApplyAsync(MashilDbContext db, ChangeRequest cr, Guid adminId, DateTime now, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cr.PayloadJson) ? "{}" : cr.PayloadJson);
        var p = doc.RootElement;
        var ctx = NormalizationContext.FromMappings(await db.ValueMappings.AsNoTracking().Where(m => m.IsActive).ToListAsync(ct));

        if (cr.Kind == ChangeRequestKind.Add) return await AddAsync(db, cr, p, ctx, now, ct);

        var id = cr.TargetToiletId ?? throw new InvalidOperationException("수정·삭제 요청에 대상 화장실이 없습니다");
        var t = await db.Toilets
            .Include(x => x.Overrides.Where(o => o.IsActive))
            .Include(x => x.Schedules)
            .Include(x => x.Issues.Where(i => i.Status == IssueStatus.Open))
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"화장실 #{id} 가 없습니다");

        var applied = new List<string>();
        var ignored = new List<string>();
        var (roadBefore, lotBefore) = (t.RoadAddress, t.LotAddress);

        if (cr.Kind == ChangeRequestKind.Delete)
        {
            Upsert(t, OverrideFields.Status, OverrideApplier.StringJson(nameof(ToiletStatus.Removed)), cr.Id, adminId, now);
            applied.Add(OverrideFields.Status);
        }
        else
        {
            foreach (var prop in p.EnumerateObject())
            {
                if (FieldMap.TryGetValue(prop.Name, out var field))
                {
                    var value = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.ToString().Trim();
                    if (field == OverrideFields.Name && string.IsNullOrEmpty(value)) { ignored.Add(prop.Name); continue; }
                    if (field == OverrideFields.Phone) value = PhoneNormalizer.Normalize(value).Phone;
                    Upsert(t, field, OverrideApplier.StringJson(string.IsNullOrEmpty(value) ? null : value), cr.Id, adminId, now);
                    applied.Add(field);
                }
                else if (prop.Name is not ("latitude" or "longitude" or "note" or "reason")) ignored.Add(prop.Name);
            }

            if (TryLatLng(p, out var lat, out var lng))
            {
                Upsert(t, OverrideFields.Location, OverrideApplier.LocationJson(lat, lng), cr.Id, adminId, now);
                applied.Add(OverrideFields.Location);
            }
            else if (p.TryGetProperty("latitude", out _) || p.TryGetProperty("longitude", out _)) ignored.Add("latitude/longitude");
        }

        ToiletRecompute.AfterOverrides(t, roadBefore, lotBefore, ctx, now);
        t.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new Result(t.Id, applied, ignored);
    }

    private static async Task<Result> AddAsync(MashilDbContext db, ChangeRequest cr, JsonElement p, NormalizationContext ctx, DateTime now, CancellationToken ct)
    {
        string? S(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s ? s : null;

        var name = S("name") ?? throw new InvalidOperationException("추가 요청에 이름이 없습니다");
        var kindText = S("kind") ?? "기타";
        var t = new Toilet
        {
            ManagementNo = $"USR-{cr.Id}",
            Origin = ToiletOrigin.UserSubmitted,
            Status = ToiletStatus.Active,
            Name = name,
            Kind = kindText,
            KindCode = KindNormalizer.Normalize(kindText).Code,
            RoadAddress = S("roadAddress"),
            LotAddress = S("lotAddress"),
            Phone = PhoneNormalizer.Normalize(S("phone")).Phone,
            OpenHours = S("openHours"),
            OpenHoursDetail = S("openHoursDetail"),
            AddressSource = AddressSource.AdminManual,
            NormalizerVersion = ImportRunner.NormalizerVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var drafts = new List<IssueDraft>();
        LocalDataToiletMapper.ApplyHours(t, OpeningHoursParser.Parse(t.OpenHours, t.OpenHoursDetail, ctx.For(IssueFields.OpeningHours)), drafts);
        foreach (var d in drafts)
            t.Issues.Add(new NormalizationIssue { FieldName = d.Field, Type = d.Type, Severity = d.Severity, RawValue = d.RawValue, Note = d.Note, CreatedAt = now });

        if (TryLatLng(p, out var lat, out var lng))
        {
            t.Overrides.Add(new ToiletOverride
            {
                FieldName = OverrideFields.Location, ValueJson = OverrideApplier.LocationJson(lat, lng),
                Source = OverrideSource.ChangeRequest, Priority = (int)OverrideSource.ChangeRequest,
                ChangeRequestId = cr.Id, CreatedAt = now,
            });
            OverrideApplier.Apply(t, t.Overrides);
        }

        db.Toilets.Add(t);
        await db.SaveChangesAsync(ct);
        return new Result(t.Id, ["(new)"], []);
    }

    private static bool TryLatLng(JsonElement p, out double lat, out double lng)
    {
        lat = lng = 0;
        return p.TryGetProperty("latitude", out var la) && la.ValueKind == JsonValueKind.Number && la.TryGetDouble(out lat)
            && p.TryGetProperty("longitude", out var lo) && lo.ValueKind == JsonValueKind.Number && lo.TryGetDouble(out lng)
            && lat is >= 33 and <= 39 && lng is >= 124 and <= 132; // 국내 범위 (DATA-08)
    }

    private static void Upsert(Toilet t, string field, string valueJson, long crId, Guid adminId, DateTime now)
    {
        foreach (var o in t.Overrides.Where(o => o.IsActive && o.FieldName == field && o.Source == OverrideSource.ChangeRequest))
            o.IsActive = false;
        t.Overrides.Add(new ToiletOverride
        {
            FieldName = field, ValueJson = valueJson, Source = OverrideSource.ChangeRequest,
            Priority = (int)OverrideSource.ChangeRequest, ChangeRequestId = crId, CreatedById = adminId, CreatedAt = now,
        });
    }
}
