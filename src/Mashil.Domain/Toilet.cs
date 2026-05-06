using NetTopologySuite.Geometries;

namespace Mashil.Domain;

/// <summary>
/// 공중/개방화장실 마스터 레코드. 좌표·주소 모두 nullable — 데이터 출처에 따라 한쪽만 있을 수 있음.
/// </summary>
public sealed class Toilet
{
    public long Id { get; set; }

    /// <summary>관리번호 — 공공데이터의 자연 키.</summary>
    public required string ManagementNo { get; set; }

    /// <summary>개방자치단체코드.</summary>
    public int LocalGovCode { get; set; }

    /// <summary>구분명 (공중화장실 / 개방화장실 / ...).</summary>
    public required string Kind { get; set; }

    public required string Name { get; set; }

    public string? RoadAddress { get; set; }
    public string? LotAddress { get; set; }
    public AddressSource AddressSource { get; set; } = AddressSource.Unknown;

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>
    /// PostGIS geography(Point, 4326). Lat/Lng 둘 다 있을 때만 채워짐.
    /// 지도 표시·근접 검색은 이 컬럼만 사용.
    /// </summary>
    public Point? Location { get; set; }

    public CoordinateSource CoordinateSource { get; set; } = CoordinateSource.Unknown;

    /// <summary>지도 위에 표시 가능한지. <see cref="Location"/> 가 not null 이면 true.</summary>
    public bool IsLocatable => Location is not null;

    // --- 변기 수 ---
    public int MaleStallCount { get; set; }
    public int MaleUrinalCount { get; set; }
    public int MaleAccessibleStallCount { get; set; }
    public int MaleAccessibleUrinalCount { get; set; }
    public int MaleChildStallCount { get; set; }
    public int MaleChildUrinalCount { get; set; }
    public int FemaleStallCount { get; set; }
    public int FemaleAccessibleStallCount { get; set; }
    public int FemaleChildStallCount { get; set; }

    // --- 운영 ---
    public string? ManagementOrg { get; set; }
    public string? Phone { get; set; }
    public string? OpenHours { get; set; }
    public string? OpenHoursDetail { get; set; }
    public string? InstalledYearMonth { get; set; }
    public string? RemodeledYearMonth { get; set; }

    // --- 시설 ---
    public string? OwnershipKind { get; set; }
    public string? WasteType { get; set; }
    public bool? SafetyFacilityRequired { get; set; }
    public bool? EmergencyBellInstalled { get; set; }
    public string? EmergencyBellLocation { get; set; }
    public bool? EntranceCctv { get; set; }
    public bool? DiaperChangerAvailable { get; set; }
    public string? DiaperChangerLocation { get; set; }

    // --- 메타 ---
    public DateOnly? DataReferenceDate { get; set; }
    public string? DataUpdateKind { get; set; }
    public DateTime? DataUpdatedAt { get; set; }
    public DateTime? LastModifiedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum CoordinateSource
{
    Unknown = 0,
    Csv = 1,
    KakaoGeocoded = 2,
    AdminManual = 3,
    /// <summary>Kakao 주소 검색에서 매칭 실패. 동일 주소로 재시도 안 함 — 관리자 수정 대상.</summary>
    KakaoNoMatch = 4,
}

public enum AddressSource
{
    Unknown = 0,
    CsvRoad = 1,
    CsvLot = 2,
    KakaoReverse = 3,
    AdminManual = 4,
    /// <summary>좌표 → 주소 reverse-geocode 결과 documents 비어있음.</summary>
    KakaoNoMatch = 5,
}
