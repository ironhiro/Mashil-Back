using System.Globalization;
using Mashil.Domain;
using H = Mashil.Import.LocalData.LocalDataPublicRestroomAdapter.H;

namespace Mashil.Import.LocalData;

/// <summary>
/// 원본 행 → <see cref="Toilet"/> 필드. P1 은 기존 CsvImport 와 같은 수준(공백 정리, 정수·Y/N·날짜 변환)만 한다.
/// 개방시간 구조화, 코드값·전화번호 정규화는 P2.
/// <para>
/// 이 소스가 제공하지 않는 필드(좌표, <see cref="Toilet.CoordinateSource"/>)는 건드리지 않는다 —
/// 지오코딩 결과가 재임포트로 지워지지 않게 (G1).
/// </para>
/// </summary>
public static class LocalDataToiletMapper
{
    public static void Apply(SourceRow r, Toilet t)
    {
        t.LocalGovCode = int.TryParse(r[H.LocalGovCode]?.Trim(), out var code) ? code : 0;
        t.Kind = Trim(r[H.Kind]) ?? string.Empty;
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

        t.ManagementOrg = Trim(r[H.ManagementOrg]);
        t.Phone = Trim(r[H.Phone]);
        t.OpenHours = Trim(r[H.OpenHours]);
        t.OpenHoursDetail = Trim(r[H.OpenHoursDetail]);
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
