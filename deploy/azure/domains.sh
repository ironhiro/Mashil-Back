#!/usr/bin/env bash
# 마실 도메인 연결 (Azure Container Apps 관리형 인증서). docs/AZURE.md
#
#   bash deploy/azure/domains.sh          # Cloudflare 에 넣을 레코드를 보여 준다
#   bash deploy/azure/domains.sh bind     # 레코드를 넣은 뒤: 도메인을 붙이고 인증서를 받는다
#
# Cloudflare 레코드는 프록시 끔(DNS only). 켜면 관리형 인증서 검증·갱신이 실패하고, 요청 제한이 Cloudflare IP 를 본다.
set -euo pipefail

RG="${RG:-rg-mashil}"
ENV_NAME="${ENV_NAME:-cae-mashil}"
declare -A HOSTS=( [mashil-api]=toilets.ironhiro.dev [mashil-api-dev]=toilets-dev.ironhiro.dev )

ASUID="$(az containerapp env show -g "$RG" -n "$ENV_NAME" --query properties.customDomainConfiguration.customDomainVerificationId -o tsv)"

if [ "${1:-}" != bind ]; then
  echo "Cloudflare DNS (ironhiro.dev) 에 아래 레코드를 넣는다. Proxy status: DNS only"
  echo
  printf "%-6s %-26s %s\n" Type Name Value
  for app in "${!HOSTS[@]}"; do
    host="${HOSTS[$app]}"; sub="${host%%.ironhiro.dev}"
    fqdn="$(az containerapp show -g "$RG" -n "$app" --query properties.configuration.ingress.fqdn -o tsv)"
    printf "%-6s %-26s %s\n" CNAME "$sub" "$fqdn"
    printf "%-6s %-26s %s\n" TXT "asuid.$sub" "$ASUID"
  done
  echo
  echo "넣고 나서: bash deploy/azure/domains.sh bind"
  exit 0
fi

for app in "${!HOSTS[@]}"; do
  host="${HOSTS[$app]}"
  echo "== $app ← $host"
  az containerapp hostname add -g "$RG" -n "$app" --hostname "$host" -o none 2>/dev/null || true
  # 관리형 인증서 (CNAME 검증). 발급까지 수 분 걸릴 수 있다
  az containerapp hostname bind -g "$RG" -n "$app" --hostname "$host" --environment "$ENV_NAME" --validation-method CNAME -o none
  echo "   https://$host"
done
