# 마실 API

런타임 OpenAPI 스펙은 백엔드 실행 후 `https://localhost:7099/swagger` (Development)에서 확인한다. 이 문서는 엔드포인트의 의도와 계약을 사람이 읽기 위한 요약본이다.

**JSON 규칙:** enum은 문자열로 주고받는다 (`"Pending"`, `"Scheduled"`, `"Mon"` …). 시각은 `"HH:mm"`(KST, `24:00` 가능), 날짜는 `"yyyy-MM-dd"`.

## Toilets (public)

| Method | Path | 용도 |
|---|---|---|
| GET | `/api/toilets/near?lat=&lng=&radiusM=&limit=` | PostGIS 근접 검색. 좌표 있는 행만, `Removed` 제외. `radiusM` 은 1~10000 으로 자른다, `limit` 1~5000 (기본 1000) |
| GET | `/api/toilets/recommended?lat=&lng=&radiusM=&limit=` | 지금 가기 좋은 곳, 점수순. 아래 "추천" 참고 |
| GET | `/api/toilets/in-bounds?north=&south=&east=&west=&limit=` | 지도 viewport 안 |
| GET | `/api/toilets/search?q=&limit=&offset=&lat=&lng=` | 이름·주소 ILIKE. 위치 미확정 행도 포함. `lat`·`lng` 를 주면 가까운 순(위치 미확정은 뒤), 없으면 이름순. `limit`(기본 50, 최대 200)·`offset` 으로 나눠 받고, 맞는 곳 전체 수는 응답 헤더 `X-Total-Count` |
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
| `averageRating` | `4.2` | 평점 평균. 평점이 없으면 `null` |
| `ratingCount` | `3` | 그 평균을 낸 평점 수 (2026-11-15 추가) |
| `commentCount` | `5` | 가려지지 않은 댓글 수. 평점 수가 아니다 |

> 웹의 `src/lib/openStatus.ts` (정규식 판정)는 `openStatus` 로 대체하면 된다. 정규식은 판정 불가 9.5%, 요일 무시 오답 가능 5.2%였고, 서버 판정은 판정 불가 0.2%다.

### 추천 `/recommended` — 2026-11-15

반경(`radiusM` 기본 1000, 100~5000) 안에서 **닫히지 않았고** 평점이나 댓글 평가가 있는 곳만, 점수 높은 순으로 `limit`(기본 5, 최대 20)건.

- 점수 = 품질 50% (별점 베이지안 평균, 사전값 3.0 · 가중치 2) + 믿음 30% (댓글들이 받은 도움돼요 비율, 라플라스 평활) + 가까움 20%
- 열려 있는지 모르면 0.85 배. 식은 `Mashil.Domain.ToiletRecommender`

응답 행: `{ toilet: <near 행>, score, ratingCount, helpfulUp, helpfulDown }`

### `/{id}` 상세 — 2026-11-14 추가 필드

| 필드 | 설명 |
|---|---|
| `kindCode`, `status`, `scheduleStatus`, `openStatus` | 위와 같음. `status` = `Active` / `Missing` / `Removed` |
| `schedules[]` | `{ day: "Mon".."Sun"\|"Holiday", open: "06:00", close: "22:00", closed: false }` — 요일별, 같은 요일에 여러 칸 가능(점심 분할) |
| `facilities` | 변기 수 9종 + `emergencyBell`, `entranceCctv`, `diaperChanger` |
| `source` | `{ provider: "LOCALDATA", dataReferenceDate, coordinateSource, editedByPeople, lastRefreshedAt }` — 출처 표시 (MAP-07). `editedByPeople` = 관리자 수정·승인된 변경 요청이 반영됨. `lastRefreshedAt` = 원본을 마지막으로 받아 반영·확인한 시각 (UTC, 2026-11-15 추가) |
| `averageRating`, `ratingCount` | 평점 평균(없으면 null)과 평점 수 — 2026-11-15 추가 |

## Auth — 2026-11-14 OAuth 전환 (P4)

