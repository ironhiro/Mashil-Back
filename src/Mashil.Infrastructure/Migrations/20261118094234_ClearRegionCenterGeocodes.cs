using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <summary>
    /// 데이터 정리 (2026-11-18 QA): 주소가 "대전광역시 서구"처럼 시도·시군구까지만 있는데 예전 지오코더가 그 지역의 대표점을
    /// 좌표로 넣은 곳(로컬 기준 111곳)의 좌표를 지운다. 대전 서구 한 점에 97곳이 몰리는 식으로 지도에 엉뚱하게 나왔다.
    /// 지금 지오코더는 지역 단위 후보를 쓰지 않으므로(#14, GeocodingService.Choose) 다시 생기지 않는다.
    /// <para>
    /// 원본(CSV)에 있던 좌표는 건드리지 않는다: 주소가 지역까지만이어도 좌표는 곳마다 달라 실제 위치일 수 있다.
    /// 좌표가 없어진 곳은 지도·근처 찾기에서 빠지고, 이름·주소 검색에는 나온다. 관리자가 직접 좌표를 넣으면 다시 나온다.
    /// </para>
    /// </summary>
    public partial class ClearRegionCenterGeocodes : Migration
    {
        // 시도만, 또는 시도 + 시군구만 있는 주소 (띄어쓰기 없이 건물번호까지 붙은 주소는 걸리지 않는다)
        private const string RegionOnly = @"'^\s*\S+(특별시|광역시|특별자치시|특별자치도|도)(\s+\S+(시|군|구))?\s*$'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE TEMP TABLE region_center_toilets ON COMMIT DROP AS
                SELECT t."Id"
                FROM toilets t
                WHERE t."CoordinateSource" = 'KakaoGeocoded'
                  AND coalesce(t."RoadAddress", t."LotAddress") ~ {RegionOnly};

                UPDATE toilet_overrides o
                SET "IsActive" = FALSE
                WHERE o."FieldName" = 'location' AND o."Source" = 'Geocode' AND o."IsActive"
                  AND o."ToiletId" IN (SELECT "Id" FROM region_center_toilets);

                -- 관리자·변경 요청 좌표가 따로 있으면 그대로 둔다
                UPDATE toilets t
                SET "Latitude" = NULL, "Longitude" = NULL, "Location" = NULL, "CoordinateSource" = 'KakaoNoMatch'
                WHERE t."Id" IN (SELECT "Id" FROM region_center_toilets)
                  AND NOT EXISTS (SELECT 1 FROM toilet_overrides o
                                  WHERE o."ToiletId" = t."Id" AND o."FieldName" = 'location' AND o."IsActive");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 데이터 정리라 되돌리지 않는다 (지운 것은 지역 대표점이라 되살릴 값이 아니다). 꺼진 오버라이드는 이력에 남아 있다
        }
    }
}
