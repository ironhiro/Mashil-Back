using Mashil.Domain;
using Mashil.Import.LocalData;
using Mashil.Import.Normalization;

namespace Mashil.Import;

/// <summary>
/// 원본 값 위에 오버라이드를 적용한 뒤 파생 값을 다시 맞춘다. 임포트와 변경 요청 승인이 같은 규칙을 쓴다.
/// <list type="bullet">
/// <item>개방시간 오버라이드가 있으면 덮어쓴 문장으로 스케줄을 다시 만든다</item>
/// <item>주소가 바뀌면 지오코딩 좌표를 무효화하고 다시 지오코딩 대상으로 만든다</item>
/// <item>주소·이름으로 못 찾았던 곳(KakaoNoMatch)은 이름이 바뀌어도 다시 시도한다 (이름으로도 찾으므로)</item>
/// </list>
/// </summary>
public static class ToiletRecompute
{
    /// <param name="fromImport">
    /// 일일 임포트: 같은 관리번호의 화장실은 주소 표기가 바뀌어도 지오코딩 좌표를 유지하고 (재지오코딩 안 함 — 갱신이 빠르고
    /// 표기만 바뀐 경우가 대부분), 실제 이전일 수 있으니 확인용 이슈만 남긴다 (2026-11-17 결정).
    /// 좌표가 없던 곳은 다시 찾는다. 변경 요청·관리자 수정은 위치를 바로잡으려는 것이라 늘 다시 찾는다.
    /// </param>
    public static void AfterOverrides(Toilet t, string? roadBefore, string? lotBefore, NormalizationContext ctx, DateTime now,
        string? nameBefore = null, bool fromImport = false)
    {
        // 오버라이드(제보·관리자 주소)까지 적용한 최종 주소로 비교한다. 원본 값으로 비교하면 제보로 고친 주소가
        // 매일 임포트 때마다 "바뀜"으로 잡혀 좌표가 흔들린다
        OverrideApplier.Apply(t, t.Overrides);
        var addressChanged = t.Id != 0 && (roadBefore != t.RoadAddress || lotBefore != t.LotAddress);
        var keepGeocode = addressChanged && fromImport
            && t.Overrides.Any(o => o.IsActive && o.FieldName == OverrideFields.Location && o.Source == OverrideSource.Geocode);
        if (keepGeocode)
        {
            t.Issues.RemoveAll(i => i.Status == IssueStatus.Open && i.FieldName == IssueFields.Geocode && i.Type == IssueType.AddressChanged);
            t.Issues.Add(new NormalizationIssue
            {
                FieldName = IssueFields.Geocode, Type = IssueType.AddressChanged, Severity = IssueSeverity.Info,
                RawValue = $"{roadBefore ?? lotBefore} → {t.RoadAddress ?? t.LotAddress}",
                Note = "원본 주소가 바뀌었지만 좌표는 그대로 둠 — 실제로 옮겼으면 좌표 수정", CreatedAt = now,
            });
        }
        // 주소가 바뀌면 지오코딩 좌표는 더 이상 믿을 수 없다 (관리자·제보·이전 좌표 오버라이드는 유지)
        else if (addressChanged)
        {
            foreach (var o in t.Overrides.Where(o => o.IsActive && o.FieldName == OverrideFields.Location && o.Source == OverrideSource.Geocode))
                o.IsActive = false;
            if (t.Overrides.Any(o => o.IsActive && o.FieldName == OverrideFields.Location))
                OverrideApplier.Apply(t, t.Overrides); // 남은 좌표 오버라이드로
            else
            {
                t.Location = null;
                t.Latitude = t.Longitude = null;
                t.CoordinateSource = CoordinateSource.Unknown;
            }
            t.Issues.RemoveAll(i => i.Status == IssueStatus.Open && i.FieldName == IssueFields.Geocode);
        }

        if (t.Id != 0 && nameBefore is not null && nameBefore != t.Name && t.CoordinateSource == CoordinateSource.KakaoNoMatch)
        {
            t.CoordinateSource = CoordinateSource.Unknown;
            t.Issues.RemoveAll(i => i.Status == IssueStatus.Open && i.FieldName == IssueFields.Geocode);
        }

        if (t.Overrides.Any(o => o.IsActive && o.FieldName is OverrideFields.OpenHours or OverrideFields.OpenHoursDetail))
        {
            var drafts = new List<IssueDraft>();
            LocalDataToiletMapper.ApplyHours(t, OpeningHoursParser.Parse(t.OpenHours, t.OpenHoursDetail, ctx.For(IssueFields.OpeningHours)), drafts);
            IssueSync.Replace(t, drafts, i => i.FieldName == IssueFields.OpeningHours, now);
        }
    }
}