설정과 흐름은 [AUTH.md](AUTH.md). 실패 응답은 `{ "error": "<code>" }`.

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/auth/providers` | 켜진 제공자 `{ providers: ["kakao", ...] }` |
| GET | `/api/auth/nonce` | `{ nonce, expiresAt }` 5분, 1회용 |
| POST | `/api/auth/oauth/{provider}` | provider = `kakao` / `google` / `apple`. body: `{ idToken?, accessToken?, authorizationCode?, redirectUri?, nonce?, device?, displayName? }` |
| POST | `/api/auth/refresh` | body: `{ refreshToken }` → 새 쌍 (회전) |
| POST | `/api/auth/logout` | body: `{ refreshToken }` → 204 |
| GET | `/api/auth/me` | `{ id, email, displayName, role, providers[], createdAt }` |
| PATCH | `/api/auth/me` | body: `{ displayName }` (최대 20자) |
| GET | `/api/auth/consents` | `{ required, documents: [{ docType, version, agreed }] }` |
| POST | `/api/auth/consents` | body: `{ items: [{ docType: "Terms"\|"Privacy"\|"LocationTerms", version }] }` → 204, 옛 버전이면 409 |
| DELETE | `/api/auth/me` | body: `{ provider, idToken?, accessToken?, authorizationCode?, nonce? }` (재인증) → 204 |
| POST | `/api/auth/register`, `/login` | **레거시·개발 전용** (`Auth:LegacyPasswordLogin`). 운영에서는 404 |

로그인·갱신 응답:

```json
{
  "accessToken": "...", "accessTokenExpiresAt": "2026-11-14T14:30:00Z",
  "refreshToken": "...", "refreshTokenExpiresAt": "2026-12-14T14:00:00Z",
  "token": "... (accessToken 과 같음, 기존 웹 호환)",
  "userId": "...", "displayName": "마실러", "role": "User",
  "isNewUser": true, "consentRequired": true
}
```

- Access Token 이 만료되면 401 과 헤더 `X-Auth-Error: token_expired` → `/refresh` 후 재시도
- `/refresh` 가 401 `reauth_required` 면 로그아웃 처리 후 로그인 화면
- 탈퇴한 사용자의 Access Token 은 만료 전이라도 즉시 401
- 오류 코드: `invalid_request`, `invalid_credential`, `nonce_required`, `nonce_invalid`, `provider_disabled`, `provider_unknown`, `idp_unavailable`, `reauth_required`, `identity_mismatch`, `consent_required`, `consent_version_mismatch`

## Ratings / Comments (auth required)

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/toilets/{toiletId}/ratings` | upsert (한 사용자당 한 점수); body: `{ score: 1..5 }` → 204. 범위 밖 400, 없는 화장실 404 |
| GET | `/api/toilets/{toiletId}/comments` | 숨김 제외, 최신순 100건. 토큰을 보내면 **내가 차단한 사용자 댓글도 제외** |
| POST | `/api/toilets/{toiletId}/comments` | body: `{ text }` (최대 1000자) → 201 댓글 행 (목록과 같은 모양). 빈 글 400, 없는 화장실 404 |
| PUT | `/api/comments/{id}/vote` | body: `{ value: 1\|-1\|0 }` (도움돼요 / 아니에요 / 취소) → 200 `{ upCount, downCount, myVote }`. 자기 댓글 400, 가려졌거나 없는 댓글 404 |

댓글 행: `{ id, toiletId, userId, authorName, authorDeleted, text, hidden, createdAt, updatedAt, isMine, upCount, downCount, myVote }`. 탈퇴한 작성자는 `authorName = "탈퇴한 사용자"`, 이름이 없으면 `"익명"`. `myVote` 는 토큰을 보냈을 때만 의미가 있다 (1, -1, 0 = 없음).

같은 사람이 평점이나 표를 동시에 여러 번 보내도 한 건으로 갱신된다 (유니크 충돌이면 한 번 다시 읽어 갱신).

쓰기(평점·댓글·변경 요청·신고)는 약관 동의 전이면 403 `consent_required` (`Consents:EnforceOnWrite`, 개발 환경에서는 꺼 둠).

## 내 활동 (auth required) — 2026-11-15

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/me/comments` | 최신순 100건. `[{ id, toiletId, toiletName, text, hidden, createdAt, updatedAt }]`. 신고로 가려진 댓글도 `hidden = true` 로 포함 (본인에게 이유를 알리려고). 화장실이 지워졌으면 `toiletName = null` |
| GET | `/api/me/change-requests` | 최신순 100건. `[{ id, kind, status, targetToiletId, toiletName, createdAt, reviewedAt, reviewNote }]`. 추가 요청이면 `toiletName` 은 제보에 적힌 이름 |

## Moderation (auth required) — UGC-03~05

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/comments/{id}/reports` | body: `{ reason: "Spam"\|"Abuse"\|"False"\|"Other", detail? }` → 201. 중복 409, 자기 댓글 400. 서로 다른 3명이 신고하면 자동 숨김 |
| POST | `/api/users/{userId}/block` | 204. 자기 자신 400 |
| DELETE | `/api/users/{userId}/block` | 204 |
| GET | `/api/users/me/blocks` | `[{ userId, displayName, blockedAt }]` |

## Change Requests (auth required)

