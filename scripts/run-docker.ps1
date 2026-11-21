# Los 4 servicios en CONTENEDORES (N-4004), mismos puertos que run-local.ps1 (no correr ambos).
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-docker.ps1         # build + up
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-docker.ps1 -Down   # baja los 4 (el emulador queda)
# Logs: docker compose logs -f orders
[CmdletBinding()]
param([switch]$Down)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$services = 'catalog', 'orders', 'payments', 'ticketing'

if ($Down) {
    docker compose -f (Join-Path $root 'docker-compose.yml') --profile services rm -sf $services
    exit $LASTEXITCODE
}

# Emulador arriba + AWS_EMULATOR_* y .secrets\ca.pem de esta corrida (compose los lee del entorno).
. (Join-Path $PSScriptRoot 'emulator-env.ps1')
if (-not $env:SEED_EVENTS) {
    $env:SEED_EVENTS = 'evt-mj-tribute-night|Michael Jackson Tribute Night|2027-03-12T21:00:00Z|120.50|500;evt-legacy-tour|Legacy World Tour (Orquestal)|2027-04-03T20:00:00Z|85.00|250'
}

$ErrorActionPreference = 'Continue' # PS 5.1: docker escribe progreso por stderr
docker compose -f (Join-Path $root 'docker-compose.yml') --profile services up -d --build $services
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$probes = @{ 5080 = '/events'; 5081 = '/health'; 5082 = '/health'; 5083 = '/health' }
foreach ($port in $probes.Keys) {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        curl.exe -s -f -o NUL "http://localhost:$port$($probes[$port])"
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { Write-Host "ERROR: :$port no responde tras 30 s (docker compose logs)."; exit 1 }
}
Write-Host '==> Los 4 servicios corren en contenedores: http://localhost:5080..5083 (mismas rutas que run-local).'
