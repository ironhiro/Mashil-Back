#!/usr/bin/env bash
# 웹(Mashil-web) 배포 — 서버에서 실행 (OPS-04, 05-operations §3.1)
#   Caddy 가 ${MASHIL_WEB_ROOT}/site 를 정적으로 서빙한다 (Caddyfile: /assets/* immutable, 나머지 no-cache, SPA 폴백).
#   1) 지금 쓰는 빌드의 에셋 mtime 을 갱신 — "이 시각까지 쓰였다". 보존 기간은 교체된 시각부터 센다
#   2) 새 빌드의 파일을 덮어쓰기만 한다 (지우지 않음 → 이전 해시 에셋이 남아 오래 열린 웹뷰의 청크 404 를 막음)
#   3) index.html 을 마지막에 원자적으로 교체 (mv)
#   이전 에셋은 web-cleanup.sh(cron)가 7일 지나면 지운다.
#
# 사용:
#   ./deploy/web-deploy.sh <dist 디렉터리> [릴리스 id]   예: ./deploy/web-deploy.sh /tmp/mashil-web-<sha> <sha>
#   ./deploy/web-deploy.sh --rollback [릴리스 id]         index.html 만 이전 빌드로 (에셋은 보존 중이라 그대로 동작)
#   ./deploy/web-deploy.sh --list
set -euo pipefail

WEB_ROOT="${MASHIL_WEB_ROOT:-/srv/mashil/web}"
SITE="$WEB_ROOT/site"
RELEASES="$WEB_ROOT/releases"
mkdir -p "$SITE/assets" "$RELEASES"

current_id() { [[ -f "$RELEASES/current" ]] && cat "$RELEASES/current" || true; }

# 지금 쓰는 빌드의 파일을 touch (교체 시점부터 보존 기간을 센다)
touch_current() {
  local cur; cur="$(current_id)"
  [[ -n "$cur" && -f "$RELEASES/$cur/files.txt" ]] || return 0
  (cd "$SITE" && while IFS= read -r f; do [[ -e "$f" ]] && touch -c -- "$f"; done < "$RELEASES/$cur/files.txt")
}

swap_index() {
  local src="$1" id="$2"
  cp -- "$src" "$SITE/.index.html.tmp"
  chmod 644 "$SITE/.index.html.tmp"
  mv -f -- "$SITE/.index.html.tmp" "$SITE/index.html"
  printf '%s\n' "$id" > "$RELEASES/.current.tmp" && mv -f "$RELEASES/.current.tmp" "$RELEASES/current"
  printf '%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$id" "${3:-deploy}" >> "$RELEASES/history"
}

case "${1:-}" in
  ""|-h|--help)
    sed -n '2,15p' "$0"; exit 1 ;;
  --list)
    cat "$RELEASES/history" 2>/dev/null || echo "기록 없음"
    echo "current: $(current_id)"; exit 0 ;;
  --rollback)
    cur="$(current_id)"
    target="${2:-}"
    if [[ -z "$target" ]]; then
      # 기록을 뒤에서부터 보며 지금과 다른 첫 릴리스
      target="$(awk -F'\t' -v cur="$cur" '$2 != cur { t = $2 } END { print t }' "$RELEASES/history" 2>/dev/null || true)"
    fi
    [[ -n "$target" && -f "$RELEASES/$target/index.html" ]] || { echo "되돌릴 릴리스가 없음: ${target:-?}" >&2; exit 1; }
    missing="$(cd "$SITE" && while IFS= read -r f; do [[ -e "$f" ]] || echo "$f"; done < "$RELEASES/$target/files.txt")"
    [[ -z "$missing" ]] || { echo "이미 정리된 파일이 있어 되돌릴 수 없음 — 그 빌드를 다시 배포할 것:" >&2; echo "$missing" | head >&2; exit 1; }
    touch_current
    swap_index "$RELEASES/$target/index.html" "$target" "rollback"
    echo "웹 롤백 완료 → $target"; exit 0 ;;
esac

DIST="$(cd "$1" && pwd)"
ID="${2:-$(date -u +%Y%m%dT%H%M%SZ)}"
[[ "$ID" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "릴리스 id 는 영숫자·._- 만" >&2; exit 1; }
[[ -f "$DIST/index.html" && -d "$DIST/assets" ]] || { echo "vite 빌드 결과가 아님 (index.html, assets/ 필요): $DIST" >&2; exit 1; }

echo "[1/3] 지금 빌드($(current_id || true)) 에셋 보존 시각 갱신"
touch_current

echo "[2/3] 새 파일 복사 (기존 파일은 지우지 않음)"
mkdir -p "$RELEASES/$ID"
(cd "$DIST" && find . -type f ! -path ./index.html | sed 's|^\./||' | LC_ALL=C sort) > "$RELEASES/$ID/files.txt"
cp -- "$DIST/index.html" "$RELEASES/$ID/index.html"
# tar -m: 복사 시각을 mtime 으로 (빌드 머신 시각이 아니라). index.html 은 3단계에서
tar -C "$DIST" --exclude=./index.html -cf - . | tar -C "$SITE" -xmf -
chmod -R a+rX "$SITE"

echo "[3/3] index.html 교체"
swap_index "$RELEASES/$ID/index.html" "$ID"
echo "웹 배포 완료 → $ID (되돌리기: $0 --rollback)"
