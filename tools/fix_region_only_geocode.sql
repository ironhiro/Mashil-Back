-- 지역 대표 좌표로 찍힌 지오코딩 결과 정리 (#14, DATA-10·24)
-- 대상: Geocode 좌표가 실제로 쓰이는 화장실(CoordinateSource = KakaoGeocoded) 중
--   (a) 도로명·지번 주소에 숫자가 하나도 없음 (시도·시군구·읍면동·도로명만 — 예전 GeocodeWorker 이관분)
--   (b) 좌표가 번호 없는 캐시 결과(MatchedAddress 가 숫자로 끝나지 않음)와 같음 (번호 검증 전 GeocodingService 결과)
-- 처리: Geocode 오버라이드를 끄고 좌표를 비워 지오코딩 대기(Unknown)로 되돌린다. 번호 없는 캐시 행은 지운다.
--   → 수정된 GeocodingService 가 다시 처리: 번호가 맞는 후보만 채택, 없으면 KakaoNoMatch + GeocodeNoMatch 이슈.
-- 관리자 좌표(AdminManual)·원본 좌표(Csv)는 건드리지 않는다. 여러 번 실행해도 결과가 같다.
--
-- 사용: 먼저 1) 만 실행해 대상 수를 확인하고, 2) 를 실행한다.
--   psql -v ON_ERROR_STOP=1 -f tools/fix_region_only_geocode.sql

-- 1) 미리 보기
WITH bad_cache AS (
    SELECT "QueryKey", "Latitude", "Longitude" FROM geocode_cache
    WHERE "Status" = 'Matched' AND coalesce("MatchedAddress", '') !~ '[0-9]$'
), target AS (
    SELECT DISTINCT t."Id"
    FROM toilets t
    JOIN toilet_overrides o ON o."ToiletId" = t."Id" AND o."FieldName" = 'location' AND o."IsActive" AND o."Source" = 'Geocode'
    WHERE t."CoordinateSource" = 'KakaoGeocoded'
      AND ((coalesce(t."RoadAddress", '') !~ '[0-9]' AND coalesce(t."LotAddress", '') !~ '[0-9]')
           OR EXISTS (SELECT 1 FROM bad_cache c
                      WHERE abs(c."Latitude" - (o."ValueJson"->>'lat')::float8) < 1e-7
                        AND abs(c."Longitude" - (o."ValueJson"->>'lng')::float8) < 1e-7))
)
SELECT (SELECT count(*) FROM target) AS toilets_to_reset, (SELECT count(*) FROM bad_cache) AS cache_rows_to_delete;

-- 2) 적용
BEGIN;

CREATE TEMP TABLE region_only_geocode ON COMMIT DROP AS
WITH bad_cache AS (
    SELECT "QueryKey", "Latitude", "Longitude" FROM geocode_cache
    WHERE "Status" = 'Matched' AND coalesce("MatchedAddress", '') !~ '[0-9]$'
)
SELECT DISTINCT t."Id"
FROM toilets t
JOIN toilet_overrides o ON o."ToiletId" = t."Id" AND o."FieldName" = 'location' AND o."IsActive" AND o."Source" = 'Geocode'
WHERE t."CoordinateSource" = 'KakaoGeocoded'
  AND ((coalesce(t."RoadAddress", '') !~ '[0-9]' AND coalesce(t."LotAddress", '') !~ '[0-9]')
       OR EXISTS (SELECT 1 FROM bad_cache c
                  WHERE abs(c."Latitude" - (o."ValueJson"->>'lat')::float8) < 1e-7
                    AND abs(c."Longitude" - (o."ValueJson"->>'lng')::float8) < 1e-7));

UPDATE toilet_overrides o SET "IsActive" = FALSE
FROM region_only_geocode r
WHERE o."ToiletId" = r."Id" AND o."FieldName" = 'location' AND o."Source" = 'Geocode' AND o."IsActive";

UPDATE toilets t SET "Location" = NULL, "Latitude" = NULL, "Longitude" = NULL,
       "CoordinateSource" = 'Unknown', "UpdatedAt" = now()
FROM region_only_geocode r
WHERE t."Id" = r."Id";

DELETE FROM geocode_cache
WHERE "Status" = 'Matched' AND coalesce("MatchedAddress", '') !~ '[0-9]$';

SELECT count(*) AS reset_toilets FROM region_only_geocode;

COMMIT;
