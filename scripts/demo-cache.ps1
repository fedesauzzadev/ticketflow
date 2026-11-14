# Demo del cache-aside observable de TicketFlow (H-04 AC3 / NFR-8).
# Requiere el entorno levantado: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
# Imprime MISS -> HIT -> invalidacion por compra -> MISS con valores. Exit 0 = demo OK.
# Compatible con PowerShell 5.1 (sin sintaxis PS7).
[CmdletBinding()]
param(
    [string]$CatalogUrl = 'http://localhost:5080',
    [string]$OrdersUrl  = 'http://localhost:5081'
)

$ErrorActionPreference = 'Stop'

function Get-Header([object]$Response, [string]$Name) {
    foreach ($k in $Response.Headers.Keys) {
        if ("$k" -ieq $Name) { return @($Response.Headers[$k])[0] }
    }
    return $null
}

function Get-Availability([string]$EventId) {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri "$CatalogUrl/events/$EventId/availability" -Method Get
}

function New-PurchaseBody([string]$EventId, [int]$Seats, [string]$Holder) {
    @{ event_id = $EventId; seats = $Seats
       card = @{ pan = '4242424242424242'; holder = $Holder; expiry = '12/29'; cvv = '123' } } |
        ConvertTo-Json -Depth 4
}

function Post-Purchase([string]$Key, [string]$Body) {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri "$OrdersUrl/purchases" -Method Post `
        -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $Key } -Body $Body
}

function Wait-Status([string]$PurchaseId, [string]$Expected, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Invoke-RestMethod -TimeoutSec 30 -Uri "$OrdersUrl/purchases/$PurchaseId"
        if ($p.status -eq $Expected) { return $p }
        if ($p.status -eq 'FAILED') { throw "la compra $PurchaseId termino FAILED: $($p.reason)" }
        Start-Sleep -Milliseconds 250
    }
    throw "la compra $PurchaseId no llego a $Expected en $TimeoutSec s"
}

function Assert-True([bool]$Cond, [string]$What) {
    if (-not $Cond) { throw "CHECK FALLADO: $What" }
    Write-Host "    OK: $What"
}

try {
    Write-Host '=== Demo cache-aside (H-04 AC3) ==='

    Write-Host 'Paso 0: evento con mas disponibilidad (la compra del Paso 3 necesita >= 1 asiento)'
    $events = Invoke-RestMethod -TimeoutSec 30 -Uri "$CatalogUrl/events"
    $evt = $events | Sort-Object available -Descending | Select-Object -First 1
    Assert-True ($null -ne $evt) 'hay eventos en el catalogo'
    Assert-True ($evt.available -ge 1) "evento '$($evt.id)' tiene asientos ($($evt.available))"

    Write-Host 'Paso 1: reset del cache (endpoint interno idempotente: garantiza el primer MISS sin gastar asientos)'
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Method Post -Uri "$CatalogUrl/internal/events/$($evt.id)/invalidate-cache" | Out-Null

    Write-Host 'Paso 2: 1a lectura de disponibilidad -> debe ser MISS'
    $r1 = Get-Availability $evt.id
    $c1 = Get-Header $r1 'X-Cache'
    $v1 = (ConvertFrom-Json $r1.Content).available
    Assert-True ($c1 -eq 'MISS') "1a lectura X-Cache: $c1 (esperaba MISS)"
    Write-Host "    disponible = $v1"

    Write-Host 'Paso 3: 2a lectura -> debe ser HIT con el MISMO valor'
    $r2 = Get-Availability $evt.id
    $c2 = Get-Header $r2 'X-Cache'
    $v2 = (ConvertFrom-Json $r2.Content).available
    Assert-True ($c2 -eq 'HIT') "2a lectura X-Cache: $c2 (esperaba HIT)"
    Assert-True ($v2 -eq $v1) "valor en HIT identico al de MISS ($v2 == $v1)"

    Write-Host 'Paso 4: compra de 1 asiento (la reserva en la saga invalida el cache)'
    $key = 'demo-cache-' + [guid]::NewGuid().ToString('N')
    $resp = Post-Purchase $key (New-PurchaseBody $evt.id 1 'Demo Cache')
    Assert-True ($resp.StatusCode -eq 201) "POST /purchases -> 201"
    $purchase = (ConvertFrom-Json $resp.Content)
    $done = Wait-Status $purchase.purchase_id 'TICKETS_ISSUED'
    Write-Host "    compra $($purchase.purchase_id) TICKETS_ISSUED (ticket $($done.ticket_ids -join ', '))"

    Write-Host 'Paso 5: 3a lectura -> debe ser MISS con el valor NUEVO (invalidacion observable)'
    $r3 = Get-Availability $evt.id
    $c3 = Get-Header $r3 'X-Cache'
    $v3 = (ConvertFrom-Json $r3.Content).available
    Assert-True ($c3 -eq 'MISS') "3a lectura X-Cache: $c3 (esperaba MISS: la compra invalido)"
    Assert-True ($v3 -eq ($v1 - 1)) "valor nuevo = $v3 (esperaba $($v1 - 1): bajo exacto 1)"

    Write-Host 'Paso 6: 4a lectura -> HIT cacheando ya el valor nuevo'
    $r4 = Get-Availability $evt.id
    $c4 = Get-Header $r4 'X-Cache'
    $v4 = (ConvertFrom-Json $r4.Content).available
    Assert-True ($c4 -eq 'HIT') "4a lectura X-Cache: $c4 (esperaba HIT)"
    Assert-True ($v4 -eq $v3) "valor en HIT = $v4 (== $v3)"

    Write-Host ''
    Write-Host "RESUMEN: MISS($v1) -> HIT($v1) -> compra de 1 asiento -> MISS($v3) -> HIT($v3)"
    Write-Host '         la lectura post-compra NO devolvio el valor cacheado: invalidacion demostrada'
    Write-Host '=== DEMO CACHE: OK ==='
    exit 0
}
catch {
    Write-Host "=== DEMO CACHE: FALLO === $($_.Exception.Message)"
    Write-Host 'El entorno esta levantado? powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1'
    exit 1
}
