# Stress test concurrente de TicketFlow (H-07 / N-2003 / D-0014).
# N compradores simultaneos (POST /purchases con Idempotency-Key unica, 1 asiento,
# PAN aprobada) sobre un evento con X asientos. Verifica con salida imprimible:
#   AC1  exactamente min(N,X) compras TICKETS_ISSUED y N-min(N,X) rechazos explicitos
#        (422 seats-unavailable o FAILED con motivo) - cero oversell.
#   AC2  la disponibilidad nunca es negativa (muestreo durante la corrida) y el valor
#        final es exactamente X - emitidas.
#   AC3  el 100% de las compras queda en estado terminal dentro del plazo
#        (-DeadlineSeconds, default 120 s; saga p95 ~0,8 s => margen ~x150).
#   AC4  invariante de cierre: disponibilidad_final + tickets_emitidos == X.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stress-test.ps1 -N 20 [-X 5] [-EventId evt-...] [-DeadlineSeconds 120]
# Semantica de X: por defecto X = disponibilidad del evento AL INICIO de la corrida. Con -X
# se alinea pre-comprando (A-X) asientos en secuencia (compras aprobadas de hasta 8 asientos,
# esperando cada una a TICKETS_ISSUED). Si X > disponibilidad actual => exit 2: no hay
# endpoint de creacion de eventos y no se fabrica disponibilidad.
# Exit codes: 0 = PASS (invariantes OK) | 1 = FAIL (invariante rota o error) | 2 = config invalida.
# Requiere el entorno levantado: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
# Compatible con PowerShell 5.1 (sin sintaxis PS7). La concurrencia y el HTTP viven en un helper
# Add-Type (HttpClient compartido, L-010: Invoke-WebRequest mide el cliente, no el servidor).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$N,
    [int]$X = -1,
    [string]$EventId = '',
    [int]$DeadlineSeconds = 120,
    [string]$CatalogUrl = 'http://localhost:5080',
    [string]$OrdersUrl = 'http://localhost:5081'
)

$ErrorActionPreference = 'Stop'

# --- validacion de entrada (exit 2 = config) ----------------------------------
if ($N -lt 1) { Write-Host 'CONFIG: N debe ser >= 1.'; exit 2 }
if ($X -lt -1) { Write-Host 'CONFIG: X debe ser >= 0 (o omitirse).'; exit 2 }
if ($DeadlineSeconds -lt 10) { Write-Host 'CONFIG: DeadlineSeconds debe ser >= 10.'; exit 2 }

