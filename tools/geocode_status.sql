-- 좌표/주소 출처별 분포
SELECT
  "CoordinateSource",
  COUNT(*) FILTER (WHERE "Location" IS NOT NULL) AS with_coord,
  COUNT(*) FILTER (WHERE "Location" IS NULL)     AS without_coord,
  COUNT(*) AS total
FROM toilets
GROUP BY "CoordinateSource"
ORDER BY total DESC;

-- Geocode 큐 (워커가 처리할 대상)
SELECT
  COUNT(*) AS pending_geocode
FROM toilets
WHERE "Location" IS NULL
  AND "CoordinateSource" = 'Unknown'
  AND ("RoadAddress" IS NOT NULL OR "LotAddress" IS NOT NULL);

-- Reverse-geocode 큐
SELECT
  COUNT(*) AS pending_reverse_geocode
FROM toilets
WHERE "Location" IS NOT NULL
  AND "RoadAddress" IS NULL
  AND "LotAddress" IS NULL
  AND "AddressSource" = 'Unknown';
