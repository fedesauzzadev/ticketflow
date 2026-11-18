# Validación ESTRUCTURAL del workflow artefacto .github/workflows/ci.yml (D-0020, AC3).
# PS 5.1 no tiene parser YAML: se chequean las INVARIANTES estructurales que hacen que el
# workflow sea el esperado (mismo gate que el CI local). Es "estructura esperada"
# documentada, no un parseo completo del lenguaje.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\validate-workflow.ps1 [-Path <yml>]
# Exit 0 = todas las invariantes OK; exit 1 = lista las rotas.
[CmdletBinding()]
param([string]$Path)

$ErrorActionPreference = 'Stop'
if (-not $Path) {
    $Path = Join-Path (Split-Path -Parent $PSScriptRoot) '.github\workflows\ci.yml'
}

$script:failed = @()
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Host "[workflow-lint] OK    $name" }
    else {
        Write-Host "[workflow-lint] FAIL  $name"
        $script:failed += $name
    }
}

Check "existe $Path" (Test-Path $Path)
if (Test-Path $Path) {
    $lines = [IO.File]::ReadAllLines($Path)
    function Has([string]$pattern) { [bool]($lines | Where-Object { $_ -match $pattern }) }

    Check "trigger 'on:' presente" (Has '^on:')
    Check "sección 'jobs:' presente" (Has '^jobs:')
    Check "runner declarado (runs-on:)" (Has 'runs-on:')
    Check "checkout del repo (uses: actions/checkout@)" (Has 'actions/checkout@')
    Check "SDK .NET 10 (uses: actions/setup-dotnet@)" (Has 'actions/setup-dotnet@')
    Check "dotnet-version 10.x" (Has "dotnet-version:\s*'?10")
    # La invarianta central (cero deriva): el workflow invoca el MISMO gate local. Anclada a
    # la línea run: REAL del step (F-V2-02: un match global se tragaba un `run:` editado y
    # validado por menciones en comentarios).
    Check "invoca el MISMO gate local (línea run: -> scripts/ci.ps1)" (Has '^\s*run:\s*.*scripts[/\\]ci\.ps1')
    Check "indentación solo con espacios (YAML sin tabs)" (-not (Has "`t"))
}

if ($script:failed.Count -gt 0) {
    Write-Host ("workflow-lint: {0} invariante(s) rota(s)" -f $script:failed.Count)
    exit 1
}
Write-Host 'workflow-lint: estructura esperada OK'
exit 0
