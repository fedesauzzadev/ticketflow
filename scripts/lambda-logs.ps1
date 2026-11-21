# Logs de la Lambda ticket-notifier en CloudWatch Logs del emulador (N-4002).
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lambda-logs.ps1            # todas las notificaciones
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lambda-logs.ps1 pur-abc... # las de una compra
# Requiere aws-cli v2 en PATH. PS 5.1.
param([string]$Filter = 'tickets-issued')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'emulator-env.ps1')
$env:AWS_ACCESS_KEY_ID = $env:AWS_EMULATOR_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:AWS_EMULATOR_SECRET_ACCESS_KEY
$env:AWS_CA_BUNDLE = $env:AWS_EMULATOR_CA_PATH
aws --endpoint-url $env:AWS_EMULATOR_ENDPOINT --region us-east-1 --no-cli-pager logs filter-log-events `
    --log-group-name /aws/lambda/ticketflow-ticket-notifier --filter-pattern "`"$Filter`"" `
    --query 'events[].message' --output text
exit $LASTEXITCODE
