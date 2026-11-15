# CI local de TicketFlow (D-0020 / N-3001, H-08 AC1..AC5): TODO el gate en un comando.
#   Etapas en orden — la PRIMERA que falla es la culpable del exit != 0 (el pipeline corta):
#     1. preflight       docker presente + emulador arriba (lo arranca si hace falta) +
#                        creds/CA de esta corrida al env (emulator-env.ps1, D-0019).
#     2. build           dotnet build -warnaserror: 0 warnings como error del compilador.
#     3. tests           suite completa contra el emulador (env de preflight) — el umbral de
#                        cobertura >=70% de los Core está cableado al env (L-012): fallar el
#                        umbral falla dotnet test y esta etapa.
#     4. infra-verify    provision.ps1 -Verify (D-0019): manifiesto infra/aws-local.json ⊆ recursos reales.
#     5. workflow-lint   validate-workflow.ps1: estructura esperada de .github/workflows/ci.yml.
#   -Demos (AC4): etapa extra — arranca los 4 servicios SOLO si :5080..5083 no responden,
#     corre demo-cache / demo-idempotency / demo-resilience (TODAS deben dar exit 0; cada demo
#     limpia sus propias inyecciones) y CIERRA ordenado solo lo que él arrancó (los servicios
#     que ya estaban corriendo quedan intactos).
#   El workflow GH Actions invoca ESTE MISMO script: cero deriva local/CI (D-0020).
# PS 5.1 (compatible pwsh para el artefacto). Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ci.ps1 [-Demos]
[CmdletBinding()]
param([switch]$Demos)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:stages = @()
$script:culprit = $null
# Corridas hijas (provision/demos/validator) en el PowerShell disponible (pwsh si está).
$ps = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

function Finish([int]$code) {
    Write-Host ''
    Write-Host '=== Resumen CI ==='
    foreach ($s in $script:stages) {
        Write-Host ("[CI] {0} -> {1} ({2:N1}s)" -f $s.Name, $(if ($s.Ok) { 'OK' } else { 'FAIL' }), $s.Seconds)
    }
    if ($script:culprit) {
        Write-Host ("CULPABLE: {0} (primera etapa que falló; las siguientes no corrieron)" -f $script:culprit)
    }
    exit $code
}

function Invoke-Stage([string]$name, [scriptblock]$body) {
    Write-Host ''
    Write-Host "==> [CI] $name"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok = $false
    try {
        $ok = ((& $body) -eq $true) # el cuerpo debe declarar explícitamente $true/$false
    }
    catch {
        Write-Host "    ERROR: $($_.Exception.Message)"
        $ok = $false
    }
    $sw.Stop()
    $script:stages += [pscustomobject]@{
        Name = $name; Ok = $ok; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
    Write-Host ("[CI] {0} -> {1} ({2:N1}s)" -f $name, $(if ($ok) { 'OK' } else { 'FAIL' }),
        $sw.Elapsed.TotalSeconds)
    if (-not $ok) {
        $script:culprit = $name
        Finish 1
    }
}

function Test-Http([int]$port, [string]$probe) {
    # NUL = dispositivo nulo de Windows (PS 5.1: -o $null pasa un argumento vacío y rompe curl).
    curl.exe -s -o NUL -m 2 "http://localhost:$port$probe" 2>$null
    return ($LASTEXITCODE -eq 0)
}

# --- 1) preflight ------------------------------------------------------------------
Invoke-Stage 'preflight' {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Host '    docker no está en el PATH (¿Docker Desktop instalado?)'
        return $false
    }
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    docker ps *> $null
    $daemonOk = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $eap
    if (-not $daemonOk) {
        Write-Host '    el daemon de docker no responde (¿Docker Desktop corriendo?)'
        return $false
    }
    # Emulador up-or-start + creds/CA de ESTA corrida + health (un solo origen, D-0019).
    . (Join-Path $PSScriptRoot 'emulator-env.ps1')
    # Los e2e de saga compiten por las colas compartidas si hay servicios vivos: la suite
    # es determinista con los servicios abajo (-Demos los levanta DESPUÉS de la etapa tests).
    $live = @(5080, 5081, 5082, 5083 | Where-Object { Test-Http $_ '/health' })
    if ($live.Count -gt 0) {
        Write-Host "    ADVERTENCIA: hay servicios arriba en :$($live -join ', :') — los e2e de"
        Write-Host '    saga pueden competir por las colas compartidas. Para un gate determinista'
        Write-Host '    corré el CI con los servicios abajo (con -Demos el CI los levanta solo).'
    }
    return $true
}

