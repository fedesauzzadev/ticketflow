# D-0019: IaC — manifiesto JSON versionado + tool .NET `provision` idempotente; el ensure defensivo de arranque SE CONSERVA

Date: 2026-11-15
Reversibility: easy

## Context
H-08/N-3002 (AC6..AC9): hoy los recursos AWS viven implícitos en código de arranque C#
(`DynamoDbTables.EnsureAsync`, `EnsureQueueAsync` del transporte, `PutBucketAsync` del store
S3). El AC exige un manifiesto versionado legible como datos + un único comando que
aprovisione from scratch, idempotente (2 re-ejecuciones ⇒ exit 0, sin duplicados) + comando
de verificación 1:1. El PO dejó al TL: formato, ejecutor, y si los servicios conservan su
ensure defensivo. Restricciones reales: aws-cli NO está garantizado en la máquina; todo lo
que hable con el emulador necesita SigV4 + pin de CA del emulador (ya resuelto en
`Shared.Infrastructure`, D-0003/L-001); PS 5.1 (no pwsh).

## Decision
1. **Manifiesto**: `infra/aws-local.json` — JSON versionado, legible como datos, declara los
   recursos del emulador con nombres + forma:
   - `tables[]`: las 6 (`purchases`, `orders_outbox`, `orders_idempotency`, `catalog_events`,
     `payments`, `tickets`) con `keySchema` (todas `pk` HASH + `attributeDefinitions`
     `pk:S` — lo que `DynamoDbTables.EnsureAsync` crea hoy) y `billingMode`.
   - `queues[]`: las 4 FIFO de `SagaQueues.All` + sus 4 DLQs (`<cola>-dlq.fifo`,
     `DlqQueueOf`), todas con `fifo: true` + `contentBasedDeduplication: true`.
   - `buckets[]`: `ticketflow-tickets`.
   El seed de eventos es dato de demo, NO infraestructura: fuera del manifiesto (nota del PO).
2. **Ejecutor**: `tools/TicketFlow.Provision` — console .NET 10 que referencia
   `Shared.Infrastructure` y REUSA la plomería verificada (`EmulatorAwsOptions.FromEnvironment`,
   `EmulatorAwsClients`, `DynamoDbTables.EnsureAsync` tolerante a carreras L-007,
   ensure de cola FIFO del transporte, patrón idempotente de bucket). Idempotencia heredada
   por construcción (describe/get antes de create; carreras absorbidas). Dos modos:
   - `--apply` (default): asegura todos los recursos del manifiesto; exit 0.
   - `--verify`: lista recursos reales (ListTables/ListQueues/ListBuckets) y compara contra
     el manifiesto: **falta alguno o difiere ⇒ exit 1 con el diff**; recursos extra ⇒
     WARNING listado (exit 0) — la comparación estricta 1:1 "sin de más" se demuestra en la
     evidencia from-scratch (emulador recién recreado no tiene extras); sobre sesión compartida
     (tests `it-*`, probes) el modo estricto sería flaky por diseño.
3. **Wrapper PS**: `scripts/provision.ps1 [-Verify]` — extrae creds+CA del contenedor y setea
   env (mismo §1-2 de `run-local.ps1`, que se extrae a `scripts/emulator-env.ps1` dot-sourced
   por run-local, provision y ci — un solo origen, deja de triplicarse) y corre la tool.
   Documentado en README (AC6: "dónde se definen los recursos" → link al manifiesto).
4. **El ensure defensivo de arranque SE CONSERVA** (decisión pedida por el PO): el manifiesto
   es la fuente de verdad de la LISTA; el ensure en el arranque es resiliencia de runtime
   (carreras de arranque L-007, arranque sin provision previo — p. ej. clonar y run-local
   directo). Costo cero (ya existe), riesgo de remoción alto. La deriva manifiesto↔código se
   cierra con un **guard test** (`tools/TicketFlow.Provision.Tests`): el JSON parseado debe
   coincidir EXACTAMENTE con las constantes de código (`SagaQueues.All` + `DlqQueueOf` + los
   6 `TableName` + `S3QrPayloadStore.BucketName`) — el test referencia los 4 Core y falla si
   alguien toca una constante sin el manifiesto (o viceversa). El gate de CI (D-0020) corre
   este test siempre.
5. **Esquemas**: las tablas comparten esquema trivial (`pk` HASH). El manifiesto lo declara y
   la tool lo aplica; el ensure de los servicios crea lo mismo si la tabla no existe. Riesgo
   de deriva de esquema aceptado y documentado (cambiar el esquema exige tocar ambos — el
   guard test de nombres + la revisión del ADR lo hacen visible).

## Alternatives considered
- **aws-cli (script PS puro)**: exigiría firmar SigV4 a mano o instalar aws-cli (no está en la
  máquina y suma dependencia global al repo) + manejar la CA self-signed fuera del pin — todo
  lo que `Shared.Infrastructure` ya resolvió en .NET. Rechazado.
- **AWS CloudFormation/SAM/CDK contra el emulador**: el emulador solo expone DynamoDB/SQS/S3
  (sin CloudFormation) — imposible por capacidad; y una tool IaC pesada para 11 recursos
  locales es sobre-ingeniería de portfolio. Rechazado.
- **Terraform local**: dependencia nueva (binario), backend de estado para recursos en memoria
  que mueren con el contenedor — el estado mentiría. Rechazado.
- **Generar el manifiesto DESDE las constantes en build-time**: el AC quiere la lista legible
  como datos versionados, no generada; además el guard test ya da la equivalencia sin meta-pasos.
  Rechazado.
- **Delegar 100% en la tool y REMOVER el ensure de arranque**: acopla el arranque de cada
  servicio a un paso previo externo (y a que alguien corra provision en el orden correcto);
  el ensure tolerante a carreras es defensivo casi gratis. Rechazado (decisión explícita del
  punto 4).
