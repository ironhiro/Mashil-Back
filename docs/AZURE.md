# Azure Container Apps 배포

마실 운영·개발 서버를 Azure Container Apps(한국 중부)에 올린다. VM 구성(`deploy/`, `docs/OPERATIONS.md`)을 대신한다.

## 구성

| 리소스 | 이름 | 비고 |
|---|---|---|
| 리소스 그룹 | `rg-mashil` | koreacentral |
| PostgreSQL Flexible Server | `pg-mashil-ks-<구독 앞 6자리>` | **한국 남부** (이 구독은 한국 중부 PostgreSQL 이 막혀 있음, `SkuNotAvailable`). Burstable B1ms, 32GB, PG 16, PostGIS. DB `mashil`(운영), `mashil_dev`(개발) |
| Container Apps 환경 | `cae-mashil` | 운영·개발 같이 |
| 웹 + API | `mashil-api` / `mashil-api-dev` | 0.5 vCPU, 1GiB. 운영 최소 1, 개발 최소 0 (첫 요청이 느릴 수 있음) |
| Worker | `mashil-worker` / `mashil-worker-dev` | 0.25 vCPU, 0.5GiB, 인그레스 없음. 개발은 평소 0 |
| 마이그레이션 작업 | `mashil-migrate` / `mashil-migrate-dev` | 수동 실행 Job. CI 가 배포마다 실행 |
| 도메인 | `toilets.ironhiro.dev` / `toilets-dev.ironhiro.dev` | Cloudflare DNS only, Container Apps 관리형 인증서 |

이미지: `ghcr.io/ironhiro/mashil-{api,worker,migrate}` (공개 패키지라 레지스트리 자격 증명 없음).
`mashil-api` 이미지에는 웹(Mashil-Front main)의 빌드가 `wwwroot` 로 들어 있어, 한 컨테이너가 웹과 `/api` 를 같은 도메인에서 내보낸다.

VM 구성에서 Caddy 가 하던 일은 API 가 맡는다:
- 보안 헤더(HSTS, nosniff, X-Frame-Options 등)와 `Server` 헤더 숨김 — `Ops/WebHosting.cs`
- 캐시 정책(`/assets/*` 1년, 나머지 no-cache), SPA 경로 → `index.html`
- 실제 접속 IP: `ForwardedHeaders__TrustIngress=true` — X-Forwarded-For 의 마지막 값(인그레스가 붙인 값)만 쓴다. 클라이언트가 꾸민 앞쪽 값은 무시
- 접근 로그 위치 마스킹: API 요청 로그는 경로만 남기고 쿼리 문자열(`lat`/`lng`)은 남기지 않는다. 도보 경로 좌표는 POST 본문이라 로그에 없다

## 처음 한 번

```bash
az login
bash deploy/azure/provision.sh       # 리소스 생성 (비용 발생). 비밀값은 ~/.mashil/azure.env (권한 600)
az containerapp job start -g rg-mashil -n mashil-migrate-dev
az containerapp job start -g rg-mashil -n mashil-migrate
bash deploy/azure/domains.sh         # Cloudflare 에 넣을 CNAME·TXT 출력
#   → Cloudflare 에 레코드 추가 (Proxy status: DNS only)
bash deploy/azure/domains.sh bind    # 도메인 연결 + 관리형 인증서
```

### 도보 도로망 임포트 (PC 에서 Azure DB 로)

`provision.sh` 가 이 PC 의 공인 IP 를 DB 방화벽에 연다(`admin-pc`). 연결 문자열은 `~/.mashil/azure.env` 값으로 만든다.

```bash
source ~/.mashil/azure.env
export ConnectionStrings__Postgres="Host=$PG_HOST;Database=mashil_dev;Username=$PG_ADMIN;Password=$PG_PASSWORD;SSL Mode=Require"
export SeoulOpenData__ApiKey="$(dotnet user-secrets list --project src/Mashil.Api | sed -n 's/^SeoulOpenData:ApiKey = //p')"
dotnet run --project tools/Mashil.OsmImport -- --pbf data/osm/south-korea-latest.osm.pbf --seoul
# 운영은 Database=mashil 로 한 번 더. 끝나면 방화벽 규칙을 지워도 된다:
az postgres flexible-server firewall-rule delete -g rg-mashil -s "${PG_HOST%%.*}" -n admin-pc --yes
```

### 앱 설정 (추가로 넣을 것)

`provision.sh` 는 DB·JWT·인그레스·도보 경로만 넣는다. 로그인 제공자(`OAuth:*`, docs/AUTH.md)와 Sentry DSN 은 준비되면 넣는다.
제공자 값은 공개 식별자(앱 ID, 클라이언트 ID)라 환경 변수로, 비밀인 값은 시크릿(`secretref:`)으로:

```bash
az containerapp update -g rg-mashil -n mashil-api --set-env-vars   OAuth__Kakao__AppId=<앱 ID> OAuth__Kakao__Audiences__0=<JavaScript 키> OAuth__Google__ClientIds__0=<클라이언트 ID>
az containerapp secret set -g rg-mashil -n mashil-api --secrets "sentry-dsn=<DSN>"
az containerapp update -g rg-mashil -n mashil-api --set-env-vars "Sentry__Dsn=secretref:sentry-dsn"
```

웹과 API 가 같은 도메인이라 CORS(`Cors__Origins`)는 필요 없다. 앱(WebView)만 쓰면 비워 둔다.

### 좌표 (Worker 지오코딩) — 꼭 넣을 것

가져오기는 원본에서 좌표를 받지 않는다. Worker 가 카카오 주소 검색으로 주소 → 좌표를 채운다(`Kakao:RestApiKey`).
키가 없으면 Worker 는 지오코딩을 쉬고, 지도·근처 검색에 아무것도 나오지 않는다.

