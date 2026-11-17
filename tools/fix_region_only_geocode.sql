-- 지역 대표 좌표로 찍힌 지오코딩 결과 정리 (#14, DATA-10·24)
-- 대상: Geocode 좌표가 실제로 쓰이는 화장실(CoordinateSource = KakaoGeocoded) 중
--   (a) 도로명·지번 주소에 숫자가 하나도 없음 (시도·시군구·읍면동·도로명만 — 예전 GeocodeWorker 이관분)
--   (b) 좌표가 번호 없는 캐시 결과(MatchedAddress 가 숫자로 끝나지 않음)와 같음 (번호 검증 전 GeocodingService 결과)
--   이름으로 찾은 결과(캐시 키 place:...)는 한 장소를 가리키므로 대상이 아니다.
-- 처리: Geocode 오버라이드를 끄고 좌표를 비워 지오코딩 대기(Unknown)로 되돌린다. 번호 없는 주소 캐시 행은 지운다.
--   → 수정된 GeocodingService 가 다시 처리: 번호가 맞는 후보만 채택, 안 되면 이름으로, 그래도 없으면 KakaoNoMatch.
-- 관리자 좌표(AdminManual)·원본 좌표(Csv)는 건드리지 않는다. 여러 번 실행해도 결과가 같다.
--
-- 좌표 비교: 소수 6자리(약 0.1m)로 맞춘 뒤 같음으로 조인한다 (해시 조인).
--   예전 판은 화장실마다 jsonb 를 풀어 캐시 행과 abs(차이) < 1e-7 로 하나씩 비교해(5만 × 264)
--   운영 DB(B1ms)에서 30분이 넘어도 끝나지 않았다 (2026-11-17).
--
-- 사용: 먼저 1) 만 실행해 대상 수를 확인하고, 2) 를 실행한다.
--   psql -v ON_ERROR_STOP=1 -f tools/fix_region_only_geocode.sql

-- 1) 미리 보기
WITH bad_cache AS (
    SELECT DISTINCT round("Latitude"::numeric, 6) AS lat, round("Longitude"::numeric, 6) AS lng
    FROM geocode_cache
    WHERE "Status" = 'Matched' AND "QueryKey" NOT LIKE 'place:%' AND coalesce("MatchedAddress", '') !~ '[0-9]$'
), geo AS (
    SELECT o."ToiletId",
           round((o."ValueJson"->>'lat')::numeric, 6) AS lat, round((o."ValueJson"->>'lng')::numeric, 6) AS lng
    FROM toilet_overrides o
    WHERE o."FieldName" = 'location' AND o."IsActive" AND o."Source" = 'Geocode'
), target AS (
    SELECT DISTINCT t."Id"
    FROM toilets t
    JOIN geo g ON g."ToiletId" = t."Id"
    WHERE t."CoordinateSource" = 'KakaoGeocoded'
      AND ((coalesce(t."RoadAddress", '') !~ '[0-9]' AND coalesce(t."LotAddress", '') !~ '[0-9]')
           OR EXISTS (SELECT 1 FROM bad_cache c WHERE c.lat = g.lat AND c.lng = g.lng))
)
SELECT (SELECT count(*) FROM target) AS toilets_to_reset,
       (SELECT count(*) FROM geocode_cache
        WHERE "Status" = 'Matched' AND "QueryKey" NOT LIKE 'place:%' AND coalesce("MatchedAddress", '') !~ '[0-9]$') AS cache_rows_to_delete;

-- 2) 적용
BEGIN;

CREATE TEMP TABLE region_only_geocode ON COMMIT DROP AS
WITH bad_cache AS (
    SELECT DISTINCT round("Latitude"::numeric, 6) AS lat, round("Longitude"::numeric, 6) AS lng
    FROM geocode_cache
    WHERE "Status" = 'Matched' AND "QueryKey" NOT LIKE 'place:%' AND coalesce("MatchedAddress", '') !~ '[0-9]$'
), geo AS (
    SELECT o."ToiletId",
           round((o."ValueJson"->>'lat')::numeric, 6) AS lat, round((o."ValueJson"->>'lng')::numeric, 6) AS lng
    FROM toilet_overrides o
    WHERE o."FieldName" = 'location' AND o."IsActive" AND o."Source" = 'Geocode'
)
SELECT DISTINCT t."Id"
FROM toilets t
JOIN geo g ON g."ToiletId" = t."Id"
WHERE t."CoordinateSource" = 'KakaoGeocoded'
  AND ((coalesce(t."RoadAddress", '') !~ '[0-9]' AND coalesce(t."LotAddress", '') !~ '[0-9]')
       OR EXISTS (SELECT 1 FROM bad_cache c WHERE c.lat = g.lat AND c.lng = g.lng));

UPDATE toilet_overrides o SET "IsActive" = FALSE
FROM region_only_geocode r
WHERE o."ToiletId" = r."Id" AND o."FieldName" = 'location' AND o."Source" = 'Geocode' AND o."IsActive";

UPDATE toilets t SET "Location" = NULL, "Latitude" = NULL, "Longitude" = NULL,
       "CoordinateSource" = 'Unknown', "UpdatedAt" = now()
FROM region_only_geocode r
WHERE t."Id" = r."Id";

DELETE FROM geocode_cache
WHERE "Status" = 'Matched' AND "QueryKey" NOT LIKE 'place:%' AND coalesce("MatchedAddress", '') !~ '[0-9]$';

SELECT count(*) AS reset_toilets FROM region_only_geocode;

COMMIT;
