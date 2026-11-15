using System.Text;
using Mashil.Import;

namespace Mashil.Import.Tests;

/// <summary>LOCALDATA 전국파일과 같은 34컬럼 헤더로 테스트 CSV 를 만든다.</summary>
internal static class TestCsv
{
    static TestCsv() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public const string Header =
        "개방자치단체코드,관리번호,구분명,근거법령명,화장실명,소재지도로명주소,소재지지번주소," +
        "남성용-대변기수,남성용-소변기수,남성용-장애인용대변기수,남성용-장애인용소변기수,남성용-어린이용대변기수,남성용-어린이용소변기수," +
        "여성용-대변기수,여성용-장애인용대변기수,여성용-어린이용대변기수,관리기관명,전화번호,개방시간,개방시간상세,설치연월," +
        "화장실소유구분명,오물처리방식,안전관리시설설치대상여부,비상벨설치여부,비상벨설치장소,화장실입구CCTV설치유무," +
        "기저귀교환대유무,기저귀교환대장소,리모델링연월,데이터기준일자,데이터갱신구분,데이터갱신시점,최종수정시점";

    public const string Row1 =
        "3000000,202530000000100840,개방화장실,,올림픽기념국민생활관,서울특별시 종로구 성균관로 91,서울특별시 종로구 혜화동 1-21," +
        "2,2,1,0,0,0,3,1,0,종로구청,0221484057,정시,\"월-금06:00~22:00 토-일09:00~18:00 공휴일 휴관\",201001," +
        "공공기관-지방자치단체,수세식,Y,Y,입구,N,N,,,2026-11-10,U,2026-11-11 22:27:30,2026-11-10 15:45:05";

    public const string Row2 =
        "3000000,202530000000100823,공중화장실,,사직단 공중화장실(사직공원),,서울특별시 종로구 사직동 1-28," +
        "3,2,1,0,0,0,3,1,0,종로구청,,상시,,,공공기관-지방자치단체,수세식,N,N,,N,Y,여자화장실,,2026-10-01,I,2026-10-02 18:00:00,2026-10-01 10:00:00";

    public static byte[] Cp949(params string[] lines) =>
        Encoding.GetEncoding(949).GetBytes(string.Join("\r\n", lines) + "\r\n");

    public static SourceFile File(params string[] lines) => new(Cp949(lines), "test.csv");
}
