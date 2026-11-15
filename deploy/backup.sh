#!/usr/bin/env bash
# Postgres 백업 (08-infrastructure §1, §5). 호스트 cron 에서 매일 03:00 KST (deploy/cron/mashil 참고).
#   - pg_dump -Fc → ${MASHIL_BACKUP_DIR}/daily/mashil-YYYYmmdd-HHMM.dump (권한 600)
#   - 받은 덤프를 pg_restore --list 로 읽어 보고 나서야 확정 (깨진 덤프를 남기지 않음)
#   - 일요일 덤프는 weekly/ 에도 복사. 보관: 일간 7일, 주간 28일 (MASHIL_BACKUP_DAILY_DAYS / _WEEKLY_DAYS)
#   - MASHIL_BACKUP_REMOTE (rclone 원격, 예: ncloud:mashil-backup/db) 가 있으면 서버 밖으로도 복사
#   - 실패하면 Alerts__WebhookUrl 로 알림
# 복구 절차는 docs/OPERATIONS.md "백업과 복구".
set -euo pipefail

source "$(dirname "$0")/lib.sh"

DIR="$(env_get MASHIL_BACKUP_DIR /var/backups/mashil)"
DAILY_DAYS="$(env_get MASHIL_BACKUP_DAILY_DAYS 7)"
WEEKLY_DAYS="$(env_get MASHIL_BACKUP_WEEKLY_DAYS 28)"
REMOTE="$(env_get MASHIL_BACKUP_REMOTE)"

trap 'notify 오류 "DB 백업 실패" "$(hostname) $(date "+%F %T")"' ERR
umask 077
mkdir -p "$DIR/daily" "$DIR/weekly"

stamp="$(date +%Y%m%d-%H%M)"
out="$DIR/daily/mashil-$stamp.dump"
tmp="$DIR/daily/.mashil-$stamp.dump.partial"

"${COMPOSE[@]}" exec -T postgres pg_dump -U mashil -d mashil -Fc --no-owner > "$tmp"
"${COMPOSE[@]}" exec -T postgres pg_restore --list < "$tmp" > /dev/null
mv -f "$tmp" "$out"
echo "백업: $out ($(du -h "$out" | cut -f1))"

if [[ "$(date +%u)" == 7 ]]; then
  cp -f "$out" "$DIR/weekly/"
fi

find "$DIR/daily"  -name 'mashil-*.dump' -type f -mtime "+$((DAILY_DAYS - 1))"  -delete
find "$DIR/weekly" -name 'mashil-*.dump' -type f -mtime "+$((WEEKLY_DAYS - 1))" -delete
find "$DIR/daily" -name '.mashil-*.partial' -type f -mmin +120 -delete

if [[ -n "$REMOTE" ]]; then
  command -v rclone >/dev/null || { echo "MASHIL_BACKUP_REMOTE 가 있지만 rclone 이 없음" >&2; false; }
  # 원격에도 같은 보관 기간을 둔다 (copy 후 원격에서 오래된 것 삭제)
  rclone copy "$DIR" "$REMOTE" --include 'daily/mashil-*.dump' --include 'weekly/mashil-*.dump'
  rclone delete "$REMOTE/daily"  --min-age "${DAILY_DAYS}d"
  rclone delete "$REMOTE/weekly" --min-age "${WEEKLY_DAYS}d"
fi
