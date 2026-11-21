# Los 4 servicios en Kubernetes local con kind (N-4005). Mismos puertos que run-local y
# run-docker (no correr a la vez). Idempotente: se puede re-ejecutar.
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-k8s.ps1         # cluster + imágenes + deploy
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-k8s.ps1 -Down   # borra el cluster kind
# Requiere kind y kubectl en PATH (winget install Kubernetes.kind Kubernetes.kubectl). PS 5.1.
# Mirar: kubectl -n ticketflow get pods · kubectl -n ticketflow logs deploy/orders -f
[CmdletBinding()]
param([switch]$Down)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cluster = 'ticketflow'
$k = @('--context', "kind-$cluster", '-n', 'ticketflow') # nunca tocar otro cluster del kubeconfig

if ($Down) { kind delete cluster --name $cluster; exit $LASTEXITCODE }

# Emulador arriba + AWS_EMULATOR_* y .secrets\ca.pem de esta corrida.
. (Join-Path $PSScriptRoot 'emulator-env.ps1')
if (-not $env:SEED_EVENTS) {
    $env:SEED_EVENTS = 'evt-mj-tribute-night|Michael Jackson Tribute Night|2027-03-12T21:00:00Z|120.50|500;evt-legacy-tour|Legacy World Tour (Orquestal)|2027-04-03T20:00:00Z|85.00|250'
}
$ErrorActionPreference = 'Continue' # PS 5.1: docker/kind escriben progreso por stderr
function Assert-Ok($what) { if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: $what"; exit 1 } }

Write-Host '==> Imagenes (mismo Dockerfile que run-docker)...'
docker compose -f (Join-Path $root 'docker-compose.yml') --profile services build catalog orders payments ticketing
Assert-Ok 'docker compose build'

if ((kind get clusters 2>$null) -notcontains $cluster) {
    Write-Host "==> Creando cluster kind '$cluster'..."
    kind create cluster --config (Join-Path $root 'infra\k8s\kind.yaml')
    Assert-Ok 'kind create cluster (puertos 5080-5083 ocupados? bajar run-local / run-docker -Down)'
}
kind load docker-image ticketflow-catalog ticketflow-orders ticketflow-payments ticketflow-ticketing --name $cluster
Assert-Ok 'kind load docker-image'

# Namespace primero; Secret y ConfigMaps salen del entorno, nunca del repo.
# `create --dry-run=client -o yaml | apply` = upsert idempotente.
kubectl --context "kind-$cluster" apply -f (Join-Path $root 'infra\k8s\ticketflow.yaml') | Out-Null
Assert-Ok 'kubectl apply'
kubectl @k create secret generic emulator-credentials --dry-run=client -o yaml `
    "--from-literal=AWS_EMULATOR_ACCESS_KEY_ID=$env:AWS_EMULATOR_ACCESS_KEY_ID" `
    "--from-literal=AWS_EMULATOR_SECRET_ACCESS_KEY=$env:AWS_EMULATOR_SECRET_ACCESS_KEY" | kubectl @k apply -f - | Out-Null
kubectl @k create configmap emulator-ca --dry-run=client -o yaml "--from-file=ca.pem=$env:AWS_EMULATOR_CA_PATH" | kubectl @k apply -f - | Out-Null
kubectl @k create configmap ticketflow-config --dry-run=client -o yaml `
    '--from-literal=AWS_EMULATOR_ENDPOINT=https://host.docker.internal:9297' `
    '--from-literal=AWS_EMULATOR_ALLOWED_HOST=host.docker.internal' `
    '--from-literal=AWS_EMULATOR_CA_PATH=/run/emulator/ca.pem' `
    "--from-literal=SEED_EVENTS=$env:SEED_EVENTS" | kubectl @k apply -f - | Out-Null
Assert-Ok 'kubectl apply (secret/configmaps)'

# Imagen :latest re-cargada o credenciales nuevas del emulador: los pods deben reiniciar para verlas.
kubectl @k rollout restart deployment catalog orders payments ticketing | Out-Null
foreach ($d in 'catalog', 'orders', 'payments', 'ticketing') {
    kubectl @k rollout status "deployment/$d" --timeout=180s
    Assert-Ok "rollout de $d (kubectl -n ticketflow describe pod -l app=$d; logs deploy/$d)"
}

$probes = @{ 5080 = '/events'; 5081 = '/health'; 5082 = '/health'; 5083 = '/health' }
foreach ($port in $probes.Keys) {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        curl.exe -s -f -o NUL "http://localhost:$port$($probes[$port])"
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { Write-Host "ERROR: :$port no responde por NodePort."; exit 1 }
}
kubectl @k get pods
Write-Host '==> Los 4 servicios corren en Kubernetes (kind): http://localhost:5080..5083'
