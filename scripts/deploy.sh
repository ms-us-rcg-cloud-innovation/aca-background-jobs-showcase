#!/usr/bin/env bash
# One-command deploy for the Azure Container Apps background-jobs showcase.
# Mirrors scripts/deploy.ps1 for Linux/macOS. See that file for details.
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-}"
LOCATION="${LOCATION:-eastus}"
ACR_NAME="${ACR_NAME:-}"
NAME_PREFIX="${NAME_PREFIX:-acajobs}"
SQL_ADMIN_PASSWORD="${SQL_ADMIN_PASSWORD:-}"
IMAGE_TAG="${IMAGE_TAG:-$(date +%Y%m%d%H%M%S)}"

if [[ -z "$RESOURCE_GROUP" || -z "$ACR_NAME" || -z "$SQL_ADMIN_PASSWORD" ]]; then
  echo "Required: RESOURCE_GROUP, ACR_NAME, SQL_ADMIN_PASSWORD env vars." >&2
  echo "Example: RESOURCE_GROUP=rg-acajobs ACR_NAME=myacr123 SQL_ADMIN_PASSWORD='S3cret!' ./scripts/deploy.sh" >&2
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Subscription:"; az account show --query '{name:name,id:id}' -o table

AAD_LOGIN="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
AAD_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)"
echo "==> Entra ID SQL admin: $AAD_LOGIN ($AAD_OBJECT_ID)"

echo "==> Creating resource group '$RESOURCE_GROUP' in $LOCATION"
az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none

if ! az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  echo "==> Creating ACR '$ACR_NAME'"
  az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Basic --admin-enabled false -o none
fi

declare -A IMAGES=(
  [producer-api]="src/Producer.Api/Dockerfile"
  [dashboard]="src/Dashboard.Web/Dockerfile"
  [event-worker]="src/Worker.EventJob/Dockerfile"
  [scheduled-worker]="src/Worker.ScheduledJob/Dockerfile"
  [manual-worker]="src/Worker.ManualJob/Dockerfile"
)
for name in "${!IMAGES[@]}"; do
  echo "==> az acr build ${name}:${IMAGE_TAG}"
  az acr build -r "$ACR_NAME" -t "${name}:${IMAGE_TAG}" -f "${IMAGES[$name]}" "$REPO_ROOT" -o none
done

echo "==> Deploying infra/main.bicep"
OUTPUTS="$(az deployment group create \
  -g "$RESOURCE_GROUP" \
  -f "$REPO_ROOT/infra/main.bicep" \
  -p namePrefix="$NAME_PREFIX" \
  -p acrName="$ACR_NAME" \
  -p imageTag="$IMAGE_TAG" \
  -p sqlAdminPassword="$SQL_ADMIN_PASSWORD" \
  -p aadAdminLogin="$AAD_LOGIN" \
  -p aadAdminObjectId="$AAD_OBJECT_ID" \
  --query properties.outputs -o json)"

PRODUCER_URL="$(echo "$OUTPUTS" | jq -r '.producerUrl.value')"
DASHBOARD_URL="$(echo "$OUTPUTS" | jq -r '.dashboardUrl.value')"
SQL_FQDN="$(echo "$OUTPUTS" | jq -r '.sqlServerFqdn.value')"
SQL_DB="$(echo "$OUTPUTS" | jq -r '.sqlDatabase.value')"
UAMI_NAME="$(echo "$OUTPUTS" | jq -r '.managedIdentityName.value')"

cat <<EOF

===================== DEPLOYMENT COMPLETE =====================
Producer API : $PRODUCER_URL
Dashboard    : $DASHBOARD_URL
SQL server   : $SQL_FQDN  (db: $SQL_DB)
===============================================================

NEXT STEP - grant the managed identity access to Azure SQL.
Connect as the Entra admin ($AAD_LOGIN) and run:

  CREATE USER [$UAMI_NAME] FROM EXTERNAL PROVIDER;
  ALTER ROLE db_datareader ADD MEMBER [$UAMI_NAME];
  ALTER ROLE db_datawriter ADD MEMBER [$UAMI_NAME];
  ALTER ROLE db_ddladmin   ADD MEMBER [$UAMI_NAME];

Or: sqlcmd -S $SQL_FQDN -d $SQL_DB -G -i scripts/setup-sql.sql -v uamiName="$UAMI_NAME"
EOF