# --- 2) build: 0 warnings como error -------------------------------------------------
Invoke-Stage 'build' {
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $out = dotnet build (Join-Path $root 'TicketFlow.sln') -warnaserror --nologo 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $eap
    if ($code -ne 0) {
        $out | Select-Object -Last 25 | ForEach-Object { Write-Host "    $_" }
        return $false
    }
    return $true
}

# --- 3) tests: suite completa + umbral de cobertura >=70% Core (cableado al env) ------
Invoke-Stage 'tests' {
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    dotnet test (Join-Path $root 'TicketFlow.sln') --nologo
    $code = $LASTEXITCODE
    $ErrorActionPreference = $eap
    return ($code -eq 0)
}

# --- 4) infra-verify: manifiesto vs recursos reales -----------------------------------
Invoke-Stage 'infra-verify' {
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'provision.ps1') -Verify
    $code = $LASTEXITCODE
    $ErrorActionPreference = $eap
    return ($code -eq 0)
}

# --- 5) workflow-lint: estructura esperada del workflow artefacto ---------------------
Invoke-Stage 'workflow-lint' {
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'validate-workflow.ps1')
    return ($LASTEXITCODE -eq 0)
}

# --- 6) demos (opcional, -Demos): todas exit 0 + cierre ordenado ----------------------
if ($Demos) {
    Invoke-Stage 'demos' {
        $services = @(
            @{ Log = 'catalog';   Project = 'src/Services/Catalog/TicketFlow.Catalog.Api';     Port = 5080; Probe = '/events' },
            @{ Log = 'orders';    Project = 'src/Services/Orders/TicketFlow.Orders.Api';       Port = 5081; Probe = '/health' },
            @{ Log = 'payments';  Project = 'src/Services/Payments/TicketFlow.Payments.Api';   Port = 5082; Probe = '/health' },
            @{ Log = 'ticketing'; Project = 'src/Services/Ticketing/TicketFlow.Ticketing.Api'; Port = 5083; Probe = '/health' }
        )
        $down = @($services | Where-Object { -not (Test-Http $_.Port $_.Probe) })
        $started = @()
        if ($down.Count -gt 0) {
            Write-Host "    servicios caídos: $($down.Log -join ', ') — levantando (build de la etapa 2, --no-build)..."
            if (-not $env:SEED_EVENTS) {
                $env:SEED_EVENTS = 'evt-mj-tribute-night|Michael Jackson Tribute Night|2027-03-12T21:00:00Z|120.50|500;evt-legacy-tour|Legacy World Tour (Orquestal)|2027-04-03T20:00:00Z|85.00|250'
            }
            $logsDir = Join-Path $root '.artifacts\logs' # mismo destino que run-local (los demos leen .artifacts\logs)
            New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
            foreach ($s in $down) {
                $env:SERVICE_PORT = "$($s.Port)" # cada hijo captura su puerto al iniciar
                # PS 5.1: ArgumentList como array NO cita paths con espacios → string único citado.
                $started += Start-Process -FilePath 'dotnet' `
                    -ArgumentList ('run --project "{0}" --no-build' -f (Join-Path $root $s.Project)) `
                    -RedirectStandardOutput (Join-Path $logsDir "$($s.Log).log") `
                    -RedirectStandardError (Join-Path $logsDir "$($s.Log).err.log") `
                    -PassThru -NoNewWindow
            }
            $env:SERVICE_PORT = '5080'
            foreach ($s in $down) {
                $ready = $false
                for ($i = 0; $i -lt 60; $i++) {
                    if (Test-Http $s.Port $s.Probe) { $ready = $true; break }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $ready) { throw "el servicio $($s.Log) no responde en :$($s.Port) tras 30 s" }
            }
        }
        else {
            Write-Host '    los 4 servicios ya estaban arriba: se usan tal cual (no se cierran al terminar)'
        }
        try {
            foreach ($demo in 'demo-cache', 'demo-idempotency', 'demo-resilience') {
                Write-Host "    -> $demo"
                & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "$demo.ps1")
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "    $demo terminó con exit $LASTEXITCODE"
                    return $false
                }
            }
            return $true
        }
        finally {
            # Cierre ordenado: SOLO lo que esta etapa arrancó.
            foreach ($p in $started) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }
    }
}

Finish 0
