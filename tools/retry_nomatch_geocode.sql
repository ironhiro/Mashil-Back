-- 못 찾은(KakaoNoMatch) 화장실을 다시 지오코딩 대기(Unknown)로 되돌린다.
-- 질의 규칙이 바뀌었을 때 쓴다 (예: 붙은 시군구 띄우기, 행정동 → 법정동, 2026-11-17).
-- 질의가 그대로인 주소는 NoMatch 캐시에 걸려 API 를 다시 부르지 않는다. 바뀐 질의만 호출된다.
-- 좌표가 있는 화장실(관리자·원본·지오코딩)은 건드리지 않는다. 여러 번 실행해도 결과가 같다.
--   psql -v ON_ERROR_STOP=1 -f tools/retry_nomatch_geocode.sql

BEGIN;

UPDATE toilets SET "CoordinateSource" = 'Unknown', "UpdatedAt" = now()
WHERE "CoordinateSource" = 'KakaoNoMatch' AND "Location" IS NULL;

SELECT count(*) AS queued FROM toilets
WHERE "CoordinateSource" = 'Unknown' AND "Location" IS NULL AND "Status" <> 'Removed';

COMMIT;
