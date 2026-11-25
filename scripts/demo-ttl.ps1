# Demo del hold de asientos con TTL (S11 / H-09 / N-3003 / D-0017 + D-0018):
# narra CON VALORES el ciclo de vida completo de un carrito abandonado:
#   1. RETENCION    POST /purchases {hold:true} -> SEATS_RESERVED con expires_at visible,
#                   disponibilidad -1, y Pagos sin tocar (el pago NO se auto-emite).
#   2. VENCIMIENTO  se espera el TTL del servicio (HOLD_TTL_SECONDS, default 90 s).
#   3. LIBERACION   el sweeper de Órdenes expira la compra (EXPIRED + motivo) y libera el
#                   asiento: disponibilidad restaurada al valor inicial.
#   4. PAGO TARDIO  POST /purchases/{id}/payment -> 409 hold-expired, delta de disponibilidad 0.
#   5. REPLAY       mismo POST original con la MISMA Idempotency-Key -> la misma compra EXPIRED.
#   6. RE-COMPRA    un comprador nuevo (key distinta) se lleva el asiento liberado -> TICKETS_ISSUED.
# Requisitos: emulador + 4 servicios arriba (scripts\run-local.ps1). Corre con el TTL que
# hereden los servicios (default 90 s) — imprime el expires_at real que devuelve la API.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-ttl.ps1 [-EventId evt-mj-tribute-night]
# Reporte: .artifacts\logs\demo-ttl-report.md. Exit 0 solo si TODAS las comprobaciones pasan (AC8).
[CmdletBinding()]
param([string]$EventId = 'evt-mj-tribute-night')

$ErrorActionPreference = 'Stop'

# Comprar exige un access token de Cognito (D-0024).
. (Join-Path $PSScriptRoot 'auth.ps1')
$auth = "Bearer $(Get-DemoToken)"
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
    $args = @('-s', '-m', '30', '-X', $method, '-w', "`n%{http_code}", '-H', "Authorization: $auth")
    foreach ($k in $headers.Keys) { $args += @('-H', "${k}: $($headers[$k])") }
    $tmp = $null
    if ($body -ne $null) {
        # PS 5.1 pierde las comillas dobles de un JSON en la línea de comandos de un nativo:
        # el body viaja por archivo temporal y curl lo lee crudo con -d @file.
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
    $text = if ($raw.Count -gt 1) { ($raw[0..($raw.Count - 2)]) -join "`n" } else { '' }
    return @{ Code = $code; Body = $text }
}
function Availability($eventId) {
    $r = Http 'GET' "http://localhost:5080/events/$eventId/availability"
    if ($r.Code -ne '200') { throw "availability HTTP $($r.Code)" }
    (ConvertFrom-Json $r.Body).available
}
function GetPurchase($id) {
    $r = Http 'GET' "http://localhost:5081/purchases/$id"
    if ($r.Code -ne '200') { return $null }
    ConvertFrom-Json $r.Body
}
function HoldBody($eventId, $seats) {
    (@{ event_id = $eventId; seats = $seats; hold = $true; card = @{
        pan = '4111111111111111'; holder = 'Michael Fan'; expiry = '12/29'; cvv = '123' } } |
        ConvertTo-Json -Compress)
}

Emit "=== demo-ttl $(Get-Date -Format o) (evento $EventId) ==="

# --- disponibilidad inicial --------------------------------------------------------
$a0 = Availability $EventId
Emit "disponibilidad inicial: $a0"

# --- 1) RETENCION: compra en hold --------------------------------------------------
Emit ''
Emit '--- 1) retencion: POST /purchases con "hold": true ---'
$key = 'demo-ttl-' + [Guid]::NewGuid().ToString('N')
$body = HoldBody $EventId 1
$r = Http 'POST' 'http://localhost:5081/purchases' $body @{ 'Idempotency-Key' = $key }
Check 'retencion aceptada (201)' ($r.Code -eq '201') "HTTP $($r.Code): $($r.Body)"
$id = (ConvertFrom-Json $r.Body).purchase_id
Emit "compra: $id"

$hold = $null
foreach ($i in 1..60) { # hasta 30 s esperando la reserva asíncrona
    $p = GetPurchase $id
    if ($p -and $p.status -eq 'SEATS_RESERVED') { $hold = $p; break }
    Start-Sleep -Milliseconds 500
}
Check 'estado SEATS_RESERVED (no terminal)' ($hold -ne $null) "estado: $((GetPurchase $id).status)"
Check 'expiracion observable (expires_at)' ($hold -and $hold.expires_at) "expires_at: $($hold.expires_at)"
$expiresAt = if ($hold) { [DateTimeOffset]::Parse($hold.expires_at) } else { [DateTimeOffset]::UtcNow }

$a1 = Availability $EventId
Check 'asiento retenido (disponibilidad -1)' ($a1 -eq ($a0 - 1)) "disponibilidad: $a0 -> $a1"

