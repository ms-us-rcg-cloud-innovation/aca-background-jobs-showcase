#requires -Version 7.0
<#
.SYNOPSIS
  Exercises the deployed showcase end-to-end so you can watch the dashboard.
.DESCRIPTION
  Sends fire-and-forget and delayed jobs (incl. a forced failure to show
  retries/dead-lettering) to the Producer API, then triggers the manual batch
  job. Open the dashboard URL in a browser to watch executions appear.
.EXAMPLE
  ./scripts/demo.ps1 -ProducerUrl https://acajobs-producer-api.x.azurecontainerapps.io -ResourceGroup rg-acajobs -ManualJobName acajobs-manual-worker
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $ProducerUrl,
  [string] $ResourceGroup,
  [string] $ManualJobName,
  [int] $FireAndForget = 5,
  [int] $Delayed = 3
)
$ErrorActionPreference = 'Stop'
$ProducerUrl = $ProducerUrl.TrimEnd('/')

Write-Host "==> Enqueue $FireAndForget fire-and-forget jobs" -ForegroundColor Cyan
for ($i = 1; $i -le $FireAndForget; $i++) {
  $body = @{ jobType = 'invoice-sync'; payload = (@{ i = $i } | ConvertTo-Json -Compress) } | ConvertTo-Json
  Invoke-RestMethod -Method Post -Uri "$ProducerUrl/jobs/enqueue" -ContentType 'application/json' -Body $body | Out-Null
}

Write-Host "==> Enqueue 1 job that will FAIL (demonstrates retry + dead-letter)" -ForegroundColor Cyan
$failBody = @{ jobType = 'invoice-sync'; payload = '{"fail":true}' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$ProducerUrl/jobs/enqueue" -ContentType 'application/json' -Body $failBody | Out-Null

Write-Host "==> Schedule $Delayed delayed jobs (visible in 20s)" -ForegroundColor Cyan
for ($i = 1; $i -le $Delayed; $i++) {
  $body = @{ jobType = 'nightly-export'; payload = '{}'; delaySeconds = 20 } | ConvertTo-Json
  Invoke-RestMethod -Method Post -Uri "$ProducerUrl/jobs/schedule" -ContentType 'application/json' -Body $body | Out-Null
}

if ($ResourceGroup -and $ManualJobName) {
  Write-Host "==> Start the manual batch job" -ForegroundColor Cyan
  az containerapp job start -n $ManualJobName -g $ResourceGroup -o none
}

Write-Host "`nDone. Recent job stats:" -ForegroundColor Green
Invoke-RestMethod -Uri "$ProducerUrl/jobs/stats" | ConvertTo-Json
Write-Host "`nOpen the dashboard to watch executions update live." -ForegroundColor Green
