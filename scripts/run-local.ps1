# Levanta el entorno local de TicketFlow (S02): emulador AWS + los 4 servicios.
#   1. Emulador caído -> lo arranca (contenedor existente o build+run documentado).
#   2. Extrae credenciales + CA de la corrida ACTUAL del emulador (se regeneran por arranque).
#   3. Build (0 warnings exigido por Directory.Build.props).
#   4. Inyecta todo por env (cero secrets en el repo, D-0003) y corre los 4 servicios:
#      Catálogo :5080 · Órdenes :5081 · Pagos :5082 · Ticketing :5083 (D-0008).
#      Logs de cada servicio a .artifacts\logs\<svc>.log (+ <svc>.err.log) — trazas
#      filtrables por compra: scripts\trace-purchase.ps1 -PurchaseId <id> (D-0011).
#      Tail en vivo: Get-Content .artifacts\logs\orders.log -Wait. Cada arranque
#      trunca los logs (Start-Process redirect sobrescribe).
# Ctrl+C detiene el script Y los 4 servicios (finally).
# Requisitos: Docker Desktop + .NET 10 SDK. Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$secretsDir = Join-Path $root '.secrets'
$container = 'aws-emulator'

$services = @(
    @{ Name = 'Catálogo';  Log = 'catalog';   Project = 'src/Services/Catalog/TicketFlow.Catalog.Api';       Port = 5080; Probe = '/events' },
    @{ Name = 'Órdenes';   Log = 'orders';    Project = 'src/Services/Orders/TicketFlow.Orders.Api';         Port = 5081; Probe = '/health' },
    @{ Name = 'Pagos';     Log = 'payments';  Project = 'src/Services/Payments/TicketFlow.Payments.Api';     Port = 5082; Probe = '/health' },
    @{ Name = 'Ticketing'; Log = 'ticketing'; Project = 'src/Services/Ticketing/TicketFlow.Ticketing.Api';   Port = 5083; Probe = '/health' }
)

# --- 1) Emulador arriba ------------------------------------------------------
# PS 5.1: con $ErrorActionPreference='Stop', el stderr de un nativo (docker cp/build
# durante arranque en frío) lanza NativeCommandError terminante y mata el script
# aunque el exit code se chequee después -> EAP 'Continue' mientras hablamos con docker.
$eap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

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

# --- 2) Credenciales + CA de ESTA corrida -------------------------------------
New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null
$caPath = Join-Path $secretsDir 'ca.pem'
# En frío el emulador regenera CA+credenciales al arrancar: reintentar hasta 60 s.
for ($i = 0; $i -lt 60; $i++) {
    docker cp "${container}:/home/emulator/.aws/emulator/9297/ca.pem" $caPath 2>$null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 1
}
if ($LASTEXITCODE -ne 0) { throw "No se pudo copiar la CA del emulador (docker cp falló 60 veces)." }