| Method | Path | 비고 |
|---|---|---|
| POST | `/api/change-requests` | body: `{ kind: "Add"\|"Edit"\|"Delete", targetToiletId?, payloadJson }`. **`"Update"` 도 `Edit` 으로 받는다** (웹 하위 호환, G3) |

`payloadJson` 키: `name`, `kind`, `roadAddress`, `lotAddress`, `latitude`, `longitude`, `openHours`, `openHoursDetail`, `phone`, `reason`. Edit 은 바뀐 필드만, 지우려면 `null`.

## Admin (admin role required)

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/admin/change-requests?status=Pending` | 큐 조회. 승인된 행은 `applied: { toiletId, createdToilet, markedRemoved, fields, activeFields }` (ADM-01, 2026-11-15) |
| PATCH | `/api/admin/change-requests/{id}` | body: `{ approve: bool, note? }` |
| GET | `/api/admin/comment-reports?status=Open` | 댓글별로 묶은 신고 큐, 신고 수 순 (ADM-02). 행마다 `toiletName`, `authorName`, 신고 내용 `details`(최근 5개) |
| PATCH | `/api/admin/comments/{id}` | body: `{ hidden: bool }`. 숨기면 열린 신고 Actioned, 복원하면 Dismissed |

**승인 반영 (2026-11-14 구현):** `approve=true` 면 같은 트랜잭션에서 화장실에 반영하고 `200 { toiletId, appliedFields, ignoredKeys }`.
- Edit: 필드별 오버라이드(source = ChangeRequest). 개방시간을 바꾸면 스케줄을 다시 만들고, 주소를 바꾸면 지오코딩 좌표를 무효화해서 다시 지오코딩한다. 위도·경도는 둘 다 있고 국내 범위일 때만 반영. `kind`는 반영하지 않음 (`ignoredKeys`)
- Delete: status = Removed
- Add: 새 화장실 (`Origin = UserSubmitted`, 관리번호 `USR-{요청 id}`)
- 원본을 직접 고치지 않으므로 재임포트해도 유지된다
- `approve=false` → `204`. 이미 처리된 요청 → `409`. 반영할 수 없는 내용 → `422`

### 데이터 운영 — 2026-11-15 (ADM-03~06)

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/admin/import-runs?limit=50` | 임포트 실행 이력, 최신순. 행마다 신규·변경·동일·누락·복귀·삭제(대상)·`newIssues` (DATA-23) |
| GET | `/api/admin/import-runs/{id}` | 실행 리포트 `{ run, newIssuesByType }` |
| POST | `/api/admin/import-runs/upload` | 파일 업로드 임포트 (DATA-19). `multipart/form-data`: `file`, 선택 `force=true`, `skipValidation=true`. **202** + 작업 |
| POST | `/api/admin/import-runs/fetch` | 지금 내려받아 임포트 (일일 갱신 한 번). body 선택 `{ force, skipValidation }`. 202 + 작업 |
| POST | `/api/admin/import-runs/renormalize` | 재정규화 (DATA-14). body 선택 `{ onlyOutdated }`. 202 + 작업 |
| POST | `/api/admin/import-runs/{id}/approve-removals` | 보류된 대량 삭제 승인 (DATA-15). `{ count }` |
| POST | `/api/admin/import-runs/{id}/reject-removals` | 거부: 삭제하지 않고 대상의 연속 누락 횟수를 0으로 (Missing 유지). `{ count }` |
| GET | `/api/admin/jobs` · `/api/admin/jobs/{id}` | 관리자 작업 (이 API 인스턴스의 최근 50건). `state` = Queued / Running / Succeeded / Failed / Busy, 결과 `runId` |
| GET | `/api/admin/normalization/report` | 정규화 리포트 (DATA-16): 필드별 성공률 + Open 이슈 상위 원본 값 50개 |
| GET | `/api/admin/issues?field=&type=&status=&rawValue=&toiletId=&offset=&limit=` | 정규화 이슈 목록 `{ total, items }` (DATA-12) |
| GET | `/api/admin/issues/groups?field=&type=&status=Open` | 같은 (필드, 유형, 원본 값) 묶음, 건수 순 |
| GET | `/api/admin/issues/counts` | 필드·유형·상태별 건수 |
| POST | `/api/admin/issues/bulk` | body `{ status: "Resolved"\|"Ignored"\|"Open", ids? , fieldName?, type?, rawValue?, toiletId?, fromStatus? }`. `{ count }` |
| GET | `/api/admin/value-mappings?field=&includeInactive=` | 매핑 사전 |
| POST | `/api/admin/value-mappings` | 매핑 등록 (DATA-13). body `{ fieldName: "opening_hours", rawKey, hours: { status, entries: [{ day, open, close, closed }] } }`. 응답 `{ mapping, issuesChanged, affectedToilets, job }` |
| DELETE | `/api/admin/value-mappings/{id}` | 매핑 해제. 이 매핑으로 해결된 이슈는 다시 Open, 대상 화장실 재정규화 |
| GET | `/api/admin/toilets/{id}` | 편집 화면: 지금 값, 원본 값, 오버라이드 이력(꺼진 것 포함), 변경 요청 (ADM-04) |
| POST | `/api/admin/toilets/{id}/overrides` | 직접 수정. 바꿀 필드만: `name`, `roadAddress`, `lotAddress`, `phone`, `openHours`, `openHoursDetail`, `latitude`+`longitude`, `status`(`Active`\|`Removed`). `null` = 그 필드의 관리자 오버라이드 끄기. `{ toiletId, appliedFields, clearedFields, ignoredKeys }` |
| DELETE | `/api/admin/toilets/{id}/overrides/{overrideId}` | 오버라이드 하나 끄기 (출처 상관없음). 204 |
| GET | `/api/admin/app-status` | 점검 모드 `{ effective, stored, config }` (ADM-06) |
| PUT | `/api/admin/app-status` | body `{ maintenance, readOnly, message?, until? }` |

