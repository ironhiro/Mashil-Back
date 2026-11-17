#!/usr/bin/env bash
# 도보 도로망 갱신 Job 만들기 (Container Apps Job, 일정 실행). docs/AZURE.md "도보 도로망 갱신"
#
#   az login 한 뒤:  bash deploy/azure/walk-job.sh [dev|prod|all]   (기본 all)
#
# 만드는 것: mashil-walkimport-dev (매월 2일 03:00 KST), mashil-walkimport (매월 3일 03:00 KST — 개발에서 하루 먼저 돈다)
#   이미지 ghcr.io/ironhiro/mashil-osmimport (CI 가 배포마다 같은 커밋으로 바꾼다, ci-deploy.sh)
#   기본 인자 --download: Geofabrik 한국 PBF + .md5 → walk_edges_new → 건수 확인 → 교체 (tools/Mashil.OsmImport)
#   4 vCPU / 8GiB (한국 전체를 메모리에 올린다. PC 측정 최대 약 2.5GB), 최대 30분, 재시도 없음
# 비밀값: DB 연결 문자열은 ~/.mashil/azure.env 에서 만들어 Job 시크릿에만 넣는다 (화면에 찍지 않음).
# 서울 보행 네트워크(--seoul)는 아직 켜지 않는다: OSM 망과 경계에서 이어지지 않아 경계를 넘는 경로가 끊긴다.
# 다시 돌려도 이미 있는 Job 은 건너뛴다. 바로 한 번 돌리려면: az containerapp job start -g rg-mashil -n mashil-walkimport-dev
set -euo pipefail

RG="${RG:-rg-mashil}"
ENV_NAME="${ENV_NAME:-cae-mashil}"
REG="ghcr.io/ironhiro"
STAGE="${1:-all}"
# shellcheck disable=SC1090
source "${HOME}/.mashil/azure.env"
: "${PG_HOST:?~/.mashil/azure.env 에 PG_HOST 가 없다}" "${PG_ADMIN:?}" "${PG_PASSWORD:?}"

make_job() { # <이름> <DB> <cron (UTC)>
  local name="$1" db="$2" cron="$3"
  if az containerapp job show -g "$RG" -n "$name" -o none 2>/dev/null; then
    echo "== $name 있음 (건너뜀)"; return
  fi
  echo "== $name ($db, cron '$cron' UTC)"
  local cs="Host=$PG_HOST;Database=$db;Username=$PG_ADMIN;Password=$PG_PASSWORD;SSL Mode=Require;Command Timeout=1800"
  az containerapp job create -g "$RG" -n "$name" --environment "$ENV_NAME" \
    --trigger-type Schedule --cron-expression "$cron" \
    --replica-timeout 1800 --replica-retry-limit 0 --parallelism 1 --replica-completion-count 1 \
    --image "$REG/mashil-osmimport:latest" --cpu 4 --memory 8Gi \
    --secrets "db=$cs" --env-vars "ConnectionStrings__Postgres=secretref:db" -o none
}

case "$STAGE" in
  dev)  make_job mashil-walkimport-dev mashil_dev "0 18 1 * *" ;;
  prod) make_job mashil-walkimport     mashil     "0 18 2 * *" ;;
  all)  make_job mashil-walkimport-dev mashil_dev "0 18 1 * *"
        make_job mashil-walkimport     mashil     "0 18 2 * *" ;;
  *) echo "사용법: walk-job.sh [dev|prod|all]"; exit 2 ;;
esac
echo "실행 기록: az containerapp job execution list -g $RG -n mashil-walkimport-dev -o table"
