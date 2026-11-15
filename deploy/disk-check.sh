#!/usr/bin/env bash
# 디스크 사용률 감시 (08-infrastructure §6). cron 에서 1시간마다. 기준을 넘으면 웹훅 알림.
#   MASHIL_DISK_ALERT_PERCENT (기본 80), 확인할 경로는 Docker 데이터(DB 볼륨 포함)와 백업 디렉터리
set -euo pipefail

source "$(dirname "$0")/lib.sh"

LIMIT="$(env_get MASHIL_DISK_ALERT_PERCENT 80)"
paths=("$(docker info --format '{{.DockerRootDir}}' 2>/dev/null || echo /var/lib/docker)" "$(env_get MASHIL_BACKUP_DIR /var/backups/mashil)")

for p in "${paths[@]}"; do
  [[ -e "$p" ]] || continue
  used="$(df -P "$p" | awk 'NR==2 { gsub("%", "", $5); print $5 }')"
  if (( used >= LIMIT )); then
    notify 경고 "디스크 ${used}% 사용 ($p)" "$(hostname). docker system df, 백업·로그 정리 확인"
  fi
done
