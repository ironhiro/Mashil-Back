# 마실 운영 런북

설계는 `Mashil-app/docs/05-operations.md`(무중단 배포·점검 모드), `08-infrastructure.md`(구성·백업·감시). 이 문서는 그 설계를 이 저장소의 파일로 실제로 돌리는 절차다. 아직 운영 서버는 없다 (호스팅은 미결 Q11).

| 구성 | 파일 |
|---|---|
| VM 1대 + Docker Compose: Caddy(80·443) → api-blue·api-green, worker, postgres(내부 전용) | `deploy/docker-compose.prod.yml` |
| 한 도메인: `/api/*`·`/healthz*` 는 API, 나머지는 웹 SPA 정적 파일. 보안 헤더·HSTS, 접근 로그 위치 쿼리 제거 | `deploy/Caddyfile` |
| 배포·롤백 | `deploy/deploy.sh`, `deploy/rollback.sh` |
| 웹 배포·이전 에셋 정리 | `deploy/web-deploy.sh`, `deploy/web-cleanup.sh` |
| 백업·디스크 경고·cron | `deploy/backup.sh`, `deploy/disk-check.sh`, `deploy/cron/mashil` |
| 환경 변수 템플릿 | `deploy/.env.example` → 서버의 `/etc/mashil/mashil.env` |
| CI (테스트 → GHCR 이미지) | `.github/workflows/ci.yml` |

> 웹을 CDN·오브젝트 스토리지 대신 Caddy 가 서빙하는 이유: 웹이 `/api` 를 상대 경로로 불러서 같은 도메인이어야 하고(CORS 불필요), 출시 단계 트래픽에는 VM 하나로 충분하다. CDN 을 붙일 때는 이 도메인 전체를 origin 으로 두면 된다 (캐시 헤더는 이미 맞춰 둠).

---

## 1. 처음 서버 준비

1. **VM** — 국내(서울) 리전 (NFR-16: LOCALDATA 접근성, 지연 시간). 2vCPU/4GB, Ubuntu 24.04, 디스크 40GB 이상.
2. **방화벽** — 22/tcp 는 관리자 IP 만, 80/tcp·443/tcp(+443/udp) 공개. 그 외 모두 닫음. Postgres 는 호스트 포트가 없다.
3. **기본 설정**
   ```bash
   sudo timedatectl set-timezone Asia/Seoul          # cron 시각 기준 (Ubuntu cron 은 CRON_TZ 미지원)
   # Docker Engine + compose plugin 설치 (docs.docker.com/engine/install/ubuntu)
   sudo git clone <백엔드 저장소> /opt/mashil
   sudo mkdir -p /etc/mashil /srv/mashil/web/site /srv/mashil/status /var/backups/mashil /var/log/mashil
   sudo install -m 600 /opt/mashil/deploy/.env.example /etc/mashil/mashil.env   # 값 채우기 (§2)
   sudo cp /opt/mashil/deploy/status.example.json /srv/mashil/status/status.json
   ```
4. **DNS** — `MASHIL_DOMAIN` 의 A(AAAA) 레코드를 VM 공인 IP 로. Caddy 가 첫 요청 때 Let's Encrypt 인증서를 받는다 (80·443 이 열려 있어야 함).
5. **GHCR 접근** — CI 가 `ghcr.io/<owner>/mashil-{api,worker,migrate}` 에 올린다. 패키지는 기본 비공개이므로 둘 중 하나:
   - 서버에서 `docker login ghcr.io -u <GitHub 사용자>` (비밀번호 = `read:packages` 권한만 있는 PAT), 또는
   - GitHub → Packages → 각 패키지 → Visibility 를 Public 으로.
6. **첫 배포** — `sudo /opt/mashil/deploy/deploy.sh <git sha>` (postgres·Caddy 도 이때 뜬다). 이어서 웹 배포(§4).
7. **LOCALDATA 다운로드 사전 검증** (NFR-16, 08 §8) — 운영 IP 에서 실제로 받히는지:
   `docker compose -f deploy/docker-compose.prod.yml --env-file /etc/mashil/mashil.env run --rm -e Import__Schedule__RunOnStartup=true worker` 로 띄워 로그에서 다운로드·임포트 결과를 보고 Ctrl+C (advisory lock 이 상주 worker 와의 동시 실행을 막는다).
8. **cron** — `sudo cp /opt/mashil/deploy/cron/mashil /etc/cron.d/mashil` (백업 03:00, 웹 정리 03:30, 디스크 경고 매시).
9. **감시 등록** (§7) — 업타임 감시, Sentry DSN, 웹훅.

## 2. 환경 변수 (`/etc/mashil/mashil.env`, chmod 600)

