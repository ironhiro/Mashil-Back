# 마실 아키텍처

## 한 장 요약

```
┌─────────────────────────┐    HTTP/JSON    ┌──────────────────────────┐
│  mashil-web             │ ──────────────> │  Mashil.Api              │
│  Vite + React + TS      │                 │  ASP.NET Core 10         │
│  + MUI + TanStack Query │ <────────────── │  + Minimal API           │
│  + Kakao Map JS SDK     │                 │  + EF Core + PostGIS     │
└─────────────────────────┘                 └──────────────────────────┘
            │                                          │
            │ supabase-js (auth/storage)               │ Npgsql
            ▼                                          ▼
   ┌──────────────────┐               ┌────────────────────────────┐
   │ Supabase         │               │ Postgres + PostGIS         │
   │ (auth.users,     │               │ (dev: docker-compose       │
   │  Storage)        │               │  prod: Supabase Postgres)  │
   └──────────────────┘               └────────────────────────────┘
                                                ▲
                          tools/Mashil.CsvImport │ (1회성 임포트)
                          공중화장실정보.csv ────┘
```

## 컴포넌트

### `Mashil.Api` (ASP.NET Core 10, Minimal API)
- `/api/toilets/near` — PostGIS `ST_DWithin` 기반 근접 검색 (`Location` not null 인 행만)
- `/api/toilets/search` — 이름·주소 ILIKE 검색 (위치 미확정 행도 포함)
- `/api/toilets/{id}` — 상세
- `/api/toilets/{id}/ratings` — 별점 upsert
- `/api/toilets/{id}/comments` — 댓글 (사용자: 작성, 관리자: 숨김)
- `/api/change-requests` — 화장실 추가/수정/삭제 **요청** (관리자 승인 후 반영)
- `/api/admin/change-requests` — 관리자 큐
- `YhLibrary.Http.ApiClient` 가 typed-client 로 등록되어 있음 (Kakao Local API 호출용)

### `Mashil.Domain`
- `Toilet`: 좌표·주소 모두 nullable. `CoordinateSource` / `AddressSource` enum 으로 출처 추적. `IsLocatable` 계산 프로퍼티로 지도 표시 가능 여부 판단
- `UserProfile`: Supabase auth.users.id 와 1:1 매핑 (`Guid`)
- `Rating`, `Comment`, `ChangeRequest`

### `Mashil.Infrastructure`
- `MashilDbContext` — EF Core 10 + Npgsql + NetTopologySuite
- 인덱스: `toilets.management_no` unique, `toilets.location` GIST, `ratings(user_id, toilet_id)` unique
- enum 컬럼은 모두 string 변환 (PostgreSQL native enum 대신 변경 비용↓)

### `tools/Mashil.CsvImport`
- 공중화장실정보.csv (EUC-KR) → DB 일회성 임포트
- 좌표/주소 부재에 따라 4-way 분류 (있음+있음 / 좌표만 / 주소만 / 둘다없음) 통계 출력
- `관리번호` 기준 upsert — 같은 CSV 재실행해도 안전

### `D:\Code\Mashil-web` (Vite + React 19 + TS + MUI 6, **백엔드와 별도 디렉터리**)
- React Router v7 + TanStack Query
- Kakao Map JS SDK (지도 표시) — `src/lib/kakaoMap.ts` 가 SDK 동적 로딩 담당
- `@supabase/supabase-js` (auth)
- 백엔드 호출은 dev 시 vite proxy `/api → :5099` 로 연결
- **왜 분리했나**: Vite 6 가 프로젝트 절대경로의 `#` 문자를 dep optimizer 내부 URL 인코딩에서 처리 못 함 (백엔드 경로 `D:\Code\C#\Mashil` 의 `C#`). 프런트만 `D:\Code\Mashil-web` 으로 옮겨서 우회. 추후 Vite 픽스되면 다시 `web/` 하위로 복귀 가능.

## 좌표 / 주소 데이터 정책

CSV 한 행이 가질 수 있는 4가지 상태:

| 상태 | DB 표현 | UI 표시 |
|---|---|---|
| 좌표 ✓ + 주소 ✓ | `Location` 채움, `AddressSource = CsvRoad` 또는 `CsvLot` | 지도 + 리스트 |
| 좌표 ✓ + 주소 ✗ | `Location` 채움, 주소는 `null` | 지도 + 리스트 (주소는 reverse-geocode 큐) |
| 좌표 ✗ + 주소 ✓ | `Location = null`, 주소 채움 | 리스트만 (좌표는 geocode 큐) |
| 좌표 ✗ + 주소 ✗ | 둘 다 `null` | 검색 결과로만 노출, 지도/근접에서 제외 |

Geocoding/reverse-geocoding 은 **import 시점에 호출하지 않음** — Kakao Local API rate limit 보호. 별도 백그라운드 워커가 큐를 소비하는 구조 (`CoordinateSource = Unknown` 이고 주소 ≠ null 인 행을 청크로 처리). `CoordinateSource` 가 `KakaoGeocoded` 로 갱신되면 큐에서 제외.

