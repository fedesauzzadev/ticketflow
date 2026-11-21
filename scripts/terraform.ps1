# Terraform contra el emulador AWS local (N-4001, D-0022). Pasa los argumentos tal cual:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 apply
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 plan -detailed-exitcode
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 destroy
# apply/destroy van con -auto-approve (es un emulador local en memoria).
# Requiere terraform en PATH (winget install Hashicorp.Terraform). PS 5.1.
$ErrorActionPreference = 'Stop'
$tfArgs = $args
if (-not $tfArgs) { $tfArgs = @('plan') }

# Emulador arriba + AWS_EMULATOR_* de esta corrida (un solo origen, D-0019).
. (Join-Path $PSScriptRoot 'emulator-env.ps1')

# El provider lee las variables estándar del SDK; el endpoint lo fija main.tf.
$env:AWS_ACCESS_KEY_ID = $env:AWS_EMULATOR_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:AWS_EMULATOR_SECRET_ACCESS_KEY
$env:AWS_CA_BUNDLE = $env:AWS_EMULATOR_CA_PATH
$env:TF_VAR_emulator_endpoint = $env:AWS_EMULATOR_ENDPOINT
$env:TF_IN_AUTOMATION = '1'
$env:TF_INPUT = '0' # nunca preguntar; vale para todos los subcomandos (output no acepta -input)

$dir = Join-Path (Split-Path -Parent $PSScriptRoot) 'infra\terraform'
$ErrorActionPreference = 'Continue' # PS 5.1: stderr de un nativo no debe matar el script
terraform "-chdir=$dir" init -input=false -no-color | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($tfArgs[0] -in 'apply', 'destroy') { $tfArgs += '-auto-approve' }
terraform "-chdir=$dir" @tfArgs
exit $LASTEXITCODE