전체 목록과 설명은 `deploy/.env.example`. 저장소에는 실제 값을 넣지 않는다 (`.env`, `*.local.env` 는 gitignore).

| 변수 | 용도 |
|---|---|
| `MASHIL_DOMAIN` | 서비스 도메인 (Caddy TLS, 스모크 테스트) |
| `POSTGRES_PASSWORD` | DB 비밀번호 (Compose 가 연결 문자열을 만든다) |
| `MASHIL_REGISTRY` | `ghcr.io/<owner>` (소문자). `deploy.sh <tag>` 가 이미지 이름을 만든다 |
| `MASHIL_IMAGE_API` / `_WORKER` / `_MIGRATE` | 태그 없이 배포할 때 쓰는 이미지 (보통 `:latest`) |
| `MASHIL_WEB_ROOT`, `MASHIL_STATUS_DIR` | 웹 정적 파일(`<root>/site`), 점검 정적 폴백(`status.json`) 위치 |
| `MASHIL_DOCKER_SUBNET` | 내부 네트워크 대역 (기본 `172.30.0.0/24`). API 는 이 대역(Caddy)에서 온 X-Forwarded-For 만 믿는다 |
| `MASHIL_BACKUP_DIR`, `_DAILY_DAYS`, `_WEEKLY_DAYS`, `_REMOTE` | 백업 위치·보관 기간·rclone 원격 (선택) |
| `MASHIL_DISK_ALERT_PERCENT` | 디스크 경고 기준 (기본 80) |
| `Sentry__Dsn`, `Sentry__Environment` | API·Worker 에러 모니터링. 비우면 꺼짐 |
| `Alerts__WebhookUrl` | Slack/Discord 웹훅. Worker 갱신 알림 + 배포·백업 실패·디스크 경고 |
| `RateLimiting__*` | 빈도 제한 조정 (§8) |
| `Jwt__SigningKey`, `OAuth__*`, `Kakao__RestApiKey`, `Cors__Origins` … | 앱 설정 (README "시크릿 설정", `docs/AUTH.md`) |

값을 바꾼 뒤에는 컨테이너를 다시 만들어야 반영된다 — 지금 버전으로 다시 배포: `MASHIL_SKIP_MIGRATE=1 ./deploy/deploy.sh <지금 태그>` (지금 태그는 `./deploy/rollback.sh --list` 의 마지막 줄).

## 3. API 배포

1. `main` 에 머지 → CI 가 테스트(단위 + PostGIS 통합) 후 이미지 3개를 `:<git sha>` 와 `:latest` 로 올린다.
2. 서버에서:
   ```bash
   cd /opt/mashil && sudo git pull          # 배포 스크립트·Compose·Caddyfile 갱신
   sudo ./deploy/deploy.sh <git sha 40자>
   ```
   순서: 이미지 받기 → 마이그레이션(expand 만, OPS-03) → api-blue → api-green 한쪽씩 (`/healthz/ready` 통과 대기, 그동안 Caddy 는 다른 쪽으로) → worker → 스모크(`/healthz/ready`, near, `/api/app/status`) → `deploy/.deployed` 에 기록(digest 고정).
   실패하면 웹훅으로 알리고 멈춘다. 이미 교체된 쪽이 있으면 §5 롤백.
3. Caddyfile 만 바꿨다면: `docker compose -f deploy/docker-compose.prod.yml --env-file /etc/mashil/mashil.env exec caddy caddy reload --config /etc/caddy/Caddyfile`

## 4. 웹 배포 (OPS-04)

CI(`Mashil-web` 저장소)가 `main` push 마다 글꼴을 만들어 `web-dist-<sha>` 아티팩트를 남긴다 (Kakao JS 키·Sentry DSN 은 저장소 Variables `VITE_KAKAO_MAP_KEY`, `VITE_SENTRY_DSN`). 아티팩트를 받아 서버로 올린 뒤:

```bash
scp -r dist/ server:/tmp/mashil-web-<sha>/
sudo /opt/mashil/deploy/web-deploy.sh /tmp/mashil-web-<sha> <sha>
```

- 새 파일을 덮어쓰기만 하고 지우지 않는다. `index.html` 은 마지막에 `mv` 로 원자적 교체.
- 이전 빌드의 해시 에셋은 **교체된 시각부터 7일** 남는다 (오래 열린 웹뷰가 지연 로딩하는 청크가 404 나지 않게). `web-cleanup.sh`(cron)가 지금 빌드에 없고 7일 지난 파일만 지운다. 미리 보기: `DRY_RUN=1 ./deploy/web-cleanup.sh`.
- 캐시: `/assets/*` 는 `immutable` 1년, 나머지(`index.html`, 글꼴 등 public 파일)는 `no-cache`(ETag 재검증). 없는 `/assets/*` 는 404, 그 외 경로는 SPA 폴백으로 `index.html`.

