# Entorno del emulador AWS local (D-0019): contenedor up-or-start + credenciales/CA de ESTA
# corrida + health check + env AWS_EMULATOR_*. Dot-sourced (`. .\emulator-env.ps1`) por
# run-local.ps1 y provision.ps1 — UN solo origen para la plomería del emulador (antes
# triplicada). PS 5.1 (no pwsh). Requisitos: Docker Desktop.
#
# Al dot-sourcear, este script:
#   1. Arranca el contenedor 'aws-emulator' si está caído (lo buildea si no existe).
#   2. docker cp de la CA (reintenta 60 s: en frío el emulador la regenera al arrancar).
#   3. Health check https://localhost:9297/__admin/health.
#   4. Extrae credenciales (docker exec) e inyecta TODO por env (cero secrets en el repo, D-0003):
#      AWS_EMULATOR_ENDPOINT / _ACCESS_KEY_ID / _SECRET_ACCESS_KEY / _CA_PATH + AWS_EC2_METADATA_DISABLED.
$root = Split-Path -Parent $PSScriptRoot
$secretsDir = Join-Path $root '.secrets'
$container = 'aws-emulator'

# PS 5.1: con $ErrorActionPreference='Stop', el stderr de un nativo (docker cp/build durante
# arranque en frío) lanza NativeCommandError terminante y mata el script aunque el exit code
# se chequee después -> EAP 'Continue' mientras hablamos con docker.
$script:eap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
# Portabilidad (CI en ubuntu/pwsh): curl.exe es de Windows; en Linux el binario es curl.
$curlBin = if (Get-Command curl.exe -ErrorAction SilentlyContinue) { 'curl.exe' } else { 'curl' }

$running = docker ps --filter "name=^$container$" --filter "status=running" --format "{{.Names}}"
if (-not $running) {
    $stopped = docker ps -a --filter "name=^$container$" --format "{{.Names}}"
    if ($stopped) {
        Write-Host "==> Arrancando contenedor $container existente..."
        docker start $container | Out-Null
    }
    else {
        Write-Host "==> Contenedor $container no existe: build + run (ver brain/evidence/2026-11-13-emulator-verification.md)..."
        docker build -t aws-emulator:verified (Join-Path $root 'aws-emulator') 2>$null | Out-Host
        docker run -d --name $container -p 9297:9297 aws-emulator:verified | Out-Null
    }
}

# --- CA de esta corrida (en frío el emulador la regenera: reintentar hasta 60 s) -------------
New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null
$caPath = Join-Path $secretsDir 'ca.pem'
for ($i = 0; $i -lt 60; $i++) {
    docker cp "${container}:/home/emulator/.aws/emulator/9297/ca.pem" $caPath 2>$null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 1
}
if ($LASTEXITCODE -ne 0) { throw "No se pudo copiar la CA del emulador (docker cp falló 60 veces)." }

# --- Health del emulador -----------------------------------------------------------------------
$health = $null
for ($i = 0; $i -lt 30; $i++) {
    $health = (& $curlBin -s --cacert $caPath https://localhost:9297/__admin/health) -join ''
    if ($health -eq 'ok') { break }
    Start-Sleep -Milliseconds 500
    # Reinicio de un contenedor existente: la CA vieja sigue en su disco hasta que el emulador
    # la regenera, y el cp de arriba pudo haberla copiado -> recopiar mientras el health falle.
    docker cp "${container}:/home/emulator/.aws/emulator/9297/ca.pem" $caPath 2>$null
}
if ($health -ne 'ok') { throw "El emulador no pasa el health check en https://localhost:9297 (última respuesta: '$health')." }

# --- Credenciales de esta corrida ---------------------------------------------------------------
$credsRaw = docker exec $container cat /home/emulator/.aws/emulator/9297/emulator_credentials
$accessKey = [regex]::Match($credsRaw, 'aws_access_key_id\s*=\s*(\S+)').Groups[1].Value
$secretKey = [regex]::Match($credsRaw, 'aws_secret_access_key\s*=\s*(\S+)').Groups[1].Value
if (-not $accessKey -or -not $secretKey) { throw "No se pudieron extraer las credenciales del emulador." }
$ErrorActionPreference = $script:eap # fin de la zona docker: de vuelta a fallo terminante

# --- Env del emulador (quedan en la sesión que dot-sourcea) -------------------------------------
$env:AWS_EMULATOR_ENDPOINT = 'https://127.0.0.1:9297'
$env:AWS_EMULATOR_ACCESS_KEY_ID = $accessKey
$env:AWS_EMULATOR_SECRET_ACCESS_KEY = $secretKey
$env:AWS_EMULATOR_CA_PATH = $caPath
$env:AWS_EC2_METADATA_DISABLED = 'true' # NFR-7: cero rutas hacia AWS real

Write-Host "==> Emulador OK (health). Credenciales y CA de esta corrida inyectadas por env (.secrets/ está gitignored)."
