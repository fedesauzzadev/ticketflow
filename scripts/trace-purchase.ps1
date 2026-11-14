# Traza distribuida de una compra (H-05 / N-2001 / D-0011): imprime, en orden
# cronológico UNIFICADO, los pasos de la saga de una compra a través de los 4
# servicios, filtrados por su correlation-id (== purchase_id).
#
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\trace-purchase.ps1 -PurchaseId pur-<32hex>
#   (con -OutFile <path> escribe el reporte completo a archivo UTF-8, inmune al codepage
#    de la consola — así se archiva la evidencia)
#
# Requiere: entorno levantado con scripts\run-local.ps1 (los logs viven en
# .artifacts\logs\<svc>.log; cada arranque de run-local los trunca). Tail en vivo:
#   Get-Content .artifacts\logs\orders.log -Wait
#
# Exit codes: 0 = traza OK y sin datos sensibles · 1 = sin líneas para ese id
#             2 = POSIBLE FUGA: PAN (13-19 dígitos) o CVV en la traza (AC2).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PurchaseId,
    [string]$LogsDir = '',
    # Reporte a archivo via .NET (UTF-8 BOM): inmune al codepage de la consola PS 5.1.
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
if ($LogsDir -eq '') { $LogsDir = Join-Path (Split-Path -Parent $PSScriptRoot) '.artifacts\logs' }

# Frontera validada (mismo contrato que la API): pur- + 32 hex.
if ($PurchaseId -notmatch '^pur-[0-9a-f]{32}$') {
    throw "PurchaseId inválido: debe tener formato 'pur-' + 32 hex (el purchase_id que responde POST /purchases)."
}
if (-not (Test-Path $LogsDir)) {
    throw "No existe '$LogsDir'. Levantá el entorno con scripts\run-local.ps1 (él redirige los logs de los 4 servicios ahí)."
}

# --- 1) Líneas de los 4 servicios que mencionan este correlation-id -------------------
# (*.log excluye *.err.log: la saga se narra en stdout; err.log es diagnóstico de arranque.)
$hits = @(Get-ChildItem (Join-Path $LogsDir '*.log') -File |
    Where-Object { $_.Name -notlike '*.err.log' } |
    ForEach-Object { Select-String -Path $_.FullName -Pattern $PurchaseId -SimpleMatch })

if ($hits.Count -eq 0) {
    Write-Warning "Sin líneas para '$PurchaseId' en $LogsDir. ¿Es de esta corrida? (run-local trunca los logs al arrancar)."
    exit 1
}

# --- 2) Parse JSON → timestamp ISO · servicio (del archivo: <svc>.log) · paso (Message) ---
$entries = @()
foreach ($m in $hits) {
    try {
        $json = $m.Line | ConvertFrom-Json
        $entries += [pscustomobject]@{
            Timestamp = [string]$json.Timestamp
            Servicio  = $m.Filename -replace '\.log$', ''   # orders/catalog/payments/ticketing
            Nivel     = [string]$json.LogLevel
            Mensaje   = [string]$json.Message
        }
    }
    catch {
        $entries += [pscustomobject]@{ Timestamp = ''; Servicio = ($m.Filename -replace '\.log$', ''); Nivel = '?'; Mensaje = $m.Line }
    }
}

# --- 3) Orden cronológico: Timestamp ISO "O" UTC → sort lexicográfico == sort temporal (D-0011).
$sorted = @($entries | Sort-Object -Property Timestamp -Culture ([Globalization.CultureInfo]::InvariantCulture))

# Reporte como string (se escribe por .NET UTF-8 con BOM si -OutFile: el codepage de la
# consola de PS 5.1 no representa acentos/flechas y corrompería la evidencia).
$report = @()
$report += "Traza de la compra $PurchaseId (correlation-id compartido, D-0011):"
$report += ($sorted | Format-Table Timestamp, Servicio, Nivel, Mensaje -AutoSize | Out-String -Width 4096).TrimEnd()
$report += ''
$resumen = ($sorted | Group-Object Servicio | Sort-Object Name | ForEach-Object { "$($_.Name): $($_.Count)" }) -join ' | '
$report += "Servicios en la traza: $resumen"
$report += "(comando crudo equivalente: Select-String `".artifacts\logs\*.log`" -Pattern '<purchase_id>' -SimpleMatch)"

# --- 4) Guardián AC2: la traza no puede contener PAN/CVV ------------------------------
# PAN = run de 13-19 dígitos (los ids pur-/tkt- son 32 hex → se enmascaran antes);
# CVV = la palabra cvv seguida de un valor de 3 dígitos. Los mensajes legítimos
# ("PAN terminada en 0002") no disparan el guardián: 0002 son 4 dígitos.
$raw = ($hits | ForEach-Object { $_.Line }) -join "`n"
$masked = [regex]::Replace($raw, '[0-9a-f]{32}', '<id>')
$pan = [regex]::Match($masked, '\d{13,19}')
$cvv = [regex]::Match($masked, '(?i)cvv\D{0,5}\d{3}')
if ($pan.Success -or $cvv.Success) {
    $report += "GUARDIAN AC2 FALLO: posible dato sensible en la traza -> " +
        $(if ($pan.Success) { "PAN? '$($pan.Value)'" }) + " " +
        $(if ($cvv.Success) { "CVV? cerca de 'cvv'" })
    if ($OutFile -ne '') { [IO.File]::WriteAllText($OutFile, ($report -join "`r`n"), (New-Object Text.UTF8Encoding $true)) }
    Write-Host "GUARDIAN AC2 FALLO - posible PAN/CVV (ver $OutFile)"
    exit 2
}
$report += 'Guardian AC2 OK: sin PAN/CVV en la traza.'

if ($OutFile -ne '') {
    # UTF-8 con BOM por .NET: la evidencia queda legible sin depender del codepage.
    [IO.File]::WriteAllText($OutFile, ($report -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $true))
    Write-Host "Traza OK ($($sorted.Count) lineas, servicios: $resumen). Reporte: $OutFile"
}
else {
    $report | Write-Host
}
exit 0
