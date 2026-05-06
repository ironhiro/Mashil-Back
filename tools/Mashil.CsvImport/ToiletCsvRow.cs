using CsvHelper.Configuration;

namespace Mashil.CsvImport;

internal sealed class ToiletCsvRow
{
    public int LocalGovCode { get; set; }
    public string ManagementNo { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? LawBasis { get; set; }
    public string? Name { get; set; }
    public string? RoadAddress { get; set; }
    public string? LotAddress { get; set; }
    public int? MaleStallCount { get; set; }
    public int? MaleUrinalCount { get; set; }
    public int? MaleAccessibleStallCount { get; set; }
    public int? MaleAccessibleUrinalCount { get; set; }
    public int? MaleChildStallCount { get; set; }
    public int? MaleChildUrinalCount { get; set; }
    public int? FemaleStallCount { get; set; }
    public int? FemaleAccessibleStallCount { get; set; }
    public int? FemaleChildStallCount { get; set; }
    public string? ManagementOrg { get; set; }
    public string? Phone { get; set; }
    public string? OpenHours { get; set; }
    public string? OpenHoursDetail { get; set; }
    public string? InstalledYearMonth { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? OwnershipKind { get; set; }
    public string? WasteType { get; set; }
    public string? SafetyFacilityRequired { get; set; }
    public string? EmergencyBellInstalled { get; set; }
    public string? EmergencyBellLocation { get; set; }
    public string? EntranceCctv { get; set; }
    public string? DiaperChangerAvailable { get; set; }
    public string? DiaperChangerLocation { get; set; }
    public string? RemodeledYearMonth { get; set; }
    public string? DataReferenceDate { get; set; }
    public string? DataUpdateKind { get; set; }
    public string? DataUpdatedAt { get; set; }
    public string? LastModifiedAt { get; set; }
}

/// <summary>
/// 공중화장실정보.csv 의 한글 헤더를 영문 프로퍼티에 매핑.
/// 헤더 순서: 개방자치단체코드, 관리번호, 구분명, 근거법령명, 화장실명,
/// 소재지도로명주소, 소재지지번주소,
/// 남성용-대변기수, 남성용-소변기수, 남성용-장애인용대변기수, 남성용-장애인용소변기수,
/// 남성용-어린이용대변기수, 남성용-어린이용소변기수,
/// 여성용-대변기수, 여성용-장애인용대변기수, 여성용-어린이용대변기수,
/// 관리기관명, 전화번호, 개방시간, 개방시간상세, 설치연월,
/// WGS84위도, WGS84경도, 화장실소유구분명, 오물처리방식,
/// 안전관리시설설치대상여부, 비상벨설치여부, 비상벨설치장소,
/// 화장실입구CCTV설치유무, 기저귀교환대유무, 기저귀교환대장소,
/// 리모델링연월, 데이터기준일자, 데이터갱신구분, 데이터갱신시점, 최종수정시점
/// </summary>
internal sealed class ToiletCsvRowMap : ClassMap<ToiletCsvRow>
{
    public ToiletCsvRowMap()
    {
        Map(m => m.LocalGovCode).Name("개방자치단체코드");
        Map(m => m.ManagementNo).Name("관리번호");
        Map(m => m.Kind).Name("구분명");
        Map(m => m.LawBasis).Name("근거법령명");
        Map(m => m.Name).Name("화장실명");
        Map(m => m.RoadAddress).Name("소재지도로명주소");
        Map(m => m.LotAddress).Name("소재지지번주소");
        Map(m => m.MaleStallCount).Name("남성용-대변기수");
        Map(m => m.MaleUrinalCount).Name("남성용-소변기수");
        Map(m => m.MaleAccessibleStallCount).Name("남성용-장애인용대변기수");
        Map(m => m.MaleAccessibleUrinalCount).Name("남성용-장애인용소변기수");
        Map(m => m.MaleChildStallCount).Name("남성용-어린이용대변기수");
        Map(m => m.MaleChildUrinalCount).Name("남성용-어린이용소변기수");
        Map(m => m.FemaleStallCount).Name("여성용-대변기수");
        Map(m => m.FemaleAccessibleStallCount).Name("여성용-장애인용대변기수");
        Map(m => m.FemaleChildStallCount).Name("여성용-어린이용대변기수");
        Map(m => m.ManagementOrg).Name("관리기관명");
        Map(m => m.Phone).Name("전화번호");
        Map(m => m.OpenHours).Name("개방시간");
        Map(m => m.OpenHoursDetail).Name("개방시간상세");
        Map(m => m.InstalledYearMonth).Name("설치연월");
        Map(m => m.Latitude).Name("WGS84위도");
        Map(m => m.Longitude).Name("WGS84경도");
        Map(m => m.OwnershipKind).Name("화장실소유구분명");
        Map(m => m.WasteType).Name("오물처리방식");
        Map(m => m.SafetyFacilityRequired).Name("안전관리시설설치대상여부");
        Map(m => m.EmergencyBellInstalled).Name("비상벨설치여부");
        Map(m => m.EmergencyBellLocation).Name("비상벨설치장소");
        Map(m => m.EntranceCctv).Name("화장실입구CCTV설치유무");
        Map(m => m.DiaperChangerAvailable).Name("기저귀교환대유무");
        Map(m => m.DiaperChangerLocation).Name("기저귀교환대장소");
        Map(m => m.RemodeledYearMonth).Name("리모델링연월");
        Map(m => m.DataReferenceDate).Name("데이터기준일자");
        Map(m => m.DataUpdateKind).Name("데이터갱신구분");
        Map(m => m.DataUpdatedAt).Name("데이터갱신시점");
        Map(m => m.LastModifiedAt).Name("최종수정시점");
    }
}