- **오버라이드 우선순위:** 관리자(30) > 변경 요청(20) > 지오코딩(10) > 이전 좌표(5) > 원본. 관리자 수정은 저장 뒤 원본부터 다시 계산하므로 끄면 다음 순위나 원본 값으로 돌아간다.
- **해결·무시한 이슈는 다시 생기지 않는다:** 재임포트·재정규화가 같은 (필드, 유형, 원본 값)의 이슈를 다시 만들지 않는다. 다시 나온 Open 이슈는 같은 행(id, 접수 시각)을 유지한다. 그래서 실행 시간 안에 생긴 이슈 = 새 이슈.
- **매핑 사전:** 지금 매핑을 읽는 필드는 개방시간뿐이다. 키는 개방시간 이슈의 원본 값(`개방시간|개방시간상세`, 공백 정리). 등록·해제하면 대상 화장실의 정규화 버전을 비우고 백그라운드로 그 화장실만 다시 정규화한다 (작업이 끊겨도 "재정규화 (바뀐 것만)"이나 다음 임포트가 처리).
- **긴 작업 (업로드·다운로드·재정규화):** API 프로세스 안의 단일 소비자 큐(`AdminJobRunner`)에서 실행하고 요청은 바로 `202`. 임포트와 같은 advisory lock 으로 Worker 의 일일 갱신·다른 인스턴스와 겹치지 않는다 — 시작할 때 락이 잡혀 있으면 `409 import_busy`, 실행 시점에 잡혀 있으면 작업 `Busy` (매핑 반영은 1분 뒤 다시 시도). 작업 상태는 그 인스턴스 메모리에만 있고 결과는 `import_runs` 에 남는다. API 를 재배포하면 진행 중이던 작업은 끊기지만 실행 행은 다음 실행이 Abandoned 로 정리하고, 임포트·재정규화는 멱등이라 다시 시키면 같은 결과가 된다.

## 앱 상태 (public)

| Method | Path | 비고 |
|---|---|---|
| GET | `/api/app/status` | `{ maintenance, readOnly, message, until }` (OPS-05). 캐시하지 말 것 — 점검을 끄면 바로 반영돼야 한다. 값은 관리자 화면(DB `app_status`) + 설정 `App:Status` 폴백 (설정에서 켠 플래그는 DB 와 상관없이 켜짐, 비상 스위치). 서버는 `App:Status:CacheSeconds`(기본 5초) 캐시 |

**점검 중 쓰기 차단 (OPS-05, 2026-11-15):** `maintenance` 나 `readOnly` 가 켜져 있으면 `/api` 아래 POST·PUT·PATCH·DELETE 는 `503 { error: "maintenance", maintenance, readOnly, message, until }` (+ `Retry-After`). 조회는 그대로. `/api/auth`·`/api/admin` 과 관리자 계정의 요청은 막지 않는다.
| GET | `/api/app/version?platform=ios\|android` | `{ minimum, latest, storeUrl }` (APP-09). 앱 버전이 minimum 보다 낮으면 강제, latest 보다 낮으면 권장 업데이트. 설정 `App:Versions:{Ios,Android}` — 예: `App__Versions__Android__Latest=1.2.0`. 모르는 platform 은 400 |

## 헬스 체크

| Method | Path | 용도 |
|---|---|---|
| GET | `/healthz` | liveness — 프로세스 생존 |
| GET | `/healthz/ready` | readiness — DB 연결까지 확인. Caddy 업스트림 전환·업타임 감시용 (503 = 준비 안 됨) |
