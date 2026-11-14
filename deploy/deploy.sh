#!/usr/bin/env bash
# 무중단 배포 (05-operations §3.2, 08-infrastructure §4)
#   1) 새 이미지 받기  2) 마이그레이션(expand 만)  3) api-blue → api-green 하나씩 교체  4) worker 교체  5) 스모크 테스트
# 사용: MASHIL_ENV_FILE=/etc/mashil/mashil.env ./deploy/deploy.sh
set -euo pipefail

cd "$(dirname "$0")"
ENV_FILE="${MASHIL_ENV_FILE:-/etc/mashil/mashil.env}"
COMPOSE=(docker compose -f docker-compose.prod.yml --env-file "$ENV_FILE")
DOMAIN="$(grep -E '^MASHIL_DOMAIN=' "$ENV_FILE" | cut -d= -f2-)"

echo "[1/5] 이미지 받기"
"${COMPOSE[@]}" pull api-blue worker
"${COMPOSE[@]}" --profile ops pull migrate

echo "[2/5] 마이그레이션 (앱 시작 시 자동 실행하지 않음, OPS-03)"
"${COMPOSE[@]}" --profile ops run --rm migrate

wait_ready() {
  local svc="$1"
  for _ in $(seq 1 60); do
    if "${COMPOSE[@]}" exec -T "$svc" sh -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz/ready HTTP/1.0\r\n\r\n" >&3 && grep -q "200" <&3' 2>/dev/null; then
      echo "  $svc ready"; return 0
    fi
    sleep 2
  done
  echo "  $svc 가 준비되지 않음" >&2; return 1
}

echo "[3/5] API 교체 (한쪽씩 — 교체 중에는 Caddy 가 다른 쪽으로 보냄)"
for svc in api-blue api-green; do
  "${COMPOSE[@]}" up -d --no-deps --force-recreate "$svc"
  wait_ready "$svc"
done

echo "[4/5] Worker 교체 (임포트 중이었다면 다음 실행이 Abandoned 로 정리)"
"${COMPOSE[@]}" up -d --no-deps --force-recreate worker

echo "[5/5] 스모크 테스트"
curl -fsS "https://${DOMAIN}/healthz/ready" >/dev/null
curl -fsS "https://${DOMAIN}/api/toilets/near?lat=37.5665&lng=126.9780&radiusM=500&limit=1" >/dev/null
echo "배포 완료"
