# deploy/*.sh 공용 함수. 직접 실행하지 않고 source 한다.
#   ENV_FILE  운영 환경 변수 파일 (기본 /etc/mashil/mashil.env, chmod 600)
#   COMPOSE   docker compose 명령 (배열)

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${MASHIL_ENV_FILE:-/etc/mashil/mashil.env}"
COMPOSE=(docker compose -f "$DEPLOY_DIR/docker-compose.prod.yml" --env-file "$ENV_FILE")

[[ -r "$ENV_FILE" ]] || { echo "환경 변수 파일을 읽을 수 없음: $ENV_FILE" >&2; exit 1; }

# env 파일에서 값 하나 읽기 (셸에 같은 이름이 export 돼 있으면 그쪽이 우선 — docker compose 와 같은 규칙)
env_get() {
  local name="$1" fallback="${2:-}" v
  v="${!name:-}"
  if [[ -z "$v" ]]; then
    v="$(grep -E "^${name}=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- || true)"
  fi
  # 값 양끝 따옴표 제거
  v="${v%\"}"; v="${v#\"}"
  echo "${v:-$fallback}"
}

# Slack/Discord 웹훅 알림 (Worker 와 같은 형식 { text, content }). 웹훅이 없으면 표준 오류로만.
notify() {
  local level="$1" title="$2" body="${3:-}"
  local text="[마실 ${level}] ${title}"
  [[ -n "$body" ]] && text+=$'\n'"$body"
  echo "$text" >&2
  local url
  url="$(env_get Alerts__WebhookUrl)"
  [[ -z "$url" ]] && return 0
  local json
  json="$(printf '%s' "$text" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | awk 'BEGIN{ORS="\\n"} {print}')"
  curl -fsS -m 10 -H 'Content-Type: application/json' \
    -d "{\"text\":\"${json}\",\"content\":\"${json}\"}" "$url" >/dev/null || echo "웹훅 전송 실패" >&2
}
