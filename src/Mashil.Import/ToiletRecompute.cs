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
    public static void AfterOverrides(Toilet t, string? roadBefore, string? lotBefore, NormalizationContext ctx, DateTime now, string? nameBefore = null)
    {
        // 주소가 바뀌면 지오코딩 좌표는 더 이상 믿을 수 없다 (관리자·이전 좌표 오버라이드는 유지)
        var addressChanged = t.Id != 0 && (roadBefore != t.RoadAddress || lotBefore != t.LotAddress);
        if (addressChanged)
        {
            foreach (var o in t.Overrides.Where(o => o.IsActive && o.FieldName == OverrideFields.Location && o.Source == OverrideSource.Geocode))
                o.IsActive = false;
            if (!t.Overrides.Any(o => o.IsActive && o.FieldName == OverrideFields.Location))
            {
                t.Location = null;
                t.Latitude = t.Longitude = null;
                t.CoordinateSource = CoordinateSource.Unknown;
            }
            t.Issues.RemoveAll(i => i.Status == IssueStatus.Open && i.FieldName == IssueFields.Geocode);
        }

        OverrideApplier.Apply(t, t.Overrides);

        // 주소 오버라이드로 주소가 바뀌었을 수도 있으니 한 번 더 확인
        if (t.Id != 0 && !addressChanged && (roadBefore != t.RoadAddress || lotBefore != t.LotAddress)
            && !t.Overrides.Any(o => o.IsActive && o.FieldName == OverrideFields.Location && o.Source != OverrideSource.Geocode))
        {
            foreach (var o in t.Overrides.Where(o => o.IsActive && o.FieldName == OverrideFields.Location && o.Source == OverrideSource.Geocode))
                o.IsActive = false;
            t.Location = null;
            t.Latitude = t.Longitude = null;
            t.CoordinateSource = CoordinateSource.Unknown;
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