# --- 2) + 3) VENCIMIENTO y LIBERACION ------------------------------------------------
Emit ''
Emit ("--- 2)+3) vencimiento (TTL del servicio) y liberacion: esperando hasta {0:HH:mm:ss} UTC ---" -f $expiresAt)
$expired = $null
$deadline = $expiresAt.AddSeconds(60) # TTL + 5 s de sweeper + margen
foreach ($i in 1..(([int](($deadline - [DateTimeOffset]::UtcNow).TotalSeconds)) * 2 + 2)) {
    $p = GetPurchase $id
    if ($p -and $p.status -eq 'EXPIRED') { $expired = $p; break }
    Start-Sleep -Milliseconds 500
}
Check 'estado terminal EXPIRED' ($expired -ne $null) "estado: $((GetPurchase $id).status)"
Check 'terminal con motivo' ($expired -and $expired.reason -like '*expirada sin pago*') "reason: $($expired.reason)"

# La liberación es asíncrona (outbox -> SQS -> Catálogo): se espera a que se refleje.
$a2 = -1
foreach ($i in 1..60) { # hasta 30 s de propagación de la compensación
    $a2 = Availability $EventId
    if ($a2 -eq $a0) { break }
    Start-Sleep -Milliseconds 500
}
Check 'asiento liberado (disponibilidad restaurada)' ($a2 -eq $a0) "disponibilidad: $a1 -> $a2 (inicial: $a0)"

# --- 4) PAGO TARDIO: 409 accionable --------------------------------------------------
Emit ''
Emit '--- 4) pago tardio: POST /purchases/{id}/payment tras el TTL ---'
$r = Http 'POST' "http://localhost:5081/purchases/$id/payment"
$type = ''
try { $type = (ConvertFrom-Json $r.Body).type } catch { }
Check 'rechazo 409 hold-expired' ($r.Code -eq '409' -and $type -eq 'hold-expired') "HTTP $($r.Code) type: $type ($($r.Body))"
$a3 = Availability $EventId
Check 'sin segunda liberacion (delta 0)' ($a3 -eq $a2) "disponibilidad: $a2 -> $a3"

# --- 5) REPLAY de la misma key: misma compra EXPIRED ---------------------------------
Emit ''
Emit '--- 5) replay del POST original (misma Idempotency-Key) ---'
$r = Http 'POST' 'http://localhost:5081/purchases' $body @{ 'Idempotency-Key' = $key }
$replay = $null
try { $replay = ConvertFrom-Json $r.Body } catch { }
Check 'replay devuelve la misma compra EXPIRED' ($r.Code -eq '200' -and $replay -and $replay.purchase_id -eq $id -and $replay.status -eq 'EXPIRED') "HTTP $($r.Code): $($replay.purchase_id) $($replay.status)"
$a4 = Availability $EventId
Check 'replay sin efectos (delta 0)' ($a4 -eq $a3) "disponibilidad: $a3 -> $a4"

# --- 6) RE-COMPRA con key nueva -------------------------------------------------------
Emit ''
Emit '--- 6) re-compra del asiento liberado (comprador nuevo, key distinta) ---'
$key2 = 'demo-ttl-' + [Guid]::NewGuid().ToString('N')
$body2 = (@{ event_id = $EventId; seats = 1; card = @{
    pan = '4111111111111111'; holder = 'Otro Fan'; expiry = '12/29'; cvv = '123' } } |
    ConvertTo-Json -Compress)
$r = Http 'POST' 'http://localhost:5081/purchases' $body2 @{ 'Idempotency-Key' = $key2 }
Check 're-compra aceptada (201)' ($r.Code -eq '201') "HTTP $($r.Code)"
$id2 = (ConvertFrom-Json $r.Body).purchase_id

$final = $null
foreach ($i in 1..120) { # hasta 60 s de saga
    $p = GetPurchase $id2
    if ($p -and $p.status -in 'TICKETS_ISSUED', 'FAILED') { $final = $p; break }
    Start-Sleep -Milliseconds 500
}
Check 're-compra TICKETS_ISSUED' ($final -and $final.status -eq 'TICKETS_ISSUED') "estado: $($final.status) tickets: $($final.ticket_ids -join ', ')"
$a5 = Availability $EventId
Check 'disponibilidad final == inicial - 1' ($a5 -eq ($a0 - 1)) "disponibilidad: $a5 (inicial $a0 - 1 emitido)"

# --- reporte ---------------------------------------------------------------------------
Emit ''
if ($failed.Count -eq 0) {
    Emit 'RESULTADO: TODO PASS (retencion -> vencimiento -> liberacion -> pago tardio 409 -> replay -> re-compra)'
    $exit = 0
}
else {
    Emit ("RESULTADO: FALLARON: {0}" -f ($failed -join ', '))
    $exit = 1
}
$outPath = Join-Path $logsDir 'demo-ttl-report.md'
[IO.File]::WriteAllText($outPath, ($report -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($true)))
Write-Host "reporte: $outPath"
exit $exit
