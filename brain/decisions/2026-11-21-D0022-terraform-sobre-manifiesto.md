# D-0022: Terraform contra el emulador, leyendo el manifiesto existente (enmienda D-0019)

Date: 2026-11-21
Reversibility: easy

## Context
El dueño del repo pidió ampliar el proyecto con más AWS (Terraform, Lambda, EKS) como
material de estudio y portfolio. D-0019 había rechazado Terraform ("dependencia nueva; el
estado mentiría sobre recursos en memoria"). El objetivo cambió: ahora Terraform ES el
contenido didáctico, y Lambda/IAM/EKS de los nodos siguientes necesitan un lugar declarativo.
Sondeo 2026-11-21: el emulador responde Lambda (invoke real), IAM, STS, EKS (control plane),
API Gateway, Step Functions, EventBridge, CloudWatch Logs, Secrets Manager, SSM, KMS.

## Decision
1. `infra/terraform/main.tf` con provider `hashicorp/aws ~> 6.0` apuntado al emulador
   (`endpoints{}` + `skip_*` + `s3_use_path_style`); credenciales y CA por env desde
   `scripts/emulator-env.ps1` vía `scripts/terraform.ps1`.
2. Los recursos se generan con `for_each` sobre `jsondecode(file("../aws-local.json"))`:
   el manifiesto de D-0019 sigue siendo la única fuente de verdad; Provision y Terraform
   son dos ejecutores del mismo dato. `provision.ps1 -Verify` verifica lo que Terraform crea.
3. Estado local gitignored (`*.tfstate`); `.terraform.lock.hcl` versionado. Si el contenedor
   se recrea, el refresh de Terraform detecta los recursos faltantes y los vuelve a crear:
   la objeción de D-0019 no aplica en esa dirección.
4. Límite aceptado: sobre un emulador donde el ensure de arranque ya creó los recursos,
   `apply` falla por "ya existe" (no están en el estado). Orden de uso: Terraform primero,
   servicios después. Adoptar existentes (`import`) queda fuera hasta que duela.

## Alternatives considered
- **HCL con los 15 recursos escritos a mano**: más "típico", pero duplica el manifiesto y
  obliga a un segundo guard test. Rechazado.
- **OpenTofu**: mismo HCL; se eligió Terraform por reconocimiento en el mercado laboral.
- **Reemplazar TicketFlow.Provision**: rompe el gate de CI (el runner no tiene terraform) y
  el guard test. Rechazado; conviven.
