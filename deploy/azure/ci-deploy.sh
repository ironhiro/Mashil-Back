#!/usr/bin/env bash
# CI 배포 한 번 (ci.yml 의 deploy-dev / release 가 부른다, docs/RELEASE.md).
#   ci-deploy.sh <suffix> <host> <api-image> <worker-image> <migrate-image> <release>
#     suffix: "-dev" 또는 "" (운영)
#     release: /healthz/version 이 돌려줄 이름 (운영은 v2026.11.17 같은 태그, 개발은 main-<sha7>)
# 순서: 마이그레이션 Job → API → Worker → 점검 → (있으면) 도보 도로망 Job 이미지. 점검이 실패하면 API·Worker 를 직전 이미지로 되돌린다.
# 마이그레이션은 되돌리지 않는다 — expand/contract 규칙(새 스키마는 직전 API 와도 호환, OPS-03) 덕에 직전 이미지가 그대로 돈다.
# 환경 변수: RG (리소스 그룹), EXPECT_WEB (선택: /version.json 의 web 값이 이것과 같아야 한다)
set -euo pipefail

SUFFIX="$1" HOST="$2" API_IMG="$3" WORKER_IMG="$4" MIGRATE_IMG="$5" RELEASE="$6"
: "${RG:?RG 가 필요하다}"
API="mashil-api${SUFFIX}" WORKER="mashil-worker${SUFFIX}" MIGRATE="mashil-migrate${SUFFIX}"
EXPECT_SERVER="${API_IMG##*:}"; EXPECT_SERVER="${EXPECT_SERVER%%-*}" # 이미지 태그 <sha>-<run> 의 sha (운영 태그면 아래에서 덮어씀)
EXPECT_SERVER="${EXPECT_SHA:-$EXPECT_SERVER}"

image_of() { az containerapp show -n "$1" -g "$RG" --query 'properties.template.containers[0].image' -o tsv; }
release_of() { az containerapp show -n "$1" -g "$RG" --query "properties.template.containers[0].env[?name=='MASHIL_RELEASE'].value | [0]" -o tsv; }

PREV_API=$(image_of "$API"); PREV_WORKER=$(image_of "$WORKER"); PREV_REL=$(release_of "$API" || true)
echo "대상 ${API} (${HOST})"
echo "  지금: ${PREV_API##*/} (${PREV_REL:-이름 없음})"
echo "  새로: ${API_IMG##*/} (${RELEASE})"

# 1) 마이그레이션
az containerapp job update -n "$MIGRATE" -g "$RG" --image "$MIGRATE_IMG" -o none
EXEC=$(az containerapp job start -n "$MIGRATE" -g "$RG" --query name -o tsv)
ST=""
for _ in $(seq 1 60); do
  ST=$(az containerapp job execution show -n "$MIGRATE" -g "$RG" --job-execution-name "$EXEC" --query properties.status -o tsv)
  [ "$ST" = "Succeeded" ] && break
  if [ "$ST" = "Failed" ]; then echo "::error::마이그레이션 실패 ($EXEC). API·Worker 는 건드리지 않았다"; exit 1; fi
  sleep 10
done
[ "$ST" = "Succeeded" ] || { echo "::error::마이그레이션 시간 초과 ($EXEC)"; exit 1; }

# 2) API → 3) Worker. MASHIL_RELEASE 는 /healthz/version 에 나온다
az containerapp update -n "$API" -g "$RG" --image "$API_IMG" --set-env-vars "MASHIL_RELEASE=$RELEASE" -o none
az containerapp update -n "$WORKER" -g "$RG" --image "$WORKER_IMG" --set-env-vars "MASHIL_RELEASE=$RELEASE" -o none

# 4) 점검: 새 리비전이 실제로 트래픽을 받는지까지 (같은 태그로 update 하면 리비전이 안 바뀌던 일, #28)
check() {
  curl -fsS --retry 12 --retry-delay 10 --retry-all-errors "https://${HOST}/healthz/ready" >/dev/null || return 1
  local v rel srv
  for _ in $(seq 1 18); do # 리비전 전환은 몇십 초 걸린다
    v=$(curl -fsS "https://${HOST}/healthz/version" 2>/dev/null || true)
    rel=$(jq -r '.release // empty' <<<"$v" 2>/dev/null || true)
    srv=$(jq -r '.server // empty' <<<"$v" 2>/dev/null || true)
    [ "$rel" = "$RELEASE" ] && break
    sleep 10
  done
  [ "$rel" = "$RELEASE" ] || { echo "::error::새 리비전이 뜨지 않았다 (release=${rel:-없음})"; return 1; }
  [ "$srv" = "$EXPECT_SERVER" ] || { echo "::error::서버 커밋이 다르다 (${srv} ≠ ${EXPECT_SERVER})"; return 1; }
  curl -fsS --retry 6 --retry-delay 5 --retry-all-errors "https://${HOST}/api/app/status" >/dev/null || return 1
  local web
  web=$(curl -fsS --retry 6 --retry-delay 5 --retry-all-errors "https://${HOST}/version.json" | jq -r '.web // empty') || return 1
  [ -n "$web" ] || { echo "::error::웹 버전 파일이 없다"; return 1; }
  if [ -n "${EXPECT_WEB:-}" ] && [ "$web" != "$EXPECT_WEB" ]; then echo "::error::웹 커밋이 다르다 (${web} ≠ ${EXPECT_WEB})"; return 1; fi
  echo "점검 통과: release=${rel} server=${srv::7} web=${web::7}"
}

if ! check; then
  echo "::warning::점검 실패 → 직전 이미지로 되돌린다 (${PREV_API##*/})"
  az containerapp update -n "$API" -g "$RG" --image "$PREV_API" --set-env-vars "MASHIL_RELEASE=${PREV_REL:-rollback}" -o none
  az containerapp update -n "$WORKER" -g "$RG" --image "$PREV_WORKER" --set-env-vars "MASHIL_RELEASE=${PREV_REL:-rollback}" -o none
  curl -fsS --retry 12 --retry-delay 10 --retry-all-errors "https://${HOST}/healthz/ready" >/dev/null \
    && echo "되돌림 완료" || echo "::error::되돌린 뒤에도 준비되지 않았다. 직접 확인 필요"
  exit 1
fi

# 5) 도보 도로망 Job (있으면): 이 배포와 같은 커밋의 임포터로 바꿔 둔다. 실행은 Job 일정대로 (deploy/azure/walk-job.sh)
WALK="mashil-walkimport${SUFFIX}"
OSM_IMG="${MIGRATE_IMG/mashil-migrate/mashil-osmimport}"
if az containerapp job show -n "$WALK" -g "$RG" -o none 2>/dev/null; then
  az containerapp job update -n "$WALK" -g "$RG" --image "$OSM_IMG" -o none     && echo "${WALK}: ${OSM_IMG##*/}"     || echo "::warning::${WALK} 이미지를 못 바꿨다 (도로망 갱신만 영향, 서비스는 정상)"
fi
