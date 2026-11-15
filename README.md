# Mashil-Back

> **잠깐, 다녀와.**
> 한국 공중·개방 화장실 데이터 기반 길찾기/평점/댓글 사이드 프로젝트의 백엔드.

```
ASP.NET Core 10 (Minimal API) + Worker  +  EF Core 10 + PostGIS
```

> 2026-11-14: YhLibrary 의존을 제거했다. Kakao 클라이언트가 `Mashil.Import` 로 옮겨가서
> 이 저장소만으로 빌드된다 (Docker 빌드 컨텍스트 단순화, MessagePack 취약점 경고 제거).
> 아래 "사전 준비"의 YhLibrary 설정은 더 이상 필요 없다.

프런트는 [Mashil-Front](https://github.com/) (Vite + React 19 + TS + MUI 6 + Kakao Maps).
이 백엔드는 dev 시 vite proxy 가 `/api → :5099` 로 연결.

## 구조

```
Mashil/
├── Mashil.slnx
├── Directory.Build.props          공통 csproj 설정 + YhLibraryRoot 정의
├── docker-compose.yml             Postgres 16 + PostGIS
├── docs/
├── src/
│   ├── Mashil.Api/                ASP.NET Core, /api/* + Swagger + JWT
│   ├── Mashil.Domain/             엔티티 (Toilet, Rating, Comment, ChangeRequest, UserProfile)
│   ├── Mashil.Infrastructure/     EF Core + Npgsql + NetTopologySuite (+ 마이그레이션)
│   ├── Mashil.Import/             원본 보존 임포트: 인코딩 판별, 소스 어댑터, 오버라이드, 사전 검증,
│   │                              누락 처리, LOCALDATA 다운로더, 알림, 일일 작업
│   └── Mashil.Worker/             백그라운드 호스트: 일일 임포트 스케줄 + 지오코딩 (API 와 분리)
├── tests/
│   ├── Mashil.Import.Tests/              xUnit 단위 테스트
│   └── Mashil.Import.IntegrationTests/   실제 Postgres 통합 테스트 (MASHIL_TEST_DB 있을 때만)
└── tools/
    └── Mashil.CsvImport/          CLI: 파일/다운로드 임포트, 삭제 승인 (Mashil.Import 사용)
```

## 개발 시작

### 사전 준비

이 저장소 외에 [YhLibrary](https://github.com/) 도 클론해야 합니다.

**옵션 A — sibling 디렉터리 (가장 간단):**

```
어느 부모 폴더/
├── Mashil/        ← 이 저장소
└── YhLibrary/     ← 별도 클론 (이름 그대로)
```

이러면 `Directory.Build.props` 의 기본값(`..\YhLibrary` 가 아니라 `..\MyLibrary`)이 동작하지 않을 수 있습니다 — 아래 옵션 B 를 권장.

**옵션 B — 환경 변수 (위치 자유):**

```powershell
# Windows
setx YHLIBRARY_ROOT "D:\path\to\YhLibrary"
# 새 셸 열기

# bash/zsh
export YHLIBRARY_ROOT="/path/to/YhLibrary"
```

**옵션 C — 사용자별 MSBuild props (커밋 안 됨):**

저장소 루트에 `Directory.Build.props.user` 파일 생성 (`.gitignore` 처리됨):

```xml
<Project>
  <PropertyGroup>
    <YhLibraryRoot>D:\path\to\YhLibrary</YhLibraryRoot>
  </PropertyGroup>
</Project>
```

> 옵션 A 의 기본 경로가 `..\MyLibrary` 인 이유: 작성자의 로컬 환경에서 YhLibrary 가 `D:\Code\C#\MyLibrary` 디렉터리 이름으로 있기 때문. 본인 환경이 다르면 옵션 B/C 사용.

### 빌드 & 실행

```bash
# 1) Postgres + PostGIS 컨테이너
docker compose up -d

# 2) 시크릿 주입 (User Secrets) — 아래 "시크릿 설정" 섹션 참고
dotnet user-secrets --project src/Mashil.Api set "Jwt:SigningKey" "$(openssl rand -base64 48)"
dotnet user-secrets --project src/Mashil.Api set "Kakao:RestApiKey" "<본인 REST API 키>"

# 3) 스키마 적용 (마이그레이션) — 임포터·API 는 스키마를 만들지 않는다
dotnet tool restore
dotnet ef database update --project src/Mashil.Infrastructure --startup-project src/Mashil.Api
#   연결 문자열은 환경 변수 MASHIL_DB (없으면 docker-compose 기본값)

# 4) (선택) 수동 임포트 — 먼저 --dry-run 으로 DB 없이 검증 가능
dotnet run --project tools/Mashil.CsvImport -- "<공중화장실정보.csv 경로>" --dry-run
dotnet run --project tools/Mashil.CsvImport -- "<공중화장실정보.csv 경로>"
dotnet run --project tools/Mashil.CsvImport -- --fetch          # LOCALDATA 에서 직접 내려받아 임포트
#   같은 파일이면 Skipped (--force 로 강제). 크기·행 수 검증을 넘기려면 --no-validate
#   대량 삭제가 보류(PendingApproval)되면 확인 후: --approve-removals=<runId>

# 5) 백엔드 (https://localhost:7099/swagger)
dotnet run --project src/Mashil.Api

# 6) 워커 — 매일 KST 04:30 LOCALDATA 갱신 + 지오코딩
dotnet run --project src/Mashil.Worker
#   즉시 한 번 돌려 보려면: Import__Schedule__RunOnStartup=true

# 테스트 (단위: Mashil.Import.Tests·Mashil.Api.Tests. 통합 테스트는 MASHIL_TEST_DB 가 있을 때만 실행, 임시 DB 를 만들고 지움)
export MASHIL_TEST_DB="Host=localhost;Port=5432;Database=postgres;Username=mashil;Password=mashil_dev"
dotnet test

# API 회귀 테스트 (Postman 컬렉션, API 를 띄운 상태에서). Postman 앱에서는 두 파일을 import 해 Runner 로 실행
npx newman@6 run tests/postman/Mashil.postman_collection.json -e tests/postman/local.postman_environment.json

# 스트레스 테스트 (k6, 로컬 API 에만). 20 → PEAK req/s 단계 증가, 약 6분
k6 run -e PEAK=400 tests/load/mashil-load.js
```

스트레스 테스트(`tests/load/mashil-load.js`)는 읽기 90% (근처 50, 추천 10, 상세 15, 댓글 목록 15)와 쓰기 10% (평점 5, 도움돼요 4, 댓글 1)를 섞는다. 기준: 오류 1% 미만, p95 500ms 미만. 2026-11-15 개발 PC(API·DB·k6 한 대) 기준 약 600 req/s 까지 p95 27ms, 700 req/s 부터 CPU 포화(대부분 Postgres). 테스트 계정·댓글·평점이 개발 DB 에 남는다.

Postman 컬렉션(`tests/postman/`)은 실행마다 테스트 계정 둘을 새로 만들고(레거시 이메일 가입, 개발 설정에서만 켜짐) 인증·화장실·평점·댓글과 도움돼요·내 활동·제보·신고와 차단·토큰 회전을 순서대로 확인한다. 테스트 댓글·평점·제보·신고가 개발 DB 에 남으므로 운영 서버에는 돌리지 않는다. 생성 스크립트 없이 JSON 을 직접 고친다.

### 일일 갱신이 하는 일 (Mashil.Worker)

1. LOCALDATA 정보 페이지 → `validate/download-count` → 전국파일 다운로드 (429 면 15·30·60분 대기 후 재확인)
2. 소스별 advisory lock, 남아 있는 Running 실행은 Abandoned 로 정리
3. 사전 검증 — 헤더, 손상 행 1% 미만, 직전 대비 크기 ±30%, 행 수 -5% 이내. 실패하면 **아무것도 쓰지 않음**
4. 바뀐 행만 반영 (500행 배치 = 한 트랜잭션, 서비스 무중단)
5. 누락 처리 — 빠진 화장실은 Missing(지도에 계속 표시), 3회 연속 빠지면 Removed.
   한 번에 1% 넘게 지워질 상황이면 보류하고 알림. 원본이 없는 예전 임포트 행은 Missing 까지만 (자동 삭제 안 함)
6. 결과 알림 — 로그 + `Alerts:WebhookUrl`(Slack/Discord) 이 있으면 웹훅. 마지막 정상 갱신이 48시간을 넘으면 경고

설정은 `src/Mashil.Worker/appsettings.json` 의 `Import:*`, `Alerts:*`, `Geocode:*`.

### 정규화·지오코딩 도구 (CsvImport CLI)

```bash
dotnet run --project tools/Mashil.CsvImport -- "<CSV>" --hours-report      # 개방시간 파서 커버리지 (DB 없음)
dotnet run --project tools/Mashil.CsvImport -- --renormalize                # 저장된 원본으로 재정규화 (--only-outdated)
dotnet run --project tools/Mashil.CsvImport -- --adopt-coordinates          # 기존 좌표를 오버라이드로 (1회)
dotnet run --project tools/Mashil.CsvImport -- --geocode=200                # 지오코딩 N건 (user-secrets 의 Kakao 키)
```

## 컨테이너 / 운영 배포

설계는 `Mashil-app/docs/08-infrastructure.md`. **서버 준비·배포·롤백·백업·감시·점검 모드 절차는 [docs/OPERATIONS.md](docs/OPERATIONS.md).** 파일:

| 파일 | 내용 |
|---|---|
| `Dockerfile` | 타깃 `api` / `worker` / `migrate`(EF 마이그레이션 번들). `--build-arg GIT_SHA` → Sentry 릴리스 |
| `.github/workflows/ci.yml` | 빌드 + 단위 + 통합(PostGIS 서비스 컨테이너) 테스트, `main` 이면 GHCR 에 이미지 3개 (`:<sha>`, `:latest`) |
| `deploy/docker-compose.prod.yml` | Caddy(80·443) → api-blue·api-green, worker, postgres(내부 전용, 호스트 포트 없음), `ops` 프로필: migrate |
| `deploy/Caddyfile` | 자동 TLS·HSTS·보안 헤더, `/api` 헬스체크 업스트림, 웹 SPA 정적(에셋 immutable, index no-cache), 점검 정적 폴백, 접근 로그 위치 쿼리 제거 (NFR-06) |
| `deploy/deploy.sh` · `rollback.sh` | 무중단 배포(마이그레이션 → 한쪽씩 교체 → 스모크 → `.deployed` 기록) · 기록된 이전 이미지로 롤백 (OPS-06) |
| `deploy/web-deploy.sh` · `web-cleanup.sh` | 웹 배포(이전 해시 에셋 7일 보존, OPS-04) · 오래된 에셋 정리 |
| `deploy/backup.sh` · `disk-check.sh` · `cron/mashil` | pg_dump 일간 7 + 주간 4 · 디스크 경고 · 호스트 cron |
| `deploy/.env.example` | 운영 환경 변수 템플릿 (`/etc/mashil/mashil.env`, chmod 600) |

```bash
docker build --target api -t mashil-api .
docker build --target worker -t mashil-worker .
docker build --target migrate -t mashil-migrate .
docker run --rm -e MASHIL_DB="Host=...;Database=mashil;Username=...;Password=..." mashil-migrate   # 마이그레이션
```

API 는 `Cors:Origins`(쉼표 구분)로 허용 도메인을 받는다. 기본은 `http://localhost:5173`.

운영 관련 설정 (기본값은 `src/Mashil.Api/appsettings.json`):
- `RateLimiting:*` — IP 당 빈도 제한 (Global·Write·Auth, 429 + `Retry-After`). **개발 환경은 꺼져 있다** (`appsettings.Development.json`) — k6 를 Development 가 아닌 환경에서 돌리면 `RateLimiting__Enabled=false`.
- `ForwardedHeaders:KnownProxies` / `KnownNetworks` — X-Forwarded-For 를 믿을 프록시 (운영 Compose 가 Caddy 대역을 넣음).
- `Sentry:Dsn` — 있으면 API·Worker 에러를 Sentry 로 (개인정보·위치 쿼리 제외). 로그는 운영에서 콘솔 JSON, 개발에서 텍스트.

CSV 는 [LOCALDATA 공중화장실정보](https://file.localdata.go.kr/file/public_restroom_info/info) 의 "전국파일" 입니다.
응답 헤더는 UTF-8 이라고 선언하지만 실제 파일은 CP949 — 임포터가 내용으로 판별합니다.
이 파일에는 **좌표가 없습니다.** 지도 표시는 Geocode 워커가 주소로 좌표를 채운 뒤부터 됩니다.

임포트는 원본 행을 `raw_toilet_records` 에 그대로 보존하고, 바뀐 행만 `toilets` 에 반영합니다.
관리자 수정·지오코딩 결과는 `toilet_overrides` 에 두고 재임포트 때 다시 적용하므로 덮어써지지 않습니다.

## 시크릿 설정

`appsettings.json` 에는 **민감한 값 절대 적지 말 것**. 대신 다음 셋 중 하나:

### 1. User Secrets (dev 권장)

```bash
# Kakao Local API (REST 키 — JS 키와 다름)
dotnet user-secrets --project src/Mashil.Api set "Kakao:RestApiKey" "<REST API 키>"

# JWT 서명 키 (32자 이상 랜덤)
dotnet user-secrets --project src/Mashil.Api set "Jwt:SigningKey" "<32자 이상 랜덤 문자열>"

# DB 패스워드 override (docker-compose 기본값과 다른 환경)
dotnet user-secrets --project src/Mashil.Api set "ConnectionStrings:Postgres" "Host=...;Database=...;Username=...;Password=..."
```

User Secrets 는 `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json` (Windows) 또는
`~/.microsoft/usersecrets/<id>/secrets.json` (mac/linux) 에 저장. 저장소 외부.

설정값 확인:

```bash
dotnet user-secrets --project src/Mashil.Api list
```

### 2. 환경 변수 (CI/Docker)

`__` 가 ASP.NET Configuration 의 `:` 대체:

```bash
export ConnectionStrings__Postgres="Host=...;..."
export Kakao__RestApiKey="..."
export Jwt__SigningKey="..."
```

### 3. `appsettings.Local.json` (gitignored)

급할 때 `src/Mashil.Api/appsettings.Local.json` 만들어도 됨 (`.gitignore` 처리됨).
단, 팀 환경에서는 User Secrets 가 표준.

> ⚠️ **절대 금지**: `appsettings.json` / `appsettings.Development.json` 에 실제 키 넣고 commit.
> PR 올리기 전 `git diff` 로 확인. 만약 실수로 commit·push 했다면 키를 즉시 폐기/재발급하고 git history 에서 제거 (`git filter-repo` 또는 BFG).

## 환경 의존 — 절대 경로 / 호스트별 설정 정책

YhLibrary 위치 외에 환경 의존 값:

| 항목 | 어디서 설정 | 기본값 |
|---|---|---|
| YhLibrary 소스 위치 | `YHLIBRARY_ROOT` env / `Directory.Build.props.user` | `..\MyLibrary` (sibling) |
| Postgres 연결 | `ConnectionStrings:Postgres` (User Secrets / env) | docker-compose dev DB |
| Kakao REST API 키 | `Kakao:RestApiKey` (User Secrets / env) | 빈 값 → Geocode 워커 idle |
| JWT 서명 키 | `Jwt:SigningKey` (User Secrets / env) | 빈 값 → 앱 시작 실패 (의도) |
| CSV 파일 경로 | `Mashil.CsvImport` 의 첫 번째 CLI arg | 없음 (필수) |
| 마이그레이션 대상 DB | `MASHIL_DB` env (`dotnet ef` 디자인 타임) | docker-compose dev DB |

소스에 `D:\...` / `C:\...` 같은 absolute 경로 박지 마세요. 새로 추가할 때도 위 패턴(env/User Secrets/CLI arg) 따르세요.

## 좌표/주소 데이터 정책

원본 CSV 행은 4가지 상태:

| 좌표 | 주소 | 처리 |
|:-:|:-:|---|
| ✓ | ✓ | 지도 + 리스트 노출. 정상 |
| ✓ | ✗ | 지도 노출. 주소는 reverse-geocode 큐 |
| ✗ | ✓ | 리스트/검색만. 좌표는 geocode 큐 |
| ✗ | ✗ | 검색만 (보존) |

Import 시 외부 API 호출하지 않음 — `GeocodeWorker` 가 백그라운드에서 큐 소비.

## 사용한 오픈소스

버전은 `Directory.Packages.props` 기준 (2026-11-16). 라이선스는 각 패키지가 밝힌 것.

### 런타임 (API · Worker · 임포터)

| 패키지 | 버전 | 라이선스 | 쓰는 곳 |
|---|---|---|---|
| [.NET / ASP.NET Core](https://github.com/dotnet/aspnetcore) | 10.0 | MIT | 런타임, 웹 API |
| [Entity Framework Core](https://github.com/dotnet/efcore) (`Microsoft.EntityFrameworkCore`, `.Relational`, `.Design`) | 10.0.15 | MIT | ORM, 마이그레이션 |
| [Npgsql EF Core provider](https://github.com/npgsql/efcore.pg) (+ `.NetTopologySuite`) | 10.0.3 | PostgreSQL | PostgreSQL·PostGIS 연결 |
| [NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite) | 2.5.0 | BSD-3-Clause | 좌표·거리 계산 (근처 검색) |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.0 | MIT | 마실 JWT 인증 |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect`, `.JsonWebTokens` | 8.0.1 | MIT | Kakao·Google·Apple ID 토큰 검증 |
| `Microsoft.Extensions.*` (Hosting, Http, Configuration, Options) | 9.0.0 | MIT | Worker 호스트, HttpClient, 설정 |
| [CsvHelper](https://github.com/JoshClose/CsvHelper) | 33.0.1 | MS-PL 또는 Apache-2.0 | LOCALDATA CSV 읽기 |
| [Serilog](https://github.com/serilog/serilog) (`AspNetCore` 9.0.0, `Extensions.Hosting` 10.0.0, `Settings.Configuration` 10.0.1, `Sinks.Console` 6.1.1, `Formatting.Compact` 3.0.0) | 표 참고 | Apache-2.0 | 구조화 로그 |
| [Sentry .NET](https://github.com/getsentry/sentry-dotnet) (`Sentry.AspNetCore`, `Sentry.Serilog`) | 7.0.0 | MIT | 오류 모니터링 (DSN 이 있을 때만) |
| [Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) | 7.2.0 | MIT | Swagger UI (개발 환경) |

### 테스트

| 패키지 | 버전 | 라이선스 | 쓰는 곳 |
|---|---|---|---|
| [xUnit](https://github.com/xunit/xunit) (+ `xunit.runner.visualstudio` 3.1.4) | 2.9.3 | Apache-2.0 | 단위·통합 테스트 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | 테스트 실행 |
| `Microsoft.AspNetCore.TestHost` | 10.0.0 | MIT | 요청 제한 테스트 (메모리 안 서버) |
| [Newman](https://github.com/postmanlabs/newman) (`npx newman@6`, 설치하지 않음) | 6 | Apache-2.0 | Postman 컬렉션 회귀 테스트 |
| [k6](https://github.com/grafana/k6) (따로 설치) | 2.4 | AGPL-3.0 | 스트레스 테스트 (`tests/load`). 도구로만 쓰고 배포물에 넣지 않음 |

### 인프라 (컨테이너 이미지)

| 이미지 | 라이선스 | 쓰는 곳 |
|---|---|---|
| [`postgis/postgis:16-3.4`](https://github.com/postgis/docker-postgis) | PostgreSQL (PostgreSQL), GPL-2.0 (PostGIS) | DB. 수정 없이 실행만 함 |
| [`caddy:2.8`](https://github.com/caddyserver/caddy) | Apache-2.0 | HTTPS, 리버스 프록시, 웹 정적 파일 |
| `mcr.microsoft.com/dotnet/{sdk,aspnet,runtime,runtime-deps}:10.0` | MIT (.NET) | 빌드·실행 이미지 |
| GitHub Actions: `actions/checkout`·`setup-dotnet` (MIT), `docker/login-action`·`setup-buildx-action`·`build-push-action` (Apache-2.0) | 표 참고 | CI (`.github/workflows/ci.yml`) |

## 데이터 · 외부 서비스

오픈소스가 아니라 각 제공자의 이용 조건을 따른다.

| 항목 | 쓰는 곳 | 조건 |
|---|---|---|
| [LOCALDATA](https://www.localdata.go.kr/) 공중화장실정보 전국파일 | 화장실 원본 데이터 (매일 갱신) | 공공데이터. 출처 표시 (화면 상세의 "출처: 공공데이터 LOCALDATA") |
| [Kakao Local API](https://developers.kakao.com/docs/latest/ko/local/dev-guide) | 주소 → 좌표 지오코딩 | Kakao Developers 약관, 일일 호출 한도 |
| Kakao 로그인, Google 로그인, Sign in with Apple | 소셜 로그인 토큰 검증, 탈퇴 시 연결 해제 | 각 제공자 약관 |

## 라이선스

코드는 사이드 프로젝트 (라이선스 미정).
