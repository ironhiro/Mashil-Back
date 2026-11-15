#!/usr/bin/env bash
# 롤백 (OPS-06): deploy/.deployed 에 기록된 이전 이미지로 되돌린다. 목표 5분 이내.
#   - 마이그레이션은 실행하지 않는다. 스키마는 expand 만 하므로 이전 코드가 새 스키마에서 돈다 (05-operations §3.3).
#     이전 번들을 실행하면 새 마이그레이션을 되돌리려 하므로 위험하다.
#   - 교체 방식은 deploy.sh 와 같다 (api-blue → api-green 한쪽씩, 무중단).
#   - 웹은 따로: deploy/web-deploy.sh --rollback
#
# 사용:
#   ./deploy/rollback.sh            지금과 다른 API 이미지였던 가장 최근 배포로
#   ./deploy/rollback.sh --list     기록 보기 (번호 포함)
#   ./deploy/rollback.sh <번호>     --list 의 그 줄로
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
HISTORY="${MASHIL_DEPLOY_HISTORY:-$DIR/.deployed}"
[[ -s "$HISTORY" ]] || { echo "배포 기록이 없음: $HISTORY" >&2; exit 1; }

if [[ "${1:-}" == "--list" ]]; then
  # 번호  시각  태그  api 이미지  메모
  awk -F'\t' '{ printf "%4d  %s  %-12s  %s  %s\n", NR, $1, $2, $3, $6 }' "$HISTORY"
  exit 0
fi

total="$(wc -l < "$HISTORY")"
current_api="$(tail -n 1 "$HISTORY" | cut -f3)"

if [[ -n "${1:-}" ]]; then
  [[ "$1" =~ ^[0-9]+$ ]] && (( $1 >= 1 && $1 <= total )) || { echo "번호는 1..$total (--list 참고)" >&2; exit 1; }
  line="$(sed -n "${1}p" "$HISTORY")"
else
  # 뒤에서부터 지금과 다른 API 이미지를 찾는다 (롤백한 뒤 다시 롤백해도 같은 곳을 오가지 않게)
  line="$(awk -F'\t' -v cur="$current_api" '$3 != cur { l = $0 } END { print l }' "$HISTORY")"
  [[ -n "$line" ]] || { echo "되돌릴 이전 배포가 없음 (기록이 모두 지금 이미지)" >&2; exit 1; }
fi

IFS=$'\t' read -r when tag api worker migrate _note <<< "$line"
echo "롤백 대상: $when (tag=$tag)"
echo "  api    $api"
echo "  worker $worker"

export MASHIL_IMAGE_API="$api" MASHIL_IMAGE_WORKER="$worker" MASHIL_IMAGE_MIGRATE="$migrate"
export MASHIL_SKIP_MIGRATE=1
export MASHIL_DEPLOY_NOTE="rollback-to:${tag}@${when}"
exec "$DIR/deploy.sh"
