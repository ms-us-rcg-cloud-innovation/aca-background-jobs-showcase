#requires -Version 7.0
<#
.SYNOPSIS
  One-command deploy for the Azure Container Apps background-jobs showcase.
.DESCRIPTION
  1. Creates the resource group and (if needed) an Azure Container Registry.
  2. Builds all five container images in the cloud with `az acr build`
     (runs in Azure, so it can reach nuget.org even from a locked-down laptop).
  3. Deploys infra/main.bicep (env, Service Bus, Azure SQL, apps, jobs, RBAC).
  4. Prints the app URLs and the exact SQL you must run to grant the managed
     identity access to the database.
.EXAMPLE
  ./scripts/deploy.ps1 -ResourceGroup rg-acajobs -Location eastus -AcrName myacr123 -SqlAdminPassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $ResourceGroup,
  [string] $Location = 'eastus',
  [Parameter(Mandatory)] [string] $AcrName,
  [string] $NamePrefix = 'acajobs',
  [Parameter(Mandatory)] [securestring] $SqlAdminPassword,
  [string] $ImageTag = (Get-Date -Format 'yyyyMMddHHmmss')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "==> Using subscription:" -ForegroundColor Cyan
az account show --query '{name:name, id:id}' -o table

# Signed-in identity becomes the SQL Entra ID admin (so you can run setup-sql).
$aadLogin    = az ad signed-in-user show --query userPrincipalName -o tsv
$aadObjectId = az ad signed-in-user show --query id -o tsv
Write-Host "==> Entra ID SQL admin will be: $aadLogin ($aadObjectId)" -ForegroundColor Cyan

Write-Host "==> Creating resource group '$ResourceGroup' in $Location" -ForegroundColor Cyan
az group create -n $ResourceGroup -l $Location -o none

# Create the ACR if it does not already exist.
$acrExists = az acr show -n $AcrName -g $ResourceGroup --query name -o tsv 2>$null
if (-not $acrExists) {
  Write-Host "==> Creating Azure Container Registry '$AcrName'" -ForegroundColor Cyan
  az acr create -n $AcrName -g $ResourceGroup --sku Basic --admin-enabled false -o none
}

# Build every image in the cloud. Context = repo root; one Dockerfile per service.
$images = @(
  @{ name = 'producer-api';     dockerfile = 'src/Producer.Api/Dockerfile' },
  @{ name = 'dashboard';        dockerfile = 'src/Dashboard.Web/Dockerfile' },
  @{ name = 'event-worker';     dockerfile = 'src/Worker.EventJob/Dockerfile' },
  @{ name = 'scheduled-worker'; dockerfile = 'src/Worker.ScheduledJob/Dockerfile' },
  @{ name = 'manual-worker';    dockerfile = 'src/Worker.ManualJob/Dockerfile' }
)
foreach ($img in $images) {
  Write-Host "==> az acr build $($img.name):$ImageTag" -ForegroundColor Cyan
  az acr build -r $AcrName -t "$($img.name):$ImageTag" -f $img.dockerfile $repoRoot -o none
}

$plainPwd = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
  [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlAdminPassword))

Write-Host "==> Deploying infra/main.bicep" -ForegroundColor Cyan
$deployment = az deployment group create `
  -g $ResourceGroup `
  -f "$repoRoot/infra/main.bicep" `
  -p namePrefix=$NamePrefix `
  -p acrName=$AcrName `
  -p imageTag=$ImageTag `
  -p sqlAdminPassword=$plainPwd `
  -p aadAdminLogin=$aadLogin `
  -p aadAdminObjectId=$aadObjectId `
  --query properties.outputs -o json | ConvertFrom-Json

$producerUrl  = $deployment.producerUrl.value
$dashboardUrl = $deployment.dashboardUrl.value
$sqlFqdn      = $deployment.sqlServerFqdn.value
$sqlDb        = $deployment.sqlDatabase.value
$uamiName     = $deployment.managedIdentityName.value

Write-Host "`n===================== DEPLOYMENT COMPLETE =====================" -ForegroundColor Green
Write-Host "Producer API : $producerUrl"
Write-Host "Dashboard    : $dashboardUrl"
Write-Host "SQL server   : $sqlFqdn  (db: $sqlDb)"
Write-Host "Event job    : $($deployment.eventJobName.value)"
Write-Host "Scheduled job: $($deployment.scheduledJobName.value)"
Write-Host "Manual job   : $($deployment.manualJobName.value)"
Write-Host "===============================================================`n"

Write-Host "NEXT STEP - grant the managed identity access to Azure SQL." -ForegroundColor Yellow
Write-Host "Connect to the DB as the Entra admin ($aadLogin) and run:`n" -ForegroundColor Yellow
@"
CREATE USER [$uamiName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader  ADD MEMBER [$uamiName];
ALTER ROLE db_datawriter  ADD MEMBER [$uamiName];
ALTER ROLE db_ddladmin    ADD MEMBER [$uamiName];
"@ | Write-Host -ForegroundColor Gray

Write-Host "`nExample (requires the modern 'sqlcmd' with Entra auth):" -ForegroundColor Yellow
Write-Host "  sqlcmd -S $sqlFqdn -d $sqlDb -G -i scripts/setup-sql.sql -v uamiName=`"$uamiName`"" -ForegroundColor Gray