# --- helper C#: HttpClient compartido + carrera N-POSTs (D-0014, L-010) --------
# C# 5 puro (PS 5.1 compila con CodeDom). Nada de interpolacion ni null-conditionals.
Add-Type -AssemblyName System.Net.Http
$helper = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TicketFlowStress
{
    public class BuyerResult
    {
        public int Index;
        public string Key;
        public string PurchaseId;
        public int HttpStatus;
        public string Body;
        public string Error;
    }

    public class RaceResult
    {
        public BuyerResult[] Buyers;
        public int[] Samples;
        public long RaceMs;
    }

    public static class StressClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // .NET Framework serializa por host a DefaultConnectionLimit (2 por defecto):
            // sin esto, N compradores "simultaneos" hacen cola de a 2 y no hay carrera real.
            ServicePointManager.DefaultConnectionLimit = 256;
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.Expect100Continue = false;
            HttpClientHandler h = new HttpClientHandler();
            h.UseProxy = false; // L-010: sin auto-deteccion de proxy (mide el servidor, no el cliente)
            HttpClient c = new HttpClient(h);
            c.Timeout = TimeSpan.FromSeconds(30);
            return c;
        }

        public static int GetAvailability(string catalogUrl, string eventId)
        {
            HttpResponseMessage r = Http.GetAsync(catalogUrl + "/events/" + eventId + "/availability").GetAwaiter().GetResult();
            string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode)
            {
                throw new Exception("GET availability -> " + (int)r.StatusCode + " " + body);
            }
            Match m = Regex.Match(body, "\"available\"\\s*:\\s*(-?\\d+)");
            if (!m.Success)
            {
                throw new Exception("respuesta de disponibilidad sin campo available: " + body);
            }
            return int.Parse(m.Groups[1].Value);
        }

        public static string GetPurchase(string ordersUrl, string purchaseId)
        {
            HttpResponseMessage r = Http.GetAsync(ordersUrl + "/purchases/" + purchaseId).GetAwaiter().GetResult();
            string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode)
            {
                throw new Exception("GET purchase " + purchaseId + " -> " + (int)r.StatusCode + " " + body);
            }
            return body;
        }

        public static BuyerResult PostPurchase(string ordersUrl, string eventId, string key, int seats, string pan, string holder)
        {
            BuyerResult b = new BuyerResult();
            b.Key = key;
            b.HttpStatus = 0;
            b.Body = "";
            b.Error = null;
            string json = string.Format(
                "{{\"event_id\":\"{0}\",\"seats\":{1},\"card\":{{\"pan\":\"{2}\",\"holder\":\"{3}\",\"expiry\":\"12/29\",\"cvv\":\"123\"}}}}",
                new object[] { eventId, seats, pan, holder });
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, ordersUrl + "/purchases");
                req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                HttpResponseMessage resp = Http.SendAsync(req).GetAwaiter().GetResult();
                b.HttpStatus = (int)resp.StatusCode;
                b.Body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                b.Error = ex.Message;
            }
            return b;
        }

        public static RaceResult RunRace(string ordersUrl, string catalogUrl, string eventId, int n, string pan)
        {
            RaceResult result = new RaceResult();
            List<int> samples = new List<int>();
            CancellationTokenSource done = new CancellationTokenSource();
            Stopwatch sw = Stopwatch.StartNew();

            // muestreo de disponibilidad en background mientras vuelan los N POSTs (AC2)
            Task sampler = Task.Run(delegate
            {
                while (!done.IsCancellationRequested)
                {
                    try { samples.Add(GetAvailability(catalogUrl, eventId)); }
                    catch (Exception) { }
                    Thread.Sleep(150);
                }
            });

            Task<BuyerResult>[] tasks = new Task<BuyerResult>[n];
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(delegate
                {
                    return PostPurchase(ordersUrl, eventId, "stress-" + Guid.NewGuid().ToString("N"), 1, pan, "Stress Buyer " + idx);
                });
            }
            Task.WaitAll(tasks);
            sw.Stop();
            done.Cancel();
            try { sampler.Wait(5000); } catch (Exception) { }

            BuyerResult[] buyers = new BuyerResult[n];
            for (int i = 0; i < n; i++)
            {
                BuyerResult b = tasks[i].Result;
                b.Index = i;
                buyers[i] = b;
            }
            result.Buyers = buyers;
            result.Samples = samples.ToArray();
            result.RaceMs = sw.ElapsedMilliseconds;
            return result;
        }
    }
}
'@
Add-Type -TypeDefinition $helper -ReferencedAssemblies @('System', 'System.Core', 'System.Net.Http')

$fails = New-Object System.Collections.Generic.List[string]
function Check([bool]$Ok, [string]$What) {
    if ($Ok) { Write-Host "    OK:   $What" }
    else {
        $script:fails.Add($What)
        Write-Host "    FAIL: $What"
    }
}