## 인증

**로컬 JWT** (이메일/비밀번호) — Mashil.Api 가 직접 발급/검증. Supabase 는 추후 옵션.

- `POST /api/auth/register` — 이메일/비밀번호 + (선택) 표시이름. `IPasswordHasher<UserProfile>` 로 해시, 즉시 JWT 발급
- `POST /api/auth/login` — 비밀번호 검증, `LastLoginAt` 갱신, JWT 발급
- `GET /api/auth/me` — `RequireAuthorization`. JWT sub 클레임으로 본인 프로필 조회
- 평점/댓글/변경요청 POST: `RequireAuthorization()`
- `/api/admin/change-requests`: `RequireAuthorization("Admin")` (`UserRole.Admin` 인 UserProfile 만)
- 사용자 ID 추출: `ClaimsPrincipal.GetUserIdOrThrow()` (`Mashil.Api/Auth/CurrentUser.cs`)
- JWT 서명 키는 `appsettings.json:Jwt:SigningKey` (32자 이상). dev 는 placeholder, **prod 배포 시 반드시 교체**

프런트 측 (`D:\Code\Mashil-web`):
- `src/auth/AuthContext.tsx` — `localStorage` 에 토큰/유저 정보 영속, 새로고침에도 로그인 유지
- `src/api.ts` `apiFetch` — `auth: true` 옵션이면 `Authorization: Bearer ...` 자동 첨부
- `/login` 라우트에서 로그인/회원가입 한 폼 (탭으로 토글)
- AppBar 에 로그인 상태 + 로그아웃 버튼

## 외부 API

| API | 용도 | 호출 위치 |
|---|---|---|
| Kakao Local — 주소→좌표 | geocode 큐 처리 | 백엔드 (`YhLibrary.Http.ApiClient`) |
| Kakao Local — 좌표→주소 | reverse-geocode 큐 처리 | 백엔드 |
| Kakao Mobility — 도보 경로 v3 | 길찾기 | 프런트 (Kakao JS SDK) 또는 백엔드 BFF |
| Kakao Map JS SDK | 지도 렌더링 | 프런트 |

REST API key 는 `appsettings.json` 의 `Kakao:RestApiKey` (백엔드), JS key 는 `.env.local` 의 `VITE_KAKAO_MAP_KEY` (프런트). **둘은 다른 키.**

## 의존성

- `YhLibrary.Http` — typed `ApiClient`, `RetryHandler`, `SendRawAsync` 모두 사용 가능 (Kakao Local 호출 + 디버깅)
- `YhLibrary.Serialization` — 외부 API 페이로드 (de)serialize. 현재는 `JsonSerializer` 만 등록

YhLibrary 는 sibling 디렉터리(`..\MyLibrary\src\...`) 의 ProjectReference 로 직접 참조 — submodule/NuGet 미사용.

## 개발 흐름

```bash
# 1. DB 띄우기
docker compose up -d

# 2. CSV 임포트
dotnet run --project tools/Mashil.CsvImport -- "C:\Users\<user>\Downloads\공중화장실정보.csv"

# 3. 백엔드
dotnet run --project src/Mashil.Api

# 4. 프런트 (별도 터미널)
cd D:\Code\Mashil-web
npm install
npm run dev
```

## Geocode / Reverse-geocode 워커

`Mashil.Api/Background/GeocodeWorker.cs` — `BackgroundService`. 두 큐를 한 워커에서 처리.

| 큐 | WHERE | 호출 | 결과 enum |
|---|---|---|---|
| Geocode | `Location IS NULL AND CoordinateSource = Unknown AND (RoadAddress IS NOT NULL OR LotAddress IS NOT NULL)` | `GET /v2/local/search/address.json` | 매칭 → `KakaoGeocoded` / 실패 → `KakaoNoMatch` |
| Reverse-geocode | `Location IS NOT NULL AND RoadAddress IS NULL AND LotAddress IS NULL AND AddressSource = Unknown` | `GET /v2/local/geo/coord2address.json` | 매칭 → `KakaoReverse` / 실패 → `KakaoNoMatch` |

**활성화 절차**
1. https://developers.kakao.com 앱 설정에서 **REST API 키** 발급 (JS 키와 다름)
2. `appsettings.json` 의 `Kakao:RestApiKey` 에 채워넣기
3. `Geocode:Enabled = true` 로 변경
4. 백엔드 재시작 — Serilog 로 진행 상황(`Geocoding {Count} rows...`, `matched=...`) 출력

**Throttle 옵션** (`Geocode` section): `BatchSize`(기본 50) · `RequestDelayMs`(200) · `BatchSleepSeconds`(10) · `IdleSleepSeconds`(300). 기본값으로 시간당 약 9,000 호출 → 24,368개 큐는 ~2.7시간.

