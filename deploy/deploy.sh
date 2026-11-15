#!/usr/bin/env bash
# 무중단 배포 (05-operations §3.2, 08-infrastructure §4)
#   1) 새 이미지 받기  2) 마이그레이션(expand 만)  3) api-blue → api-green 하나씩 교체  4) worker 교체  5) 스모크 테스트
#   그다음 배포 기록 (deploy/.deployed — rollback.sh 가 읽는다)
#
# 사용:
#   ./deploy/deploy.sh <tag>     CI 가 GHCR 에 올린 태그 (git sha). 이미지 = ${MASHIL_REGISTRY}/mashil-{api,worker,migrate}:<tag>
#   ./deploy/deploy.sh           태그 없이: env 파일의 MASHIL_IMAGE_API / _WORKER / _MIGRATE 그대로 (보통 :latest)
#
# 환경 변수 (선택):
#   MASHIL_ENV_FILE        기본 /etc/mashil/mashil.env
#   MASHIL_SKIP_MIGRATE=1  마이그레이션 단계를 건너뜀 (rollback.sh — 이전 번들이 새 마이그레이션을 되돌리지 않게)
#   MASHIL_DEPLOY_NOTE     기록에 남길 메모 (기본 deploy)
set -euo pipefail

source "$(dirname "$0")/lib.sh"
cd "$DEPLOY_DIR"

HISTORY="${MASHIL_DEPLOY_HISTORY:-$DEPLOY_DIR/.deployed}"
DOMAIN="$(env_get MASHIL_DOMAIN)"
TAG="${1:-}"

if [[ -n "$TAG" ]]; then
  REGISTRY="$(env_get MASHIL_REGISTRY)"
  [[ -n "$REGISTRY" ]] || { echo "MASHIL_REGISTRY 가 없음 (예: ghcr.io/<owner>)" >&2; exit 1; }
  export MASHIL_IMAGE_API="$REGISTRY/mashil-api:$TAG"
  export MASHIL_IMAGE_WORKER="$REGISTRY/mashil-worker:$TAG"
  export MASHIL_IMAGE_MIGRATE="$REGISTRY/mashil-migrate:$TAG"
fi
API_REF="$(env_get MASHIL_IMAGE_API mashil-api:latest)"
WORKER_REF="$(env_get MASHIL_IMAGE_WORKER mashil-worker:latest)"
MIGRATE_REF="$(env_get MASHIL_IMAGE_MIGRATE mashil-migrate:latest)"
# 셸에서 export 한 값이 --env-file 보다 우선한다 (docker compose 규칙)
export MASHIL_IMAGE_API="$API_REF" MASHIL_IMAGE_WORKER="$WORKER_REF" MASHIL_IMAGE_MIGRATE="$MIGRATE_REF"
SKIP_MIGRATE="${MASHIL_SKIP_MIGRATE:-0}"
STEP="시작"

trap 'notify 오류 "배포 실패 (api=${API_REF})" "단계: ${STEP}. 되돌리려면 deploy/rollback.sh"' ERR

require_image() {
  docker image inspect "$1" >/dev/null 2>&1 || { echo "이미지가 없음: $1" >&2; return 1; }
}

STEP="1/5 이미지 받기"; echo "[$STEP] api=$API_REF"
# 직접 빌드한 로컬 이미지도 쓸 수 있게 pull 실패는 넘기고, 대신 이미지가 있는지 확인한다
"${COMPOSE[@]}" pull --ignore-pull-failures api-blue worker
require_image "$API_REF"; require_image "$WORKER_REF"
if [[ "$SKIP_MIGRATE" != 1 ]]; then
  "${COMPOSE[@]}" --profile ops pull --ignore-pull-failures migrate
  require_image "$MIGRATE_REF"
fi

# 처음 배포이거나 DB 가 꺼져 있으면 켠다 (이미 돌고 있으면 그대로 둔다)
"${COMPOSE[@]}" up -d --wait --no-recreate postgres

if [[ "$SKIP_MIGRATE" == 1 ]]; then
  STEP="2/5 마이그레이션 건너뜀"; echo "[$STEP] (MASHIL_SKIP_MIGRATE=1)"
else
  STEP="2/5 마이그레이션"; echo "[$STEP] (앱 시작 시 자동 실행하지 않음, OPS-03)"
  "${COMPOSE[@]}" --profile ops run --rm migrate
fi

wait_ready() {
  local svc="$1"
  for _ in $(seq 1 60); do
    # 이미지에 curl 이 없어서 bash 의 /dev/tcp 로 직접 요청한다 (sh(dash) 는 /dev/tcp 를 모름)
    if "${COMPOSE[@]}" exec -T "$svc" bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n 1 <&3 | grep -q " 200 "' 2>/dev/null; then
      echo "  $svc ready"; return 0
    fi
    sleep 2
  done
  echo "  $svc 가 준비되지 않음" >&2; return 1
}

STEP="3/5 API 교체"; echo "[$STEP] (한쪽씩 — 교체 중에는 Caddy 가 다른 쪽으로 보냄)"
for svc in api-blue api-green; do
  "${COMPOSE[@]}" up -d --no-deps --force-recreate "$svc"
  wait_ready "$svc"
done

STEP="4/5 Worker 교체"; echo "[$STEP] (임포트 중이었다면 다음 실행이 Abandoned 로 정리)"
"${COMPOSE[@]}" up -d --no-deps --force-recreate worker

STEP="5/5 스모크 테스트"; echo "[$STEP]"
"${COMPOSE[@]}" up -d --no-recreate caddy   # 처음 배포면 Caddy 를 띄운다 (돌고 있으면 그대로)
curl -fsS --retry 10 --retry-delay 3 --retry-all-errors "https://${DOMAIN}/healthz/ready" >/dev/null
curl -fsS --retry 10 --retry-delay 3 --retry-all-errors "https://${DOMAIN}/api/toilets/near?lat=37.5665&lng=126.9780&radiusM=500&limit=1" >/dev/null
curl -fsS --retry 10 --retry-delay 3 --retry-all-errors "https://${DOMAIN}/api/app/status" >/dev/null
trap - ERR

# 기록: 태그가 :latest 여도 정확히 되돌릴 수 있게 digest 로 고정한 참조를 남긴다 (로컬 이미지는 그대로)
pinned() {
  local ref="$1" repo last d
  repo="${ref%@*}"; last="${repo##*/}"
  [[ "$last" == *:* ]] && repo="${repo%:*}"
  d="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$ref" 2>/dev/null | grep -F "${repo}@" | head -n 1 || true)"
  echo "${d:-$ref}"
}
# 열: 시각(UTC)  태그  api  worker  migrate  메모
printf '%s\t%s\t%s\t%s\t%s\t%s\n' \
  "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${TAG:--}" \
  "$(pinned "$API_REF")" "$(pinned "$WORKER_REF")" "$(pinned "$MIGRATE_REF")" \
  "${MASHIL_DEPLOY_NOTE:-deploy}" >> "$HISTORY"
echo "배포 완료 — 기록: $HISTORY"
