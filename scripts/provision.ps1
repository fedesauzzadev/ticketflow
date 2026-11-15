# Aprovisionamiento IaC del emulador (D-0019 / N-3002, H-08 AC6..AC9): aplica (default) o
# verifica (-Verify) el manifiesto infra/aws-local.json contra el emulador AWS local.
#   - Idempotente: aplicar N veces deja el mismo conjunto de recursos (ensure tolerante a carreras).
#   - -Verify lista los recursos reales y los compara con el manifiesto: falta/difiere =>
#     exit 1 con el diff; extras => WARNING (exit 0) — la sesión compartida puede tener it-*.
# From scratch: docker rm -f aws-emulator (el estado del emulador es en memoria) y correr
# este script: recrea el contenedor (emulator-env.ps1) y crea todos los recursos.
# PS 5.1 (no pwsh). Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\provision.ps1          # --apply
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\provision.ps1 -Verify  # diff vs manifiesto
[CmdletBinding()]
param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Emulador arriba + credenciales/CA de esta corrida + env AWS_EMULATOR_* (un solo origen).
. (Join-Path $PSScriptRoot 'emulator-env.ps1')

$manifest = Join-Path $root 'infra\aws-local.json'
$mode = if ($Verify) { '--verify' } else { '--apply' }

Write-Host "==> TicketFlow.Provision $mode (manifiesto: infra\aws-local.json)..."
dotnet run --project (Join-Path $root 'tools\TicketFlow.Provision\TicketFlow.Provision.csproj') -- $mode --manifest $manifest
exit $LASTEXITCODE
