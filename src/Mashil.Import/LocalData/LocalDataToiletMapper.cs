using System.Globalization;
using Mashil.Domain;
using Mashil.Import.Normalization;
using H = Mashil.Import.LocalData.LocalDataPublicRestroomAdapter.H;

namespace Mashil.Import.LocalData;

/// <summary>
/// 원본 행 → <see cref="Toilet"/> 필드 + 정규화 (P2): 구분 코드, 전화번호 표기, 개방시간 구조화,
/// 변기 수·날짜 이상치 검출. 원문(개방시간, 구분명)은 표시·검증용으로 그대로 둔다.
/// <para>
/// 이 소스가 제공하지 않는 필드(좌표, <see cref="Toilet.CoordinateSource"/>)는 건드리지 않는다 —
/// 지오코딩 결과가 재임포트로 지워지지 않게 (G1).
/// </para>
/// </summary>
public static class LocalDataToiletMapper
{
    /// <summary>원본 행을 화장실에 반영하고, 정규화 중 발견한 이슈를 돌려준다.</summary>
    public static List<IssueDraft> Apply(SourceRow r, Toilet t, NormalizationContext? ctx = null, DateOnly? today = null)
    {
        ctx ??= NormalizationContext.Empty;
        var issues = new List<IssueDraft>();

        t.LocalGovCode = int.TryParse(r[H.LocalGovCode]?.Trim(), out var code) ? code : 0;
        t.Kind = Trim(r[H.Kind]) ?? string.Empty;
        var (kindCode, kindIssue) = KindNormalizer.Normalize(r[H.Kind]);
        t.KindCode = kindCode;
        if (kindIssue is not null) issues.Add(kindIssue);
        t.Name = Trim(r[H.Name]) ?? string.Empty;

        var road = Trim(r[H.RoadAddress]);
        var lot = Trim(r[H.LotAddress]);
        if (road is not null || lot is not null)
        {
            t.RoadAddress = road;
            t.LotAddress = lot;
            t.AddressSource = road is not null ? AddressSource.CsvRoad : AddressSource.CsvLot;
        }
        else if (t.AddressSource is AddressSource.CsvRoad or AddressSource.CsvLot)
        {
            // 원본에서 주소가 사라짐 → 원본 유래 주소만 비운다 (역지오코딩·관리자 주소는 유지)
            t.RoadAddress = null;
            t.LotAddress = null;
            t.AddressSource = AddressSource.Unknown;
        }

        t.MaleStallCount = Int(r[H.MaleStall]);
        t.MaleUrinalCount = Int(r[H.MaleUrinal]);
        t.MaleAccessibleStallCount = Int(r[H.MaleAccessibleStall]);
        t.MaleAccessibleUrinalCount = Int(r[H.MaleAccessibleUrinal]);
        t.MaleChildStallCount = Int(r[H.MaleChildStall]);
        t.MaleChildUrinalCount = Int(r[H.MaleChildUrinal]);
        t.FemaleStallCount = Int(r[H.FemaleStall]);
        t.FemaleAccessibleStallCount = Int(r[H.FemaleAccessibleStall]);
        t.FemaleChildStallCount = Int(r[H.FemaleChildStall]);

        var counts = new[] { t.MaleStallCount, t.MaleUrinalCount, t.MaleAccessibleStallCount, t.MaleAccessibleUrinalCount,
            t.MaleChildStallCount, t.MaleChildUrinalCount, t.FemaleStallCount, t.FemaleAccessibleStallCount, t.FemaleChildStallCount };
        if (counts.Any(c => c > MaxPlausibleCount))
            issues.Add(new IssueDraft(IssueFields.Facilities, IssueType.OutOfRange, IssueSeverity.Warning,
                string.Join(",", counts), $"변기 수가 {MaxPlausibleCount}개를 넘음"));

        t.ManagementOrg = Trim(r[H.ManagementOrg]);
        var (phone, phoneIssue) = PhoneNormalizer.Normalize(r[H.Phone]);
        t.Phone = phone;
        if (phoneIssue is not null) issues.Add(phoneIssue);

        t.OpenHours = Trim(r[H.OpenHours]);
        t.OpenHoursDetail = Trim(r[H.OpenHoursDetail]);
        ApplyHours(t, OpeningHoursParser.Parse(t.OpenHours, t.OpenHoursDetail, ctx.For(IssueFields.OpeningHours)), issues);
        t.InstalledYearMonth = Trim(r[H.InstalledYearMonth]);
        t.RemodeledYearMonth = Trim(r[H.RemodeledYearMonth]);

        t.OwnershipKind = Trim(r[H.OwnershipKind]);
        t.WasteType = Trim(r[H.WasteType]);
        t.SafetyFacilityRequired = YesNo(r[H.SafetyFacilityRequired]);
        t.EmergencyBellInstalled = YesNo(r[H.EmergencyBellInstalled]);
        t.EmergencyBellLocation = Trim(r[H.EmergencyBellLocation]);
        t.EntranceCctv = YesNo(r[H.EntranceCctv]);
        t.DiaperChangerAvailable = YesNo(r[H.DiaperChangerAvailable]);
        t.DiaperChangerLocation = Trim(r[H.DiaperChangerLocation]);

        t.DataReferenceDate = DateOnly.TryParseExact(r[H.DataReferenceDate]?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        t.DataUpdateKind = Trim(r[H.DataUpdateKind]);
        t.DataUpdatedAt = Kst(r[H.DataUpdatedAt]);
        t.LastModifiedAt = Kst(r[H.LastModifiedAt]);

        var refToday = today ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9));
        if (t.DataReferenceDate is { } refDate && (refDate > refToday || refDate.Year < 1990))
            issues.Add(new IssueDraft(IssueFields.DataReferenceDate, IssueType.OutOfRange, IssueSeverity.Info,
                r[H.DataReferenceDate], refDate > refToday ? "미래 날짜" : "1990년 이전"));

        return issues;
    }

    /// <summary>비정상으로 보는 변기 수 기준 (실데이터 200 초과 24건).</summary>
    public const int MaxPlausibleCount = 200;

    /// <summary>파싱 결과를 화장실에 반영. 스케줄 행은 통째로 교체한다 (같은 트랜잭션).</summary>
    public static void ApplyHours(Toilet t, HoursOutcome hours, List<IssueDraft> issues)
    {
        t.ScheduleStatus = hours.Status;
        t.Schedules.Clear();
        foreach (var e in hours.Entries)
            t.Schedules.Add(new OpeningSchedule { DayType = e.Day, OpenMinute = e.Open, CloseMinute = e.Close, IsClosed = e.Closed });

        if (hours.Issue is { } type)
            issues.Add(new IssueDraft(IssueFields.OpeningHours, type, hours.Severity,
                OpeningHoursParser.MappingKey(t.OpenHours, t.OpenHoursDetail), hours.Note));
    }

    internal static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static int Int(string? s) => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : 0;

    internal static bool? YesNo(string? s) => s?.Trim() switch
    {
        "Y" or "y" => true,
        "N" or "n" => false,
        _ => null,
    };

    private static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);

    /// <summary>원본 시각은 KST 로 보고 UTC 로 저장.</summary>
    internal static DateTime? Kst(string? s) =>
        DateTime.TryParseExact(s?.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateTime.SpecifyKind(dt - KstOffset, DateTimeKind.Utc)
            : null;
}
