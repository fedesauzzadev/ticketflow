# Dot-sourced: da Get-DemoToken (access token de Cognito de un usuario de demo del EMULADOR, D-0024).
# Requiere la solucion ya compilada (run-local/run-docker/run-k8s la dejan lista).
#   . .\scripts\auth.ps1
#   $h = @{ Authorization = "Bearer $(Get-DemoToken)" }
. (Join-Path $PSScriptRoot 'emulator-env.ps1')
$script:authRoot = Split-Path -Parent $PSScriptRoot

function Get-DemoToken([string]$User = 'demo') {
    $project = Join-Path $script:authRoot 'tools\TicketFlow.Provision\TicketFlow.Provision.csproj'
    $manifest = Join-Path $script:authRoot 'infra\aws-local.json'
    $out = @(dotnet run --project $project --no-build -- --token $User --manifest $manifest)
    if ($LASTEXITCODE -ne 0) { throw "No se pudo obtener el token (compila antes con: dotnet build). $out" }
    return $out[-1]
}
