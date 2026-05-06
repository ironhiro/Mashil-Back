# 마실 API (스켈레톤)

런타임 OpenAPI 스펙은 백엔드 실행 후 `https://localhost:7099/swagger` 에서 확인. 이 문서는 엔드포인트 의도/계약을 사람이 읽기 위한 요약본.

## Toilets (public)

| Method | Path | 용도 |
|---|---|---|
| GET | `/api/toilets/near?lat=&lng=&radiusM=&limit=` | PostGIS 근접 검색 — `Location not null` 행만 |
| GET | `/api/toilets/search?q=&limit=` | 이름·주소 ILIKE — 위치 미확정 행도 포함 |
| GET | `/api/toilets/{id}` | 상세 |

## Ratings / Comments (auth required)

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/toilets/{toiletId}/ratings` | upsert (한 사용자당 한 점수); body: `{ score: 1..5 }` |
| GET | `/api/toilets/{toiletId}/comments` | `hidden=false` 만 반환 |
| POST | `/api/toilets/{toiletId}/comments` | body: `{ text }` |

## Change Requests (auth required)

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/change-requests` | body: `{ kind: 'Add'\|'Edit'\|'Delete', targetToiletId?, payloadJson }` |

## Admin (admin role required)

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/admin/change-requests?status=Pending` | 큐 조회 |
| PATCH | `/api/admin/change-requests/{id}` | body: `{ approve: bool, note? }` — 승인 시 후속 적용 트랜잭션 (TODO) |

## 헬스 체크

| Method | Path |
|---|---|
| GET | `/healthz` |
