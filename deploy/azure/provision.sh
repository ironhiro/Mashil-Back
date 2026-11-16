#!/usr/bin/env bash
# 마실 Azure 리소스 만들기 (Azure Container Apps + Azure Database for PostgreSQL). docs/AZURE.md
#
#   az login 한 뒤:  bash deploy/azure/provision.sh
#
# 만드는 것 (한국 중부, 리소스 그룹 하나):
#   - PostgreSQL Flexible Server (한국 남부 — 이 구독은 중부가 막혀 있음, Burstable B1ms, 32GB) + PostGIS 허용, DB 두 개 (mashil = 운영, mashil_dev = 개발)
#   - Container Apps 환경 하나
#   - 앱: mashil-api(운영, 최소 1) / mashil-api-dev(개발, 최소 0) — 웹 + API, 외부 인그레스 8080
#         mashil-worker(운영, 최소 1) / mashil-worker-dev(개발, 최소 0 — 필요할 때만 1로)
#   - 작업: mashil-migrate / mashil-migrate-dev (수동 실행, CI 가 배포마다 실행)
# 이미지는 GHCR 공개 패키지라 레지스트리 자격 증명이 필요 없다. 처음에는 :latest, 그다음부터 CI 가 커밋 태그로 바꾼다.
#
# 비밀값: DB 비밀번호와 JWT 서명 키는 여기서 새로 만들어 Container Apps 시크릿에만 넣는다 (화면에 찍지 않음).
#         관리 작업(도보 도로망 임포트 등)에 쓰게 ~/.mashil/azure.env (권한 600) 에도 남긴다. 저장소에는 넣지 않는다.
# 다시 돌려도 이미 있는 리소스는 건너뛴다 (비밀값은 처음 만든 것을 다시 쓴다).
set -euo pipefail

LOCATION="${LOCATION:-koreacentral}"
PG_LOCATION="${PG_LOCATION:-koreasouth}"   # 이 구독은 한국 중부 PostgreSQL 이 막혀 있어(SkuNotAvailable) DB 만 한국 남부
RG="${RG:-rg-mashil}"
ENV_NAME="${ENV_NAME:-cae-mashil}"
PG="${PG:-pg-mashil-ks-$(az account show --query id -o tsv | cut -c1-6)}"   # 전 세계에서 유일해야 해서 구독 id 앞자리를 붙인다 (ks = 한국 남부)
PG_ADMIN="${PG_ADMIN:-mashiladmin}"
PG_ZONE="${PG_ZONE:-}"   # SkuNotAvailable 이 나면 1·2·3 중 되는 영역을 지정 (예: PG_ZONE=2)
REG="ghcr.io/ironhiro"
SECRETS_FILE="${HOME}/.mashil/azure.env"

mkdir -p "$(dirname "$SECRETS_FILE")"
touch "$SECRETS_FILE"
chmod 600 "$SECRETS_FILE"
# shellcheck disable=SC1090
source "$SECRETS_FILE"
gen() { openssl rand -base64 48 | tr -d '/+=\n' | cut -c1-40; }
save() { grep -q "^$1=" "$SECRETS_FILE" || echo "$1=$2" >> "$SECRETS_FILE"; }
PG_PASSWORD="${PG_PASSWORD:-$(gen)}"; save PG_PASSWORD "$PG_PASSWORD"
JWT_PROD="${JWT_PROD:-$(gen)}"; save JWT_PROD "$JWT_PROD"
JWT_DEV="${JWT_DEV:-$(gen)}"; save JWT_DEV "$JWT_DEV"
save PG_HOST "$PG.postgres.database.azure.com"
save PG_ADMIN "$PG_ADMIN"

echo "== 리소스 그룹 $RG ($LOCATION)"
az group create -n "$RG" -l "$LOCATION" -o none

echo "== PostgreSQL $PG ($PG_LOCATION, B1ms, 32GB, 16)"
if ! az postgres flexible-server show -g "$RG" -n "$PG" -o none 2>/dev/null; then
  # --public-access 0.0.0.0: Azure 안의 서비스(Container Apps)만 허용. PC 는 아래에서 따로 연다
  az postgres flexible-server create -g "$RG" -n "$PG" -l "$PG_LOCATION" \
    --tier Burstable --sku-name Standard_B1ms --storage-size 32 --version 16 \
    --admin-user "$PG_ADMIN" --admin-password "$PG_PASSWORD" \
    --public-access 0.0.0.0 --backup-retention 7 ${PG_ZONE:+--zone "$PG_ZONE"} --yes -o none