## 5. 롤백 (OPS-06, 목표 5분)

```bash
sudo ./deploy/rollback.sh --list     # 배포 기록 (번호, 시각, 태그, api 이미지, 메모)
sudo ./deploy/rollback.sh            # 지금과 다른 API 이미지였던 가장 최근 배포로
sudo ./deploy/rollback.sh 12         # 기록의 12번 줄로
sudo ./deploy/web-deploy.sh --rollback   # 웹: index.html 만 이전 빌드로 (에셋은 보존 중)
```

- 롤백은 **마이그레이션을 실행하지 않는다**. 스키마는 expand 만 하므로 이전 코드가 새 스키마에서 돈다 (05 §3.3). 이전 번들을 돌리면 새 마이그레이션을 되돌리려 해서 위험하다.
- 교체는 배포와 같은 블루/그린 순서라 무중단이고, 기록은 digest 로 고정돼 있어 `:latest` 가 바뀌어도 정확히 그 이미지로 돌아간다. 롤백도 기록에 한 줄 남는다 (`rollback-to:...`).
- 서버의 이전 이미지를 `docker image prune -a` 로 지우면 GHCR 에서 다시 받아야 하므로, 정리는 최근 몇 개를 남기고 한다.

## 6. 백업과 복구

`deploy/backup.sh` (cron 03:00): postgres 컨테이너에서 `pg_dump -Fc` → `pg_restore --list` 로 읽히는지 확인 → `$MASHIL_BACKUP_DIR/daily/mashil-YYYYmmdd-HHMM.dump`. 일요일 것은 `weekly/` 에도. 보관 일간 7일 · 주간 28일 (08 §1). `MASHIL_BACKUP_REMOTE` 를 주면 rclone 으로 서버 밖(국내 오브젝트 스토리지 권장)에도 같은 보관 기간으로 복사한다. 실패하면 웹훅.

RPO 24시간 / RTO 2시간 (08 §5). 공공데이터는 LOCALDATA 에서 다시 받을 수 있지만 **사용자 데이터·관리자 작업·지오코딩 캐시는 백업이 유일한 사본**이다.

복구 (분기마다 별도 DB 에 리허설, 08 §5):

```bash
C="docker compose -f deploy/docker-compose.prod.yml --env-file /etc/mashil/mashil.env"
# 리허설: 별도 DB 에 복원해 행 수 확인
$C exec -T postgres createdb -U mashil mashil_restore_test
$C exec -T postgres pg_restore -U mashil -d mashil_restore_test --no-owner < /var/backups/mashil/daily/mashil-<stamp>.dump
$C exec -T postgres psql -U mashil -d mashil_restore_test -c "select count(*) from toilets; select count(*) from user_profiles;"
$C exec -T postgres dropdb -U mashil mashil_restore_test

# 실제 복구 (점검 모드 §9 → API·Worker 정지 → 덮어쓰기 → 재시작)
$C stop api-blue api-green worker
$C exec -T postgres pg_restore -U mashil -d mashil --clean --if-exists --no-owner < /var/backups/mashil/daily/mashil-<stamp>.dump
$C up -d api-blue api-green worker
```

## 7. 감시와 알림 (NFR-08, NFR-10)

| 대상 | 방법 | 할 일 |
|---|---|---|
| 가용성 | 외부 업타임 감시가 `https://<도메인>/healthz/ready` 를 1분마다 (DB 까지 확인). 연속 3회 실패 시 알림 | UptimeRobot·Better Stack 같은 무료 플랜에 HTTP 모니터 1개 + 알림 채널(웹훅·메일). 국내 체크 위치가 있으면 그쪽 |
| 인증서 | 같은 감시에서 TLS 만료 알림 켜기 (Caddy 가 자동 갱신하지만 실패 감지용) | |
| 에러 | Sentry — API·Worker 는 `Sentry__Dsn`, 웹은 빌드 변수 `VITE_SENTRY_DSN`. Error 이상과 처리 안 된 예외. 개인정보 없음: SendDefaultPii=false(웹은 dataCollection), 요청 본문·쿠키 없음, URL·쿼리·브레드크럼의 `lat/lng/north/south/east/west` 는 `[Filtered]` | Sentry 프로젝트(백엔드 1, 웹 1) 만들고 DSN 넣기. 신규 이슈 알림 켜기 |
| 데이터 갱신 | Worker 가 실패·검증 거부·대량 삭제 보류·48시간 신선도를 `Alerts__WebhookUrl` 로 (구현됨) | 웹훅 URL 넣기 |
| 배포·백업 | `deploy.sh`·`backup.sh` 실패 시 같은 웹훅 | |
| 디스크 | `disk-check.sh` 매시, Docker 데이터·백업 디렉터리 80% 이상이면 웹훅 | |
| 로그 | 콘솔 JSON 한 줄씩 (Serilog CLEF: `@t`·`@m`·`@l`·속성). 컨테이너 로그는 20MB×5 회전 | `$C logs -f --since 10m api-blue | jq -r '."@m"'` |

