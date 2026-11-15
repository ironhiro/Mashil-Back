#!/usr/bin/env bash
# 이전 웹 빌드 정리 (OPS-04). cron 에서 하루 한 번.
#   - ${MASHIL_WEB_ROOT}/site 에서 지금 빌드에 없고, 마지막으로 쓰인 지 MASHIL_WEB_RETAIN_DAYS(기본 7)일이 넘은 파일만 지운다.
#     "마지막으로 쓰인 시각" = mtime. web-deploy.sh 가 교체 직전에 쓰던 빌드 파일을 touch 한다.
#   - index.html 은 지우지 않는다. 릴리스 기록(releases/<id>)은 30일 보관 (지금 릴리스는 항상 보관).
set -euo pipefail

WEB_ROOT="${MASHIL_WEB_ROOT:-/srv/mashil/web}"
SITE="$WEB_ROOT/site"
RELEASES="$WEB_ROOT/releases"
RETAIN_DAYS="${MASHIL_WEB_RETAIN_DAYS:-7}"
RELEASE_KEEP_DAYS="${MASHIL_WEB_RELEASE_KEEP_DAYS:-30}"
DRY_RUN="${DRY_RUN:-0}"

(( RETAIN_DAYS >= 7 )) || { echo "OPS-04: 보존 기간은 7일 이상이어야 함 (지금 $RETAIN_DAYS)" >&2; exit 1; }
cur="$(cat "$RELEASES/current" 2>/dev/null || true)"
[[ -n "$cur" && -f "$RELEASES/$cur/files.txt" ]] || { echo "지금 릴리스를 알 수 없어 아무것도 지우지 않음" >&2; exit 0; }

removed=0
while IFS= read -r -d '' f; do
  rel="${f#"$SITE"/}"
  [[ "$rel" == index.html || "$rel" == .index.html.tmp ]] && continue
  grep -qxF -- "$rel" "$RELEASES/$cur/files.txt" && continue
  if [[ "$DRY_RUN" == 1 ]]; then echo "지울 파일: $rel"; else rm -f -- "$f"; fi
  removed=$((removed + 1))
done < <(find "$SITE" -type f -mmin "+$((RETAIN_DAYS * 24 * 60))" -print0)
find "$SITE" -mindepth 1 -type d -empty -delete 2>/dev/null || true

# 오래된 릴리스 기록 (index.html 사본·파일 목록)
find "$RELEASES" -mindepth 1 -maxdepth 1 -type d ! -name "$cur" -mtime "+$RELEASE_KEEP_DAYS" -exec rm -rf -- {} + 2>/dev/null || true

echo "웹 정리: ${removed}개 파일 (보존 ${RETAIN_DAYS}일, 지금 릴리스 $cur)"