function Wait-Terminal([string]$PurchaseId, [datetime]$Until) {
    while ((Get-Date) -lt $Until) {
        try {
            $p = ConvertFrom-Json ([TicketFlowStress.StressClient]::GetPurchase($OrdersUrl, $PurchaseId))
            if ($p.status -eq 'TICKETS_ISSUED' -or $p.status -eq 'FAILED') { return $p }
        }
        catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

try {
    Write-Host '=== Stress test TicketFlow (H-07 / N-2003, D-0014) ==='
    Write-Host "parametros: N=$N compradores | X=$(if ($X -ge 0) { $X } else { 'disponibilidad inicial' }) | deadline terminal: ${DeadlineSeconds}s"

    # --- evento: explicito o el de mayor disponibilidad ------------------------
    if (-not $EventId) {
        $events = Invoke-RestMethod -TimeoutSec 30 -Uri "$CatalogUrl/events"
        $evt = $events | Sort-Object available -Descending | Select-Object -First 1
        if (-not $evt -or $evt.available -lt 1) {
            Write-Host "CONFIG: hace falta -EventId (ningun evento del catalogo tiene disponibilidad)."
            exit 2
        }
        $EventId = $evt.id
        Write-Host "evento: $EventId (mayor disponibilidad del catalogo)"
    }
    else {
        Write-Host "evento: $EventId"
    }

    $avail0 = [TicketFlowStress.StressClient]::GetAvailability($CatalogUrl, $EventId)
    Write-Host "disponibilidad inicial: $avail0"

    # --- semantica de X: default = disponibilidad inicial; -X alinea ------------
    if ($X -eq -1) {
        $X = $avail0
        Write-Host "X = disponibilidad inicial = $X"
    }
    elseif ($X -gt $avail0) {
        Write-Host "CONFIG: X=$X > disponibilidad actual ($avail0) de '$EventId'."
        Write-Host '       No hay endpoint de creacion de eventos: no se puede fabricar disponibilidad.'
        exit 2
    }
    elseif ($X -lt $avail0) {
        $toBuy = $avail0 - $X
        Write-Host "alineacion: pre-comprando $toBuy asientos ($avail0 -> $X) en compras secuenciales de hasta 8..."
        while ($toBuy -gt 0) {
            $take = [Math]::Min(8, $toBuy)
            $key = 'stress-align-' + [guid]::NewGuid().ToString('N')
            $r = [TicketFlowStress.StressClient]::PostPurchase($OrdersUrl, $EventId, $key, $take, '4111111111111111', 'Stress Align')
            if ($r.Error) { throw "alineacion: POST error de transporte: $($r.Error)" }
            if ($r.HttpStatus -ne 201) { throw "alineacion: POST -> $($r.HttpStatus) $($r.Body)" }
            $alignId = (ConvertFrom-Json $r.Body).purchase_id
            $p = Wait-Terminal $alignId ((Get-Date).AddSeconds($DeadlineSeconds))
            if (-not $p -or $p.status -ne 'TICKETS_ISSUED') {
                throw "alineacion: la compra $alignId no llego a TICKETS_ISSUED ($(if ($p) { $p.status + ' ' + $p.reason } else { 'sin terminal' }))"
            }
            $toBuy -= $take
        }
        $aligned = [TicketFlowStress.StressClient]::GetAvailability($CatalogUrl, $EventId)
        if ($aligned -ne $X) { throw "alineacion: disponibilidad quedo en $aligned, se esperaba $X" }
        Write-Host "    OK:   disponibilidad alineada a $X"
    }

    # --- carrera: N POSTs simultaneos + muestreo de disponibilidad --------------
    Write-Host "carrera: disparando $N compradores simultaneos (1 asiento, PAN aprobada, key unica cada uno)..."
    $raceStart = Get-Date
    $race = [TicketFlowStress.StressClient]::RunRace($OrdersUrl, $CatalogUrl, $EventId, $N, '4111111111111111')
    Write-Host "    N POSTs respondidos en $($race.RaceMs) ms"

    $samples = New-Object System.Collections.Generic.List[int]
    $samples.Add($avail0)
    foreach ($s in $race.Samples) { $samples.Add($s) }

    # --- clasificacion inicial de cada comprador --------------------------------
    # PS 5.1: foreach/@() sobre List[object] con PSCustomObject dentro rompe el binder
    # DLR ("Los tipos de argumentos no coinciden", stack MaybeDebase) -> arrays PS nativos.
    $rows = @()
    $pending = @()
    foreach ($b in $race.Buyers) {
        $row = [pscustomobject]@{
            Index = $b.Index; Key = $b.Key; HttpStatus = $b.HttpStatus
            PurchaseId = $null; Status = '-'; Reason = '-'; Error = $b.Error
        }
        if ($b.Error) {
            $row.Status = 'HTTP-ERROR'
            $row.Reason = $b.Error
        }
        elseif ($b.HttpStatus -eq 201) {
            $j = ConvertFrom-Json $b.Body
            $row.PurchaseId = $j.purchase_id
            $row.Status = $j.status
            $pending += $row
        }
        elseif ($b.HttpStatus -eq 422 -or $b.HttpStatus -eq 409) {
            $j = ConvertFrom-Json $b.Body
            $row.Status = "RECHAZO-$($b.HttpStatus)"
            $row.Reason = "$($j.type): $($j.detail)"
        }
        else {
            $row.Status = "HTTP-$($b.HttpStatus)"
            $row.Reason = $b.Body
        }
        $rows += $row
    }

    # --- polling a terminal con deadline (AC3) + muestreo continuo (AC2) --------
    $deadline = (Get-Date).AddSeconds($DeadlineSeconds)
    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        $stillPending = @()
        foreach ($row in $pending) {
            $terminalNow = $false
            try {
                $p = ConvertFrom-Json ([TicketFlowStress.StressClient]::GetPurchase($OrdersUrl, $row.PurchaseId))
                if ($p.status -eq 'TICKETS_ISSUED' -or $p.status -eq 'FAILED') {
                    $row.Status = $p.status
                    if ($p.status -eq 'FAILED') { $row.Reason = $p.reason }
                    $terminalNow = $true
                }
            }
            catch { } # GET transitorio o sin respuesta aun: reintenta en la proxima pasada
            if (-not $terminalNow) { $stillPending += $row }
        }
        $pending = $stillPending
        try { $samples.Add([TicketFlowStress.StressClient]::GetAvailability($CatalogUrl, $EventId)) } catch { }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 150 }
    }
    foreach ($row in $pending) { $row.Status = 'SIN-TERMINAL' }
    $pollEnd = Get-Date
    $totalS = [Math]::Round(($pollEnd - $raceStart).TotalSeconds, 1)

    $availFinal = [TicketFlowStress.StressClient]::GetAvailability($CatalogUrl, $EventId)
    $samples.Add($availFinal)
    $minAvail = ($samples | Measure-Object -Minimum).Minimum

    # --- clasificacion final + invariantes ---------------------------------------
    $issued = @($rows | Where-Object { $_.Status -eq 'TICKETS_ISSUED' })
    $rej422 = @($rows | Where-Object { $_.Status -eq 'RECHAZO-422' })
    $rej409 = @($rows | Where-Object { $_.Status -eq 'RECHAZO-409' })
    $rejFailed = @($rows | Where-Object { $_.Status -eq 'FAILED' })
    # rechazo explicito = 422/409 con motivo en el body, o FAILED terminal con reason
    $rejExplicit = @($rows | Where-Object {
            (($_.Status -eq 'RECHAZO-422') -or ($_.Status -eq 'RECHAZO-409') -or ($_.Status -eq 'FAILED')) -and
            $_.Reason -and $_.Reason.Trim() -ne '' -and $_.Reason -ne '-'
        })
    $terminal = $issued.Count + $rej422.Count + $rej409.Count + $rejFailed.Count
    $expectedIssued = [Math]::Min($N, $X)

    Write-Host ''
    Write-Host '--- invariantes ---'
    Check ($issued.Count -le $X) "AC1  cero oversell: emitidos ($($issued.Count)) nunca > X ($X)"
    Check ($issued.Count -eq $expectedIssued) "AC1  emitidos exactos: $($issued.Count) == min(N,X) ($expectedIssued)"
    Check ($rejExplicit.Count -eq ($N - $expectedIssued)) "AC1  rechazos explicitos: $($rejExplicit.Count) == N - emitidos ($($N - $expectedIssued))"
    Check ($minAvail -ge 0) "AC2  disponibilidad nunca negativa: minima observada $minAvail ($($samples.Count) muestras)"
    Check ($availFinal -eq ($X - $issued.Count)) "AC2  disponibilidad final: $availFinal == X - emitidos ($($X - $issued.Count))"
    Check ($terminal -eq $N) "AC3  terminales dentro del plazo: $terminal/$N en ${totalS}s (limite ${DeadlineSeconds}s)"
    Check (($availFinal + $issued.Count) -eq $X) "AC4  invariante de cierre: disponibilidad_final ($availFinal) + emitidos ($($issued.Count)) == X ($X)"

    Write-Host ''
    Write-Host '--- detalle por comprador ---'
    foreach ($r in ($rows | Sort-Object Index)) {
        $k = $r.Key; if ($k.Length -gt 18) { $k = $k.Substring(0, 18) }
        $reason = "$($r.Reason)"; if ($reason.Length -gt 52) { $reason = $reason.Substring(0, 52) + '...' }
        $pur = "$($r.PurchaseId)"; if (-not $pur) { $pur = '-' }
        Write-Host ("    #{0,-3} {1,-18} HTTP {2,-3} {3,-13} {4,-12} {5}" -f $r.Index, $k, $r.HttpStatus, $r.Status, $pur, $reason)
    }

    Write-Host ''
    Write-Host '=== RESUMEN STRESS ==='
    Write-Host "    evento .......................... $EventId"
    Write-Host "    compradores N ................... $N"
    Write-Host "    asientos X ...................... $X (disponibilidad inicial: $avail0)"
    Write-Host "    TICKETS_ISSUED .................. $($issued.Count) (esperado: $expectedIssued)"
    Write-Host "    rechazos 422 explicitos ......... $($rej422.Count)"
    Write-Host "    rechazos 409 explicitos ......... $($rej409.Count)"
    Write-Host "    FAILED con motivo ............... $($rejFailed.Count)"
    Write-Host "    sin estado terminal ............. $($N - $terminal)  [viola AC3 si > 0]"
    Write-Host "    disponibilidad minima observada . $minAvail (de $($samples.Count) muestras)"
    Write-Host "    disponibilidad final ............ $availFinal"
    Write-Host "    invariante de cierre ............ $availFinal + $($issued.Count) = $($availFinal + $issued.Count) == X ($X)"
    Write-Host "    timings ........................ . carrera N POSTs: $($race.RaceMs) ms | hasta ultimo terminal: ${totalS}s (plazo ${DeadlineSeconds}s)"

    if ($fails.Count -eq 0) {
        Write-Host '=== STRESS: PASS ==='
        exit 0
    }
    Write-Host "=== STRESS: FAIL ($($fails.Count) invariantes rotas) ==="
    exit 1
}
catch {
    Write-Host "=== STRESS: FALLO === $($_.Exception.Message)"
    if ($_.InvocationInfo) { Write-Host "en linea $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())" }
    Write-Host 'El entorno esta levantado? powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1'
    exit 1
}