위치 정보는 로그에도 남지 않는다: Caddy 접근 로그는 위치 쿼리를 지우고 IP 를 /24(/48)로 가린다. API 요청 로그는 경로만 남긴다 (쿼리 없음).

## 8. 빈도 제한 (API)

클라이언트 IP 당 고정 창. 넘으면 `429` + `Retry-After`(초) + `{"error":"rate_limited"}`.

| 등급 | 대상 | 기본값 |
|---|---|---|
| Global | `/api/*` 전체 | 분당 1200 |
| Write | `/api/*` 의 GET·HEAD·OPTIONS 외 | 분당 60 (Global 과 함께 적용) |
| Auth | `/api/auth/*` | 분당 30 (Global 과 함께 적용) |

- `/healthz*` 는 세지 않는다 (Caddy 헬스체크). 모바일 통신사 NAT 로 여러 사용자가 IP 하나를 쓸 수 있어 넉넉하게 잡았다. IPv6 는 /64 단위로 센다.
- 클라이언트 IP 는 `UseForwardedHeaders` 가 X-Forwarded-For 에서 읽는데, `ForwardedHeaders:KnownNetworks`(운영 Compose: `MASHIL_DOCKER_SUBNET`)에서 온 요청만 믿는다. Caddy 는 들어온 X-Forwarded-For 를 버리고 실제 접속 IP 를 넣으므로 클라이언트가 헤더를 꾸며 제한을 피할 수 없다. 앞단에 LB·CDN 을 두면 Caddyfile 의 `trusted_proxies` 와 이 값을 함께 바꾼다.
- 조정: `RateLimiting__Auth__PermitLimit=60`, `RateLimiting__Global__WindowSeconds=60` 처럼 env 파일에서. 끄기: `RateLimiting__Enabled=false`.
- **개발·부하 테스트**: `appsettings.Development.json` 은 `RateLimiting:Enabled=false` 라 로컬 k6(`tests/load/mashil-load.js`, 한 IP 에서 수백 req/s)가 그대로 돈다. Development 가 아닌 환경(예: Release 빌드를 Production 으로 띄움)에서 k6 를 돌릴 때는 `RateLimiting__Enabled=false` 를 주고 띄운다.

## 9. 점검 모드 (OPS-05)

| 상황 | 방법 |
|---|---|
| 읽기 전용 점검 (쓰기만 쉼) · 전체 점검 — API 는 살아 있음 | 관리자가 `PUT /api/admin/app-status` (관리자 화면, ADM-06). DB 에 저장되고 모든 인스턴스가 몇 초 안에 따라온다. 점검 중 일반 사용자 쓰기는 503 `{"error":"maintenance"}` |
| 비상 스위치 (관리자 로그인이 안 될 때) | env 파일에 `App__Status__Maintenance=true` 또는 `App__Status__ReadOnly=true` (+ `App__Status__Message`, `App__Status__Until=2026-12-01T06:00:00+09:00`) → 지금 태그로 재배포(§2 끝) |
| API·DB 를 내려야 하는 점검 (PG 메이저 업그레이드 등) | `/srv/mashil/status/status.json` 의 `"maintenance": true`, 문구·`until` 을 먼저 고친다 → API 정지. 두 API 가 모두 내려가 있으면 Caddy 가 `/api/app/status` 를 이 파일로 답한다 (`handle_errors`). 끝나면 다시 `false` |

앱·웹은 시작할 때와 API 오류가 이어질 때 `/api/app/status` 를 보고 점검 화면을 띄운다 (05 §4). 점검 공지는 새벽 시간대에.

## 10. 보안 점검표

- [ ] SSH 키 인증만, 22 는 관리자 IP 만. 80·443 외 포트 닫힘 (`ss -tlnp` 로 5432 가 호스트에 없는지)
- [ ] `/etc/mashil/mashil.env` 권한 600, root 소유. 저장소·CI 로그에 시크릿 없음
- [ ] HTTPS 만 (Caddy 가 80 → 443 리다이렉트), HSTS 1년, `nosniff`, `Referrer-Policy`, `X-Frame-Options`, `Permissions-Policy`
- [ ] `Cors__Origins` 는 운영 웹 도메인만, `Auth__LegacyPasswordLogin=false`
- [ ] Sentry 프로젝트의 "Data Scrubber" 켜짐, IP 저장 끔 (Settings → Security & Privacy)
- [ ] 백업 복구 리허설 기록 (분기 1회)