$health = $null
for ($i = 0; $i -lt 30; $i++) {
    $health = (curl.exe -s --cacert $caPath https://localhost:9297/__admin/health) -join ''
    if ($health -eq 'ok') { break }
    Start-Sleep -Milliseconds 500
}
if ($health -ne 'ok') { throw "El emulador no pasa el health check en https://localhost:9297 (última respuesta: '$health')." }

$credsRaw = docker exec $container cat /home/emulator/.aws/emulator/9297/emulator_credentials
$accessKey = [regex]::Match($credsRaw, 'aws_access_key_id\s*=\s*(\S+)').Groups[1].Value
$secretKey = [regex]::Match($credsRaw, 'aws_secret_access_key\s*=\s*(\S+)').Groups[1].Value
if (-not $accessKey -or -not $secretKey) { throw "No se pudieron extraer las credenciales del emulador." }
$ErrorActionPreference = $eap # fin de la zona docker: de vuelta a fallo terminante

# --- 3) Env común + build -----------------------------------------------------
$env:AWS_EMULATOR_ENDPOINT = 'https://127.0.0.1:9297'
$env:AWS_EMULATOR_ACCESS_KEY_ID = $accessKey
$env:AWS_EMULATOR_SECRET_ACCESS_KEY = $secretKey
$env:AWS_EMULATOR_CA_PATH = $caPath
$env:AWS_EC2_METADATA_DISABLED = 'true' # NFR-7: cero rutas hacia AWS real
if (-not $env:SEED_EVENTS) {
    $env:SEED_EVENTS = 'evt-mj-tribute-night|Michael Jackson Tribute Night|2027-03-12T21:00:00Z|120.50|500;evt-legacy-tour|Legacy World Tour (Orquestal)|2027-04-03T20:00:00Z|85.00|250'
}

Write-Host "==> Emulador OK (health). Credenciales y CA de esta corrida inyectadas por env (.secrets/ está gitignored)."
Write-Host '==> Build de la solución (0 warnings exigido)...'
dotnet build (Join-Path $root 'TicketFlow.sln') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build falló.' }

# --- 4) Los 4 servicios --------------------------------------------------------
# Logs a archivo (D-0011): PS 5.1 no permite redirigir stdout y stderr al mismo
# archivo → .log (JSON de la saga) + .err.log (diagnóstico). .artifacts/ gitignored.
$logsDir = Join-Path $root '.artifacts\logs'
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
$procs = @()
try {
    foreach ($s in $services) {
        $env:SERVICE_PORT = "$($s.Port)" # cada hijo captura su propio puerto al iniciar
        Write-Host "==> $($s.Name) en http://localhost:$($s.Port) (logs: .artifacts\logs\$($s.Log).log)"
        # PS 5.1: ArgumentList como array NO cita paths con espacios (repo "MichaelJackson AWS")
        # → string único con las comillas explícitas.
        $project = Join-Path $root $s.Project
        $procs += Start-Process -FilePath 'dotnet' `
            -ArgumentList ('run --project "{0}" --no-build' -f $project) `
            -RedirectStandardOutput (Join-Path $logsDir "$($s.Log).log") `
            -RedirectStandardError (Join-Path $logsDir "$($s.Log).err.log") `
            -PassThru -NoNewWindow
    }
    $env:SERVICE_PORT = '5080' # restauro el default para no dejar env raro en la sesión

    foreach ($s in $services) {
        $ready = $false
        for ($i = 0; $i -lt 60; $i++) {
            # NUL = dispositivo nulo de Windows (PS 5.1: -o $null pasa un argumento vacío y rompe curl).
            curl.exe -s -o NUL "http://localhost:$($s.Port)$($s.Probe)"
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw "$($s.Name) no responde en :$($s.Port)$($s.Probe) tras 30 s." }
    }

    Write-Host ''
    Write-Host '==> Todo listo:'
    Write-Host '    GET  http://localhost:5080/events                       (catálogo, header X-Cache en /availability)'
    Write-Host '    POST http://localhost:5081/purchases                    (header Idempotency-Key; body {event_id,seats,card{pan,holder,expiry,cvv}})'
    Write-Host '    GET  http://localhost:5081/purchases/{id}               (estados de la saga: PENDING..TICKETS_ISSUED/FAILED)'
    Write-Host '    GET  http://localhost:5082/payments/{purchase_id}       (evidencia del paso de pago)'
    Write-Host '    GET  http://localhost:5083/tickets/{id}                 (entrada + qr_s3_key)'
    Write-Host '    PAN aprobada: cualquier terminación EXCEPTO 0002 · PAN rechazada: termina 0002 (compensación)'
    Write-Host '==> Traza de una compra (H-05): scripts\trace-purchase.ps1 -PurchaseId <purchase_id>  ·  tail en vivo: Get-Content .artifacts\logs\orders.log -Wait'
    Write-Host '==> Ctrl+C detiene los 4 servicios.'
    Wait-Process -Id ($procs.Id)
}
finally {
    foreach ($p in $procs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
