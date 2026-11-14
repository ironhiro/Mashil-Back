# 마실 API

런타임 OpenAPI 스펙은 백엔드 실행 후 `https://localhost:7099/swagger` (Development)에서 확인한다. 이 문서는 엔드포인트의 의도와 계약을 사람이 읽기 위한 요약본이다.

**JSON 규칙:** enum은 문자열로 주고받는다 (`"Pending"`, `"Scheduled"`, `"Mon"` …). 시각은 `"HH:mm"`(KST, `24:00` 가능), 날짜는 `"yyyy-MM-dd"`.

## Toilets (public)

| Method | Path | 용도 |
|---|---|---|
| GET | `/api/toilets/near?lat=&lng=&radiusM=&limit=` | PostGIS 근접 검색. 좌표 있는 행만, `Removed` 제외 |
| GET | `/api/toilets/in-bounds?north=&south=&east=&west=&limit=` | 지도 viewport 안 |
| GET | `/api/toilets/search?q=&limit=` | 이름·주소 ILIKE. 위치 미확정 행도 포함 |
| GET | `/api/toilets/{id}` | 상세 |

### `/near` 응답 행 — 2026-11-14 추가 필드

기존 필드(`openHours`, `openHoursDetail` 원문 포함)는 하위 호환을 위해 그대로 둔다.

| 필드 | 예 | 설명 |
|---|---|---|
| `kindCode` | `"Open"` | `Public` / `Open` / `Temporary` / `Mobile` / `Other` |
| `scheduleStatus` | `"Scheduled"` | `AlwaysOpen` / `Scheduled` / `NotOpen` / `Irregular` / `FollowsFacility` / `Unknown` |
| `openStatus.kind` | `"Open"` | `AlwaysOpen` / `Open` / `Closed` / `Unknown` — 서버가 KST·요일·공휴일·자정 넘김을 반영해 판정 |
| `openStatus.closesAt` | `"18:00"` | 열려 있을 때 닫는 시각 |
| `openStatus.nextOpen` | `{ "daysFromToday": 1, "day": "Sun", "at": "09:00" }` | 닫혀 있을 때 다음 개방 |
| `openStatus.label` | `"영업중 · 18:00까지"` | 그대로 보여 줄 수 있는 한국어 문구 |

> 웹의 `src/lib/openStatus.ts` (정규식 판정)는 `openStatus` 로 대체하면 된다. 정규식은 판정 불가 9.5%, 요일 무시 오답 가능 5.2%였고, 서버 판정은 판정 불가 0.2%다.

### `/{id}` 상세 — 2026-11-14 추가 필드

| 필드 | 설명 |
|---|---|
| `kindCode`, `status`, `scheduleStatus`, `openStatus` | 위와 같음. `status` = `Active` / `Missing` / `Removed` |
| `schedules[]` | `{ day: "Mon".."Sun"\|"Holiday", open: "06:00", close: "22:00", closed: false }` — 요일별, 같은 요일에 여러 칸 가능(점심 분할) |
| `facilities` | 변기 수 9종 + `emergencyBell`, `entranceCctv`, `diaperChanger` |
| `source` | `{ provider: "LOCALDATA", dataReferenceDate, coordinateSource, editedByPeople }` — 출처 표시 (MAP-07). `editedByPeople` = 관리자 수정·승인된 변경 요청이 반영됨 |

## Ratings / Comments (auth required)

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/toilets/{toiletId}/ratings` | upsert (한 사용자당 한 점수); body: `{ score: 1..5 }` |
| GET | `/api/toilets/{toiletId}/comments` | `hidden=false` 만 반환 |
| POST | `/api/toilets/{toiletId}/comments` | body: `{ text }` |

## Change Requests (auth required)

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/change-requests` | body: `{ kind: "Add"\|"Edit"\|"Delete", targetToiletId?, payloadJson }`. **`"Update"` 도 `Edit` 으로 받는다** (웹 하위 호환, G3) |

`payloadJson` 키: `name`, `kind`, `roadAddress`, `lotAddress`, `latitude`, `longitude`, `openHours`, `openHoursDetail`, `phone`, `reason`. Edit 은 바뀐 필드만, 지우려면 `null`.

## Admin (admin role required)

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/admin/change-requests?status=Pending` | 큐 조회 |
| PATCH | `/api/admin/change-requests/{id}` | body: `{ approve: bool, note? }` |

**승인 반영 (2026-11-14 구현):** `approve=true` 면 같은 트랜잭션에서 화장실에 반영하고 `200 { toiletId, appliedFields, ignoredKeys }`.
- Edit: 필드별 오버라이드(source = ChangeRequest). 개방시간을 바꾸면 스케줄을 다시 만들고, 주소를 바꾸면 지오코딩 좌표를 무효화해서 다시 지오코딩한다. 위도·경도는 둘 다 있고 국내 범위일 때만 반영. `kind`는 반영하지 않음 (`ignoredKeys`)
- Delete: status = Removed
- Add: 새 화장실 (`Origin = UserSubmitted`, 관리번호 `USR-{요청 id}`)
- 원본을 직접 고치지 않으므로 재임포트해도 유지된다
- `approve=false` → `204`. 이미 처리된 요청 → `409`. 반영할 수 없는 내용 → `422`

## 헬스 체크

| Method | Path | 용도 |
|---|---|---|
| GET | `/healthz` | liveness — 프로세스 생존 |
| GET | `/healthz/ready` | readiness — DB 연결까지 확인. Caddy 업스트림 전환·업타임 감시용 (503 = 준비 안 됨) |
