# Demo de idempotencia observable de TicketFlow (H-04 AC2 / NFR-8).
# Requiere el entorno levantado: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
# Imprime: misma key => misma compra (mismo purchase_id); disponibilidad identica antes/despues
# del replay; misma key + body distinto => 422; evento agotado + key nueva => 422.
# Exit 0 = demo OK (el caso agotado se omite solo si ningun evento esta accesible para agotar).
# Compatible con PowerShell 5.1 (sin sintaxis PS7).
[CmdletBinding()]
param(
    [string]$CatalogUrl = 'http://localhost:5080',
    [string]$OrdersUrl  = 'http://localhost:5081'
)

$ErrorActionPreference = 'Stop'

function Get-Available([string]$EventId) {
    (Invoke-RestMethod -TimeoutSec 30 -Uri "$CatalogUrl/events/$EventId/availability").available
}

function New-PurchaseBody([string]$EventId, [int]$Seats, [string]$Holder) {
    @{ event_id = $EventId; seats = $Seats
       card = @{ pan = '4242424242424242'; holder = $Holder; expiry = '12/29'; cvv = '123' } } |
        ConvertTo-Json -Depth 4
}

# POST que NO explota en 4xx: devuelve @{ Status = [int]; Json = parsed }.
function Post-Purchase([string]$Key, [string]$Body) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 -Uri "$OrdersUrl/purchases" -Method Post `
            -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $Key } -Body $Body
        return @{ Status = [int]$r.StatusCode; Json = (ConvertFrom-Json $r.Content) }
    }
    catch {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return @{ Status = [int]$resp.StatusCode; Json = ($reader.ReadToEnd() | ConvertFrom-Json) }
    }
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
    Write-Host '=== Demo idempotencia (H-04 AC2) ==='

    Write-Host 'Paso 0: evento con mas disponibilidad'
    $events = Invoke-RestMethod -TimeoutSec 30 -Uri "$CatalogUrl/events"
    $evt = $events | Sort-Object available -Descending | Select-Object -First 1
    Assert-True ($null -ne $evt -and $evt.available -ge 1) "evento '$($evt.id)' con asientos ($($evt.available))"
    $body1 = New-PurchaseBody $evt.id 1 'Demo Idempotencia'
    $key = 'demo-idem-' + [guid]::NewGuid().ToString('N')

    Write-Host "Paso 1: compra con key $key"
    $availBefore = Get-Available $evt.id
    $r1 = Post-Purchase $key $body1
    Assert-True ($r1.Status -eq 201) "POST inicial -> 201 Created"
    $purchaseId = $r1.Json.purchase_id
    $done = Wait-Status $purchaseId 'TICKETS_ISSUED'
    $tickets1 = ($done.ticket_ids | Sort-Object) -join ','
    Write-Host "    compra $purchaseId TICKETS_ISSUED (ticket $($done.ticket_ids -join ', '))"

    $availAfter = Get-Available $evt.id
    Assert-True ($availAfter -eq ($availBefore - 1)) "disponibilidad tras la compra: $availBefore -> $availAfter (bajo exacto 1)"

    Write-Host 'Paso 2: REPLAY con la MISMA key y el MISMO body'
    $r2 = Post-Purchase $key $body1
    Assert-True ($r2.Status -eq 200) "replay -> $($r2.Status) OK (no 201: no se creo nada nuevo)"
    Assert-True ($r2.Json.purchase_id -eq $purchaseId) "misma key => MISMA compra ($($r2.Json.purchase_id))"
    Assert-True ($r2.Json.status -eq 'TICKETS_ISSUED') "estado replay: $($r2.Json.status)"
    $tickets2 = ($r2.Json.ticket_ids | Sort-Object) -join ','
    Assert-True ($tickets2 -eq $tickets1) "mismos tickets ($tickets2)"

    Write-Host 'Paso 3: disponibilidad antes/despues del replay'
    $availReplay = Get-Available $evt.id
    Assert-True ($availReplay -eq $availAfter) "disponibilidad identica: $availAfter -> $availReplay (delta 0, cero efectos nuevos)"

    Write-Host 'Paso 4: misma key + body DISTINTO (holder) => 422'
    $r3 = Post-Purchase $key (New-PurchaseBody $evt.id 1 'Otro Holder')
    Assert-True ($r3.Status -eq 422) "key reusada con body distinto -> 422"
    Assert-True ($r3.Json.type -eq 'idempotency-key-reused') "tipo: $($r3.Json.type) - $($r3.Json.detail)"

    Write-Host 'Paso 5: evento agotado + key nueva => 422 seats-unavailable'
    $events2 = Invoke-RestMethod -TimeoutSec 30 -Uri "$CatalogUrl/events"
    $soldOut = $events2 | Where-Object { $_.available -eq 0 } | Select-Object -First 1
    if (-not $soldOut) {
        $small = $events2 | Where-Object { $_.available -ge 1 -and $_.available -le 7 } |
            Sort-Object available | Select-Object -First 1
        if ($small) {
            Write-Host "    agotando '$($small.id)' (compra de sus $($small.available) asientos)..."
            $drain = Post-Purchase ('demo-drain-' + [guid]::NewGuid().ToString('N')) `
                (New-PurchaseBody $small.id $small.available 'Demo Agotado')
            Assert-True ($drain.Status -eq 201) "compra de vaciado -> 201"
            Wait-Status $drain.Json.purchase_id 'TICKETS_ISSUED' | Out-Null
            $soldOut = $small
        }
    }
    if ($soldOut) {
        $r4 = Post-Purchase ('demo-soldout-' + [guid]::NewGuid().ToString('N')) (New-PurchaseBody $soldOut.id 1 'Demo Agotado')
        Assert-True ($r4.Status -eq 422) "evento agotado + key nueva -> 422"
        Assert-True ($r4.Json.type -eq 'seats-unavailable') "tipo: $($r4.Json.type) - $($r4.Json.detail)"
    }
    else {
        Write-Host '    SKIP: no hay evento con <= 7 asientos para agotar. Para verlo, levantar el entorno'
        Write-Host '          con SEED_EVENTS incluyendo un evento de 1 asiento (variante documentada en el README).'
    }

    Write-Host ''
    Write-Host "RESUMEN: misma key => misma compra ($purchaseId); disponibilidad $availAfter -> $availReplay (identica);"
    Write-Host '         key reusada con body distinto => 422; agotado + key nueva => 422'
    Write-Host '=== DEMO IDEMPOTENCIA: OK ==='
    exit 0
}
catch {
    Write-Host "=== DEMO IDEMPOTENCIA: FALLO === $($_.Exception.Message)"
    Write-Host 'El entorno esta levantado? powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1'
    exit 1
}