fi
az postgres flexible-server parameter set -g "$RG" -s "$PG" -n azure.extensions --value POSTGIS -o none
for db in mashil mashil_dev; do
  az postgres flexible-server db show -g "$RG" -s "$PG" -n "$db" -o none 2>/dev/null \
    || az postgres flexible-server db create -g "$RG" -s "$PG" -n "$db" -o none
done
MY_IP="$(curl -fsS https://api.ipify.org)"
az postgres flexible-server firewall-rule create -g "$RG" -s "$PG" -n admin-pc --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" -o none
echo "   이 PC($MY_IP)에서 접속 허용 (임포트용, 끝나면 지워도 됨: firewall-rule delete -s <서버> -n admin-pc)"

conn() { echo "Host=$PG.postgres.database.azure.com;Port=5432;Database=$1;Username=$PG_ADMIN;Password=$PG_PASSWORD;SSL Mode=Require"; }

echo "== Container Apps 환경 $ENV_NAME"
az containerapp env show -g "$RG" -n "$ENV_NAME" -o none 2>/dev/null \
  || az containerapp env create -g "$RG" -n "$ENV_NAME" -l "$LOCATION" -o none

# 앱 하나 만들기 (이미 있으면 건너뜀). $1 이름, $2 이미지, $3 최소, $4 최대, $5 vCPU, $6 메모리, $7 인그레스(yes/no), 나머지 env
mkapp() {
  local name=$1 image=$2 min=$3 max=$4 cpu=$5 mem=$6 ingress=$7; shift 7
  if az containerapp show -g "$RG" -n "$name" -o none 2>/dev/null; then echo "   $name 있음 (건너뜀)"; return; fi
  local args=(-g "$RG" -n "$name" --environment "$ENV_NAME" --image "$image"
    --min-replicas "$min" --max-replicas "$max" --cpu "$cpu" --memory "$mem" -o none)
  [ "$ingress" = yes ] && args+=(--ingress external --target-port 8080)
  az containerapp create "${args[@]}" "$@"
  echo "   $name 만듦"
}

for stage in prod dev; do
  if [ "$stage" = prod ]; then sfx=""; db=mashil; jwt="$JWT_PROD"; envname=production; minApi=1; minWorker=1
  else sfx="-dev"; db=mashil_dev; jwt="$JWT_DEV"; envname=development; minApi=0; minWorker=0; fi
  cs="$(conn "$db")"

  echo "== $stage: API mashil-api$sfx"
  mkapp "mashil-api$sfx" "$REG/mashil-api:latest" "$minApi" 1 0.5 1.0Gi yes \
    --secrets "db=$cs" "jwt=$jwt" \
    --env-vars ASPNETCORE_ENVIRONMENT=Production "ConnectionStrings__Postgres=secretref:db" "Jwt__SigningKey=secretref:jwt" \
      ForwardedHeaders__TrustIngress=true "Sentry__Environment=$envname" Routing__Enabled=true

  echo "== $stage: Worker mashil-worker$sfx"
  mkapp "mashil-worker$sfx" "$REG/mashil-worker:latest" "$minWorker" 1 0.25 0.5Gi no \
    --secrets "db=$cs" \
    --env-vars DOTNET_ENVIRONMENT=Production "ConnectionStrings__Postgres=secretref:db" "Sentry__Environment=$envname"

  echo "== $stage: 마이그레이션 작업 mashil-migrate$sfx"
  if ! az containerapp job show -g "$RG" -n "mashil-migrate$sfx" -o none 2>/dev/null; then
    az containerapp job create -g "$RG" -n "mashil-migrate$sfx" --environment "$ENV_NAME" \
      --trigger-type Manual --replica-timeout 900 --replica-retry-limit 0 --parallelism 1 --replica-completion-count 1 \
      --image "$REG/mashil-migrate:latest" --cpu 0.25 --memory 0.5Gi \
      --secrets "db=$cs" --env-vars "MASHIL_DB=secretref:db" -o none
  fi
done

echo
echo "== 끝. 다음 (docs/AZURE.md):"
echo "   1) 마이그레이션: az containerapp job start -g $RG -n mashil-migrate-dev  (운영은 mashil-migrate)"
echo "   2) 도메인: bash deploy/azure/domains.sh  (Cloudflare 에 넣을 CNAME·TXT 를 알려 주고, 레코드가 생기면 인증서를 붙인다)"
echo "   3) CI 배포 자격 증명: docs/AZURE.md '배포 자격 증명'"
echo "   비밀값 파일: $SECRETS_FILE (저장소에 넣지 말 것)"
