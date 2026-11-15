# D-0020: CI local `scripts/ci.ps1` (etapas con gate real) + workflow GH Actions que invoca el MISMO script + determinismo de demos (paginación de DLQ, F-V1-R3-01)

Date: 2026-11-15
Reversibility: easy

## Context
H-08/N-3001 (AC1..AC5). El repo NO tiene remote (decisión PO): el "CI de cada push"
materializa como (a) script de CI local PS 5.1 de un solo comando — el gate que dueño y
evaluador corren hoy — y (b) workflow de GitHub Actions versionado como artefacto válido,
documentado, que correría sin cambios con remote. Absorbe F-V1-R3-01: `demo-resilience.ps1`
flakea con la DLQ de Pagos >10 entradas porque `PeekDlqAsync` lee UNA página de 10 y la
entrega de la demo puede no estar ahí. Reutilizable: umbral de cobertura ya cableado
condicional al env (L-012/F-03), extracción de env del emulador (run-local §1-2 → D-0019 la
extrae a `emulator-env.ps1`), build 0 warnings exigido por Directory.Build.props.

## Decision
1. **`scripts/ci.ps1`** (PS 5.1, BOM UTF-8 por L-013, un comando). Etapas en orden, cada una
   con duración y veredicto impreso; la PRIMERA que falla es la culpable del exit ≠ 0 (AC2):
   | # | Etapa | Gate |
   |---|---|---|
   | 1 | `preflight` | docker presente; emulador arriba o arrancable (contenedor existente → `docker start`; si no → build+run documentado) vía `emulator-env.ps1`; health `ok`; creds+CA al env |
   | 2 | `build` | `dotnet build TicketFlow.sln -warnaserror` — 0 warnings como error del compilador (gate real, no cosmético) |
   | 3 | `tests` | `dotnet test TicketFlow.sln` con env del emulador seteado ⇒ suite completa (integración incluida) + umbral cobertura ≥70% Core activo (L-012) — exit del test = exit de la etapa |
   | 4 | `infra-verify` | `scripts/provision.ps1 -Verify` (D-0019): manifiesto ⊆ recursos reales (incluye el guard test vía etapa tests) |
   | 5 | `workflow-lint` | `scripts/validate-workflow.ps1`: validación ESTRUCTURAL del YAML (PS 5.1 no tiene parser YAML): claves requeridas (`on:`, `jobs:`, `uses: actions/checkout`, `setup-dotnet` con `10.x`), invocación de `ci.ps1`, indentación consistente — documentada como "estructura esperada" (AC3) |
   - Resumen final `[CI] etapa → OK/FAIL (Ns)`; exit 0 solo si TODAS pasan.
   - **`-Demos` (AC4)**: requiere servicios levantados — la etapa extra arranca el entorno vía
     `run-local.ps1`, corre `demo-cache.ps1`, `demo-idempotency.ps1`, `demo-resilience.ps1`
     (y `demo-ttl.ps1` desde S11), TODAS deben terminar exit 0, y CIERRA ordenado los
     servicios (stop de procesos + teardown de inyecciones) antes de salir.
2. **`.github/workflows/ci.yml`** (artefacto, sin remote): `ubuntu-latest` + `shell: pwsh` →
   checkout → `setup-dotnet` 10.x → **`pwsh -File scripts/ci.ps1`**. Decisiones documentadas
   en el propio YAML + README:
   - El runner real sirve el emulador CON DOCKER: build de `aws-emulator/` + `docker run` —
     `emulator-env.ps1` ya sabe hacer build+run+creds+CA cuando el contenedor no existe; el
     workflow NO reimplementa etapas, invoca el mismo gate (cero deriva local/CI).
   - `ci.ps1` se escribe compatible PS 5.1 → corre sin cambios en pwsh/Linux (los nativos que
     usa — docker, dotnet — son multiplataforma).
   - README documenta: qué ejecutaría en un runner real (build de imagen + suite completa
     contra el contenedor del job), y que hoy no corre porque el repo no tiene remote (AC3).
3. **F-V1-R3-01 — paginación/filtrado de la DLQ** (mecanismo del TL que exige AC5):
   `SqsSagaTransport.PeekDlqAsync` pasa de "1 página de 10" a **enumeración completa sin
   borrar**: loop de `ReceiveMessage(MaxNumberOfMessages=10, VisibilityTimeout=2)` deduplicando
   por `MessageId` hasta que una pasada no aporte mensajes nuevos (tope 50 pasadas / 500
   entradas — la DLQ es evidencia, no cola de trabajo); la visibilidad de 2 s rota las páginas
   (con `VisibilityTimeout=0` el emulador devolvería siempre la misma primera página) y al
   expirar todo vuelve a estar visible — el contrato peek-sin-delete se conserva.
   `GET /admin/dlq` devuelve la lista completa (cap documentado) y `demo-resilience.ps1`
   assertiona que SU `purchase_id` está en la lista ⇒ determinista con la DLQ sucia (>10,
   >50 entradas de corridas previas). Se restauran los asserts de contenido DLQ que el fix
   2026-11-15-F-V1-R2-01 había retirado por el mismo flake.
4. **AC2 (gate real)**: la evidencia de verificación de S10 ROMPE una etapa a propósito
   (test rojo y umbral incumplido) → exit ≠ 0 con etapa culpable → revertido → exit 0. El
   script no captura exit codes "para seguir": cada etapa corta el pipeline.

## Alternatives considered
- **Workflow que reimplementa las etapas en pasos nativos de GH Actions**: dos pipelines que
  derivan (el local miente sobre qué correría el runner). Un solo gate invocado por ambos es
  la definición de "replica las etapas" (AC3). Rechazado.
- **Subset sin emulador en el runner** (unit-only): la cobertura de los Core vive en los tests
  de integración (L-012) — un CI sin emulador mide humo. Con docker disponible en
  ubuntu-lasted, el runner real corre TODO. Rechazado (documentado como alternativa en el
  README por si la imagen no buildeara en algún runner).
- **Purgar la DLQ antes del demo** (fix de F-V1-R3-01): `PurgeQueue` no verificado en el
  emulador y destruiría la evidencia de corridas previas que el AC quiere tolerar. Rechazado.
- **`Invoke-Pester`/módulos de test de infraestructura para PS**: dependencia nueva para
  validar 5 invariantes del YAML. Regex estructural documentado es el mínimo que el AC pide.
  Rechazado.
