# Demo de resiliencia observable (S06 / H-06 / N-2002 / D-0012):
# el evaluador inyecta fallos por si mismo y comprueba:
#   AC1  fallo transitorio  -> saga sobrevive con >=2 reintentos y backoff creciente visible.
#   AC2  fallo persistente  -> mensaje en DLQ inspeccionable + compra FAILED con motivo.
#   AC3  post-recuperacion  -> disponibilidad consistente (sin reservas huerfanas / drift).
#   AC4  downstream colgado (docker pause) -> circuit breaker: fail-fast << 10 s, apertura
#        logueada; al recuperar -> cierre logueado y una compra nueva completa la saga.
#   AC5  invariante final: disponibilidad == inicial - asientos efectivamente emitidos.
# Requisitos: emulador + 4 servicios arriba (scripts\run-local.ps1) y docker.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-resilience.ps1 [-EventId evt-mj-tribute-night]
# Reporte: .artifacts\logs\demo-resilience-report.md (UTF-8 BOM). Exit 0 = todo OK.
[CmdletBinding()]
param([string]$EventId = 'evt-mj-tribute-night')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logsDir = Join-Path $root '.artifacts\logs'
$report = @()
$failed = @()

function Emit($line) {
    Write-Host $line
    $script:report += $line
}
function Check($name, $ok, $detail) {
    if ($ok) { Emit ("[PASS] {0} - {1}" -f $name, $detail) }
    else {
        Emit ("[FAIL] {0} - {1}" -f $name, $detail)
        $script:failed += $name
    }
}
function Http($method, $url, $body = $null, $headers = @{}) {
    $args = @('-s', '-m', '30', '-X', $method, '-w', "`n%{http_code}")
    foreach ($k in $headers.Keys) { $args += @('-H', "${k}: $($headers[$k])") }
    $tmp = $null
    if ($body -ne $null) {
        # PS 5.1 pierde/quiebra las comillas dobles de un JSON en la linea de comandos de un
        # nativo (exit 3): el body via por archivo temporal y curl lo lee crudo con -d @file.
        $tmp = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($tmp, $body)
        $args += @('-H', 'Content-Type: application/json', '-d', "@$tmp")
    }
    try {
        $raw = & curl.exe @args $url
    }
    finally {
        if ($tmp) { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
    $code = [string]($raw | Select-Object -Last 1)
    # PS 5.1: sin -SkipLast; el body es todo menos la ultima linea (el http_code).
    $text = if ($raw.Count -gt 1) { ($raw[0..($raw.Count - 2)]) -join "`n" } else { '' }
    return @{ Code = $code; Body = $text }
}
function Availability($eventId) {
    $r = Http 'GET' "http://localhost:5080/events/$eventId/availability"
    if ($r.Code -ne '200') { throw "availability HTTP $($r.Code)" }
    (ConvertFrom-Json $r.Body).available
}
function Buy($eventId, $seats) {
    $key = 'demo-res-' + [Guid]::NewGuid().ToString('N')
    $body = (@{ event_id = $eventId; seats = $seats; card = @{
        pan = '4111111111111111'; holder = 'Michael Fan'; expiry = '12/29'; cvv = '123' } } |
        ConvertTo-Json -Compress)
    $r = Http 'POST' 'http://localhost:5081/purchases' $body @{ 'Idempotency-Key' = $key }
    if ($r.Code -ne '201') { throw "compra HTTP $($r.Code): $($r.Body)" }
    (ConvertFrom-Json $r.Body).purchase_id
}
function PurchaseStatus($id) {
    $r = Http 'GET' "http://localhost:5081/purchases/$id"
    if ($r.Code -ne '200') { return $null }
    ConvertFrom-Json $r.Body
}
function WaitTerminal($id, $timeoutSec) {
    foreach ($i in 1..($timeoutSec * 2)) {
        $p = PurchaseStatus $id
        if ($p -and $p.status -in 'TICKETS_ISSUED', 'FAILED') { return $p }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
# Lineas de log de un servicio para un purchase id (JSON D-0011: .Timestamp / .Message).
function TraceLines($svc, $purchaseId, $pattern) {
    $hits = @()
    $file = Join-Path $logsDir "$svc.log"
    if (-not (Test-Path $file)) { return $hits }
    # El servicio mantiene el .log abierto (redirect de run-local): lectura con share ReadWrite.
    $fs = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $sr = New-Object IO.StreamReader($fs)
        while ($null -ne ($line = $sr.ReadLine())) {
            if ($line -notlike "*$purchaseId*") { continue }
            try { $j = $line | ConvertFrom-Json } catch { continue }
            if ($j.Message -and $j.Message -like $pattern) {
                $hits += @{ Timestamp = [DateTimeOffset]::Parse($j.Timestamp); Message = $j.Message }
            }
        }
    }
    finally { $fs.Dispose() }
    $hits
}

# Log de un servicio contiene un texto (lectura compartida: el servicio lo mantiene abierto).
function LogContains($svc, $text) {
    $file = Join-Path $logsDir "$svc.log"
    if (-not (Test-Path $file)) { return $false }
    $fs = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $sr = New-Object IO.StreamReader($fs)
        while ($null -ne ($line = $sr.ReadLine())) {
            if ($line -like "*$text*") { return $true }
        }
    }
    finally { $fs.Dispose() }
    return $false
}

Emit "=== demo-resilience $(Get-Date -Format o) (evento $EventId) ==="

# --- disponibilidad inicial ------------------------------------------------------
$a0 = Availability $EventId
Emit "disponibilidad inicial: $a0"

# --- AC1: fallo transitorio (2 intentos) -> sobrevive con backoff visible ---------
Emit ''
Emit '--- AC1: fallo transitorio en authorize-payment (count=2) ---'
$r = Http 'POST' 'http://localhost:5082/admin/failure' '{"step":"authorize-payment","mode":"transient","count":2}'
Check 'AC1 inyeccion aceptada' ($r.Code -eq '201') "HTTP $($r.Code): $($r.Body)"

$pa = Buy $EventId 1
Emit "compra A: $pa (1 asiento)"
$p = WaitTerminal $pa 40
Check 'AC1 saga sobrevive' ($p -and $p.status -eq 'TICKETS_ISSUED') "estado: $($p.status) motivo: $($p.reason)"

$retries = TraceLines 'payments' $pa '*intento ?/3*'
$retries = @($retries | Sort-Object { $_.Timestamp })
if ($retries.Count -ge 2) {
    $gapMs = [math]::Round(($retries[1].Timestamp - $retries[0].Timestamp).TotalMilliseconds)
    Emit ("reintentos en payments.log: {0}" -f (($retries | ForEach-Object { "{0}  {1}" -f $_.Timestamp.ToString('HH:mm:ss.fff'), $_.Message }) -join ' | '))
    Check 'AC1 backoff creciente' ($gapMs -ge 450) "espera entre intento 1 y 2: $gapMs ms (>=450 esperado)"
} else {
    Check 'AC1 backoff creciente' $false "solo $($retries.Count) lineas de reintento en payments.log"
}
$dlqA = Http 'GET' 'http://localhost:5082/admin/dlq'
Check 'AC1 sin DLQ' ($dlqA.Body -notlike "*$pa*") 'el mensaje transitorio no fue a la DLQ'

# --- AC2: fallo persistente -> DLQ + terminal con motivo ---------------------------
Emit ''
Emit '--- AC2: fallo persistente en authorize-payment ---'
$r = Http 'POST' 'http://localhost:5082/admin/failure' '{"step":"authorize-payment","mode":"persistent"}'
Check 'AC2 inyeccion aceptada' ($r.Code -eq '201') "HTTP $($r.Code)"

$pb = Buy $EventId 2
Emit "compra B: $pb (2 asientos)"
$p = WaitTerminal $pb 40
Check 'AC2 terminal FAILED con motivo' ($p -and $p.status -eq 'FAILED' -and $p.reason -like '*authorize-payment*') "estado: $($p.status) motivo: $($p.reason)"

$dlq = Http 'GET' 'http://localhost:5082/admin/dlq'
Check 'AC2 mensaje en DLQ inspeccionable' ($dlq.Code -eq '200' -and $dlq.Body -like "*$pb*") "GET /admin/dlq (payments) contiene la entrega fallida"

# --- AC3: recuperacion -> sin limbo ------------------------------------------------
Emit ''
Emit '--- AC3: downstream recuperado -> disponibilidad consistente ---'
$r = Http 'DELETE' 'http://localhost:5082/admin/failure/authorize-payment'
Check 'AC3 regla borrada' ($r.Code -eq '204') "DELETE HTTP $($r.Code)"
$a3 = Availability $EventId
# A emitio 1 asiento; B reservo 2 y la compensacion los devolvio (net 0).
Check 'AC3 sin drift' ($a3 -eq ($a0 - 1)) "disponibilidad $a3 == inicial $a0 - 1 (B compensado, sin huerfanos)"

# --- AC4: downstream colgado -> circuit breaker ------------------------------------
Emit ''
Emit '--- AC4: docker pause del emulador -> circuit breaker ---'
$eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
docker pause aws-emulator | Out-Null
Emit "emulador pausado: $(docker ps --filter name=aws-emulator --format '{{.Status}}')"

foreach ($i in 1..3) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $null = Http 'GET' 'http://localhost:5080/events'
    $sw.Stop()
    Emit ("  /events #$i tras pause: {0} ms (timeout de 10 s consumiendose)" -f [math]::Round($sw.Elapsed.TotalMilliseconds))
}
$sw = [Diagnostics.Stopwatch]::StartNew()
$fast = Http 'GET' 'http://localhost:5080/events'
$sw.Stop()
$fastMs = [math]::Round($sw.Elapsed.TotalMilliseconds)
Check 'AC4 fail-fast << timeout' ($sw.Elapsed.TotalMilliseconds -lt 2000) "4a llamada: HTTP $($fast.Code) en $fastMs ms (las anteriores ~10000)"

$openLogged = LogContains 'catalog' 'circuit breaker de dynamodb OPEN'
Check 'AC4 apertura logueada' $openLogged "'circuit breaker de dynamodb OPEN' en catalog.log"

docker unpause aws-emulator | Out-Null
$ErrorActionPreference = $eap
Emit 'emulador reanudado; esperando probe del breaker...'

$recovered = $false
foreach ($i in 1..60) {
    $r = Http 'GET' 'http://localhost:5080/events'
    if ($r.Code -eq '200') { $recovered = $true; break }
    Start-Sleep -Milliseconds 500
}
Check 'AC4 breaker cierra al recuperar' $recovered 'GET /events vuelve a 200 (probe exitoso)'
$closedLogged = LogContains 'catalog' 'circuit breaker de dynamodb CLOSED'
Check 'AC4 cierre logueado' $closedLogged "'circuit breaker de dynamodb CLOSED' en catalog.log"

# una compra NUEVA completa la saga tras la recuperacion (los breakers de los 4 servicios se reponen)
$pc = Buy $EventId 1
Emit "compra C: $pc (1 asiento)"
$p = WaitTerminal $pc 90
Check 'AC4 saga nueva completa' ($p -and $p.status -eq 'TICKETS_ISSUED') "estado: $($p.status)"

# --- AC5: invariante final -----------------------------------------------------------
Emit ''
Emit '--- AC5: invariante de disponibilidad ---'
$af = Availability $EventId
Check 'AC5 disponibilidad == inicial - emitidos' ($af -eq ($a0 - 2)) "final $af == inicial $a0 - 2 emitidos (A=1, C=1)"

# --- reporte ------------------------------------------------------------------------
Emit ''
if ($failed.Count -eq 0) {
    Emit 'RESULTADO: TODO PASS (AC1..AC5)'
    $exit = 0
} else {
    Emit ("RESULTADO: FALLARON: {0}" -f ($failed -join ', '))
    $exit = 1
}
$outPath = Join-Path $logsDir 'demo-resilience-report.md'
[IO.File]::WriteAllText($outPath, ($report -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($true)))
Write-Host "reporte: $outPath"
exit $exit
