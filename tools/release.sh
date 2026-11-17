#!/usr/bin/env bash
# 운영 배포: 개발 서버에 떠 있는 main 커밋에 날짜 버전 태그를 붙여 push 한다 (docs/RELEASE.md).
# 태그 push 를 받은 CI(release 작업)가 개발 이미지를 그대로 운영에 올린다.
#   tools/release.sh          # 오늘 날짜 (한국 시간) 로. 같은 날 두 번째면 .2, .3 ...
#   tools/release.sh --dry    # 태그 이름과 커밋만 보여 준다
set -euo pipefail
DEV=https://toilets-dev.ironhiro.dev
DRY=${1:-}

git fetch -q --tags origin main
MAIN=$(git rev-parse origin/main)
DEV_SHA=$(curl -fsS "$DEV/healthz/version" | sed -E 's/.*"server":"([^"]+)".*/\1/')

if [ "$DEV_SHA" != "$MAIN" ]; then
  echo "개발 서버(${DEV_SHA::7})가 아직 main(${MAIN::7})이 아니다."
  echo "개발 배포가 끝나고 확인한 뒤 다시 실행한다 (진행 상황: gh run list -L 3)."
  exit 1
fi

BASE="v$(TZ=Asia/Seoul date +%Y.%m.%d)"
TAG="$BASE"; N=1
while git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; do N=$((N + 1)); TAG="$BASE.$N"; done

LAST=$(git describe --tags --abbrev=0 --match 'v20*' "$MAIN" 2>/dev/null || true)
echo "태그 $TAG → ${MAIN::7} (직전 운영: ${LAST:-없음})"
if [ -n "$LAST" ]; then git log --oneline --first-parent "$LAST..$MAIN" | sed 's/^/  /'; fi
[ "$DRY" = "--dry" ] && exit 0

git tag -a "$TAG" "$MAIN" -m "Release $TAG"
git push -q origin "$TAG"
echo "push 완료. 운영 배포 진행: gh run list -L 1 / 끝나면 https://toilets.ironhiro.dev/healthz/version"