**매칭 실패 행 재시도 차단**: 첫 시도 실패 시 enum을 `KakaoNoMatch` 로 표시. 동일 입력으로 재호출 안 함 — 관리자가 추후 수동 보정 (`AdminManual`) 또는 `change_requests` 흐름으로 갱신.

**HTTP 인증**: Program.cs 에서 `AddHttpClient<ApiClient>` 등록 시점에 `Authorization: KakaoAK <key>` 헤더가 박힘. RestApiKey 가 비어 있으면 헤더 부재 → API 가 401 반환 → 워커가 catch + 다음 배치 시도. 그래서 키가 비어 있는데 `Enabled=true` 로 두면 무한히 401 폭격하므로 **둘이 짝**.

## 길찾기 (5)

**Kakao Mobility 공개 REST 는 도보 라우팅을 지원하지 않음** (`/v1/directions` 는 자동차 전용; 도보/자전거는 B2B 영역). 그래서 현재 구조는:

1. **앱 내 직선 안내** — `src/api.ts:computeStraightRoute(from, to)` 가 Haversine 거리 + 도보 4 km/h 추정 시간 계산. `MapView` 가 `kakao.maps.Polyline` 으로 출발-도착을 잇는 한 줄 (secondary.dark `#FB8500`) 표시. UX 의도: 화장실까지의 대략적 거리/시간 시각화. 휘어진 골목 안내 못 함.
2. **카카오맵 길찾기 딥링크** — 선택된 화장실 카드의 "카카오맵에서 길찾기 ↗" 버튼이 공식 문서 형식 `https://map.kakao.com/link/from/{sName},{sLat},{sLng}/to/{eName},{eLat},{eLng}` (WGS84) 으로 새 창 열기. 실제 경로 폴리라인은 카카오맵 측에서 렌더. 위치 권한 없으면 출발지 빠진 `link/to/{name},{lat},{lng}` 폴백. 도보 모드 강제는 이 URL 형식에서 미지원 — 카카오맵 진입 후 사용자가 도보 토글 (시도했던 `?rt=...&target=walk` 형식은 `rt=` 가 WTM 한국좌표계 미터를 요구해 lat/lng 입력으로는 도착지 해석 실패).

**왜 두 개 다 두는가**: (1) 은 우리 페이지를 떠나지 않고 즉시 보이는 가벼운 안내 — sub-1km 도심 케이스에선 충분. (2) 는 골목길 정확한 경로가 필요할 때의 escape hatch.

진짜 in-page 보행 polyline 이 필요하면 **TMAP (SKT) 보행자 API** 통합이 다음 단계 (별도 키 발급 필요, 무료 tier 월 수천 건). 그 경우 `routeFrom`/`routeTo` 만 받던 props 가 `routePath: LatLng[]` 로 바뀌고 `Polyline.setPath(...)` 로 실제 좌표 시퀀스 그림.

## 관리자 화면 (6)

`/admin/change-requests` — `RequireAdmin` 가드. 비-Admin 접근 시 안내 / 비로그인 → `/login` redirect.

- 상태 필터 (Pending / Approved / Rejected / 전체)
- 테이블: id / kind / target / status / 제출 시각 / payload preview / 검토 메모 입력 + 승인 / 반려
- TanStack Query mutation 후 `invalidateQueries(["admin", "change-requests"])` 로 자동 리프레시
- 엔드포인트는 이미 (2) 단계에서 `RequireAuthorization("Admin")` 으로 게이트됨

**Admin 사용자 만들기**: register 로 가입한 사용자는 기본 `User` 롤. SQL 로 승격:
```sql
UPDATE user_profiles SET "Role" = 'Admin' WHERE "Email" = 'admin@example.com';
```
(EF 가 enum 을 string 으로 저장하므로 단순 업데이트 — migration 불필요)

승인된 변경 요청을 실제 Toilet 레코드에 반영하는 자동화는 추후 — 현재는 상태만 변경되며 admin endpoint 의 `// TODO: when Approved, apply the payload to the target Toilet in a follow-up service.` 주석에 명시.

## 다음 마일스톤 (남은 항목)

1. ~~EF Core Migration 첫 발행~~ ✓
2. ~~JWT 미들웨어 + `[Authorize]` 적용~~ ✓
3. ~~Kakao Map 컴포넌트 + `near` 결과 핀 표시~~ ✓
4. ~~Geocode/Reverse-geocode 워커~~ ✓
5. ~~길찾기~~ ✓ (직선; Kakao Mobility 도보 라우팅으로 업그레이드는 옵션)
6. ~~관리자 화면~~ ✓ (큐+승인/반려; 승인된 요청의 자동 반영은 별도 서비스로)
7. Supabase 운영 환경 마이그레이션 동기화 (현재는 로컬 JWT — Supabase 전환은 옵션)
8. (신규) ChangeRequest 작성 UI — 사용자가 잘못된 좌표/주소를 신고할 수 있는 인디케이터/폼
9. (신규) 평점·댓글 UI — 백엔드/api 함수는 준비됨, 프런트 UI 미구현