```bash
# 키는 화면에 찍지 않고 user-secrets 에서 바로 넘긴다
K="$(dotnet user-secrets list --project src/Mashil.Worker | sed -n 's/^Kakao:RestApiKey = //p' | tr -d '\r')"
az containerapp secret set -g rg-mashil -n mashil-worker --secrets "kakao-rest=$K" -o none
az containerapp update -g rg-mashil -n mashil-worker --set-env-vars "Kakao__RestApiKey=secretref:kakao-rest" -o none
```

- 카카오 콘솔에서 REST API 키에 **호출 허용 IP** 를 걸었다면 Container Apps 의 나가는 IP 대역을 넣는다.
  `az containerapp show -g rg-mashil -n mashil-worker --query properties.outboundIpAddresses` (2026-11 기준
  `20.196.224.0/19`, `20.214.112.0/21`). 대역 밖이면 Worker 로그에 `Kakao 호출 불가: HTTP 401 …` 이 남고 다음 날까지 쉰다.
- 처음 5만여 곳은 초당 약 2.5건으로 6시간쯤 걸린다. 하루 예산은 `Geocode:DailyBudget`(80,000) — 카카오 무료 한도(100,000)를
  운영·개발이 나눠 쓰므로 개발은 운영이 끝난 뒤에 돌린다. 개발 DB 에 운영의 `geocode_cache` 를 복사해 두면 API 를 거의 부르지 않는다.

#### 브이월드 (선택) — 카카오가 못 찾은 주소

카카오가 못 찾은 주소만 같은 질의로 브이월드(국토부 정부 주소 DB, Geocoder API 2.0)에 묻는다(`VWorld:ApiKey`).
키가 없으면 쓰지 않는다. 결과는 카카오와 같은 도로명·동 + 본번 검증을 거치고, 시군구가 바뀌거나 산 지번이 일반 지번으로
바뀐 결과는 버린다. 찾은 곳에는 Info 이슈 `FallbackGeocode`("브이월드로 찾음")가 남는다. 캐시 키는 `vworld:<질의>`.

```bash
# vworld.kr 오픈API 인증키. 화면에 찍지 않는다
az containerapp secret set -g rg-mashil -n mashil-worker --secrets "vworld-key=<키>" -o none
az containerapp update -g rg-mashil -n mashil-worker --set-env-vars "VWorld__ApiKey=secretref:vworld-key" -o none
```

- 무료 한도는 하루 40,000건. 호출은 카카오와 같은 `Geocode:DailyBudget` 에 센다.
- 질의 규칙을 바꾼 뒤 못 찾은 곳을 다시 돌리려면 `tools/retry_nomatch_geocode.sql`.

## 배포 자격 증명 (CI)

```bash
SUB=$(az account show --query id -o tsv)
az ad sp create-for-rbac --name gh-mashil-deploy --role contributor \
  --scopes "/subscriptions/$SUB/resourceGroups/rg-mashil" --json-auth
```

출력 JSON 을 Mashil-Back 저장소 시크릿 `AZURE_CREDENTIALS` 에 넣고(화면·채팅에 붙이지 말 것), 저장소 변수를 만든다:

| 변수 | 값 |
|---|---|
| `AZURE_DEPLOY` | `true` |
| `AZURE_RESOURCE_GROUP` | `rg-mashil` |
| `VITE_KAKAO_MAP_KEY` | 카카오 JavaScript 키 (웹에 공개되는 키) |
| `VITE_SENTRY_DSN` | (선택) 웹 Sentry DSN |

GitHub 환경 `production` 에 승인자(Required reviewers)를 걸어 두면 운영 배포 전에 승인을 받는다.

## 배포 흐름

| 계기 | 대상 |
|---|---|
| Mashil-Back main push | 개발 (`toilets-dev`) |
| `repository_dispatch` `web-updated` (웹 main 이 바뀜) | 개발 |
| 태그 `v2026.11.17` push (`tools/release.sh`) | 운영 (`toilets`) — 개발 이미지를 그대로 승격, [RELEASE.md](RELEASE.md) |

순서: 이미지 빌드(api 는 웹 포함) → 마이그레이션 Job → API 이미지 교체(새 리비전이 준비되면 트래픽 전환) → Worker → 점검(`/healthz/version` 으로 새 리비전·커밋 확인). 점검이 실패하면 직전 이미지로 자동 되돌림 (`deploy/azure/ci-deploy.sh`).
마이그레이션은 expand/contract 규칙(OPS-03)을 지킨다 — 새 스키마가 직전 API 와도 동작해야 한다.

웹만 바꿨을 때는 Mashil-Back 의 ci 를 수동 실행(target=`dev`)하거나, 웹 CI 에서 dispatch 를 보낸다
(다른 저장소로 보내려면 `repo` 권한 토큰 필요: `gh api repos/ironhiro/Mashil-Back/dispatches -f event_type=web-updated`).

### 되돌리기

```bash
az containerapp revision list -g rg-mashil -n mashil-api -o table
az containerapp update -g rg-mashil -n mashil-api --image ghcr.io/ironhiro/mashil-api:<이전 버전 태그> --set-env-vars MASHIL_RELEASE=<이전 버전 태그>
```

## 비용 (2026-11 소매가 기준 추정)

| 항목 | 월 |
|---|---|
| PostgreSQL B1ms (시간당 $0.026) | 약 $19 |
| 저장소 32GB ($0.131/GB) | 약 $4 |
| Container Apps (운영 API·Worker 상시 1개, 대부분 유휴, 무료 사용량 차감) | 약 $5~15 |
| 합계 | **약 $30~40 (4~6만 원)** |

개발 앱은 최소 0이라 쓰지 않을 때 거의 0. 백업은 7일 보관(기본 포함).
