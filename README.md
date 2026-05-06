# Mashil-Back

> **잠깐, 다녀와.**
> 한국 공중·개방 화장실 데이터 기반 길찾기/평점/댓글 사이드 프로젝트의 백엔드.

```
ASP.NET Core 10 (Minimal API)  +  EF Core 10 + PostGIS
                ↑
                │ ProjectReference
                │
        YhLibrary.Http / YhLibrary.Serialization
```

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
│   └── Mashil.Infrastructure/     EF Core + Npgsql + NetTopologySuite
└── tools/
    └── Mashil.CsvImport/          공중화장실정보.csv → DB (EUC-KR)
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

# 3) (선택) CSV 일회성 임포트 — 본인 환경의 CSV 경로
dotnet run --project tools/Mashil.CsvImport -- "<공중화장실정보.csv 절대 경로>"

# 4) 백엔드 (https://localhost:7099/swagger)
dotnet run --project src/Mashil.Api
```

CSV 는 [공공데이터포털 — 공중화장실 표준데이터](https://www.data.go.kr/) 에서 받을 수 있습니다 (EUC-KR/CP949 인코딩).

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
| CSV 파일 경로 | `Mashil.CsvImport` 의 CLI arg / `CsvPath` config | 없음 (필수) |

소스에 `D:\...` / `C:\...` 같은 absolute 경로 박지 마세요. 새로 추가할 때도 위 패턴(env/User Secrets/CLI arg) 따르세요.

## YhLibrary 의존

- `YhLibrary.Http.ApiClient` — Kakao Local API typed client + `RetryHandler`
- `YhLibrary.Serialization.JsonSerializer` — DI 에 `ISerializer` 로 등록

## 좌표/주소 데이터 정책

원본 CSV 행은 4가지 상태:

| 좌표 | 주소 | 처리 |
|:-:|:-:|---|
| ✓ | ✓ | 지도 + 리스트 노출. 정상 |
| ✓ | ✗ | 지도 노출. 주소는 reverse-geocode 큐 |
| ✗ | ✓ | 리스트/검색만. 좌표는 geocode 큐 |
| ✗ | ✗ | 검색만 (보존) |

Import 시 외부 API 호출하지 않음 — `GeocodeWorker` 가 백그라운드에서 큐 소비.

## 라이선스 / 데이터 출처

화장실 마스터 데이터는 [공공데이터포털](https://www.data.go.kr/) 의 "공중화장실 표준데이터" 가공.
코드는 사이드 프로젝트 (라이선스 미정).
