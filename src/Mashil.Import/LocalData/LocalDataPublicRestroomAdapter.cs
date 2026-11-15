using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Mashil.Domain;

namespace Mashil.Import.LocalData;

/// <summary>
/// LOCALDATA 공중화장실정보 전국파일 (<c>공중화장실정보.csv</c>, 34컬럼, 좌표 없음).
/// 2026-11-14 프로파일링 기준 (Mashil-app/docs/06-data-profile.md).
/// </summary>
public sealed class LocalDataPublicRestroomAdapter : ISourceAdapter
{
    public string SourceCode => DataSourceCodes.LocalDataPublicRestroom;

    public IReadOnlyList<string> RequiredHeaders { get; } =
    [
        H.ManagementNo, H.Kind, H.Name, H.RoadAddress, H.LotAddress,
        H.MaleStall, H.MaleUrinal, H.FemaleStall,
        H.OpenHours, H.OpenHoursDetail,
    ];

    public IEnumerable<SourceRow> Read(TextReader reader, Action<int, string>? onSkipped = null)
    {
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.None, // 원본 보존: 값은 손대지 않는다
        });

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is null)
            throw new SourceSchemaException(RequiredHeaders);

        var header = csv.HeaderRecord.Select(h => h.Trim().TrimStart('﻿')).ToArray();
        var missing = RequiredHeaders.Where(r => !header.Contains(r)).ToList();
        if (missing.Count > 0) throw new SourceSchemaException(missing);

        var keyIndex = Array.IndexOf(header, H.ManagementNo);

        while (csv.Read())
        {
            var line = csv.Parser.Row;
            var record = csv.Parser.Record;
            if (record is null || record.Length != header.Length)
            {
                onSkipped?.Invoke(line, $"컬럼 수 {record?.Length ?? 0} ≠ 헤더 {header.Length}");
                continue;
            }

            var key = record[keyIndex].Trim();
            if (key.Length == 0)
            {
                onSkipped?.Invoke(line, "관리번호 없음");
                continue;
            }

            var fields = new Dictionary<string, string?>(header.Length, StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++)
                fields[header[i]] = record[i];

            yield return new SourceRow(key, fields, line);
        }
    }

    /// <summary>원본 헤더 이름.</summary>
    public static class H
    {
        public const string LocalGovCode = "개방자치단체코드";
        public const string ManagementNo = "관리번호";
        public const string Kind = "구분명";
        public const string LawBasis = "근거법령명";
        public const string Name = "화장실명";
        public const string RoadAddress = "소재지도로명주소";
        public const string LotAddress = "소재지지번주소";
        public const string MaleStall = "남성용-대변기수";
        public const string MaleUrinal = "남성용-소변기수";
        public const string MaleAccessibleStall = "남성용-장애인용대변기수";
        public const string MaleAccessibleUrinal = "남성용-장애인용소변기수";
        public const string MaleChildStall = "남성용-어린이용대변기수";
        public const string MaleChildUrinal = "남성용-어린이용소변기수";
        public const string FemaleStall = "여성용-대변기수";
        public const string FemaleAccessibleStall = "여성용-장애인용대변기수";
        public const string FemaleChildStall = "여성용-어린이용대변기수";
        public const string ManagementOrg = "관리기관명";
        public const string Phone = "전화번호";
        public const string OpenHours = "개방시간";
        public const string OpenHoursDetail = "개방시간상세";
        public const string InstalledYearMonth = "설치연월";
        public const string OwnershipKind = "화장실소유구분명";
        public const string WasteType = "오물처리방식";
        public const string SafetyFacilityRequired = "안전관리시설설치대상여부";
        public const string EmergencyBellInstalled = "비상벨설치여부";
        public const string EmergencyBellLocation = "비상벨설치장소";
        public const string EntranceCctv = "화장실입구CCTV설치유무";
        public const string DiaperChangerAvailable = "기저귀교환대유무";
        public const string DiaperChangerLocation = "기저귀교환대장소";
        public const string RemodeledYearMonth = "리모델링연월";
        public const string DataReferenceDate = "데이터기준일자";
        public const string DataUpdateKind = "데이터갱신구분";
        public const string DataUpdatedAt = "데이터갱신시점";
        public const string LastModifiedAt = "최종수정시점";
    }
}
