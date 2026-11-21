# Changelog

Formato [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/),
versionado [SemVer](https://semver.org/lang/es/).

## [Unreleased]

### Agregado

- **Terraform contra el emulador** (N-4001, D-0022): `infra/terraform/main.tf` crea los 15
  recursos con `for_each` sobre el manifiesto `infra/aws-local.json` (única fuente de
  verdad) + wrapper `scripts/terraform.ps1`. Verificado from scratch: 15 added, plan sin
  drift, `provision.ps1 -Verify` 15/15 OK.
- **Lambda de notificación** (N-4002): `ticketflow-ticket-notifier` (Python 3.12) se dispara
  con el evento S3 `ObjectCreated` del QR de cada entrada y deja la notificación en
  CloudWatch Logs. Función, rol IAM de mínimo privilegio, permiso de invocación y
  notificación del bucket en `infra/terraform/lambda.tf`; `scripts/lambda-logs.ps1` la lee.
  Sin cambios en los servicios .NET.

## [0.3.0] - 2026-11-19

Entrega V2 (historias H-08..H-09, slices S09..S11). QA F6: SHIP (2 rondas,
2026-11-18) — aprobado por el Negocio en G4. Cierre de corrida: V0/V1/V2 completos.

### Agregado

- **Hold de asientos con TTL** (H-09): `POST /purchases` acepta `"hold": true` opcional —
  la compra reserva sin emitir el pago, expone `expires_at` y espera el endpoint nuevo
  `POST /purchases/{id}/payment` (202 a tiempo / 200 replay / 409 `hold-expired` tardío /
  409 `invalid-state` / 404 / 400). Si el TTL (`HOLD_TTL_SECONDS`, default 90 s, rango
  5..3600 fail-fast) vence sin pago, un sweeper aplicativo en Órdenes (cada 5 s, scan
  filtrado verificado por probe) transiciona a `EXPIRED` (terminal con motivo, D-0017/D-0018)
  y libera el asiento; la carrera pago-vs-vencimiento se resuelve por CAS
  (`payment_initiated_at`), la liberación nunca se duplica (idempotencia `decrement_pids`)
  y la expiración sobrevive reinicios de microservicios (estado 100 % en DynamoDB).
  Demo `scripts\demo-ttl.ps1` (retención → vencimiento → liberación → 409 → replay →
  re-compra), incorporada a `ci.ps1 -Demos`. Suite: 186/186.
- **CI local en un comando + workflow artefacto** (H-08 AC1..AC5): `scripts\ci.ps1` corre
  todo el gate (preflight → build 0 warnings → suite completa contra el emulador con umbral
  de cobertura ≥70% de los Core → infra-verify → workflow-lint; `-Demos` agrega las 4 demos
  con cierre ordenado) — la primera etapa que falla es la culpable del `exit != 0`.
  `.github/workflows/ci.yml` está versionado como artefacto (repo sin remote) e invoca el
  MISMO script: cero deriva local/CI.
- **IaC declarativa del emulador** (H-08 AC6..AC9): manifiesto `infra/aws-local.json`
  (6 tablas DynamoDB + 4 colas FIFO + 4 DLQs + bucket S3) aplicado/verificado por
  `tools/TicketFlow.Provision` vía `scripts\provision.ps1` (idempotente — aplicar N
  veces deja el mismo conjunto; `-Verify` lista lo real y difunde contra el manifiesto,
  exit 1 si falta o difiere). Guard test clava manifiesto ↔ constantes de código; los
  servicios conservan su ensure defensivo de arranque (D-0019). Suite: 155/155.

### Corregido

- **F-V2-01** (QA V2, gate NO determinista — falsos rojos ~43% en árbol limpio): la tabla
  `purchases`, el outbox y las colas de la saga del emulador son compartidas y las
  collections de `Orders.Tests` corrían en paralelo — el sweep del e2e expiraba fixtures de
  otros tests y los hosted services de las factories in-proc competían por los mensajes
  (repro QA §2b). Ahora TODO el proyecto de tests de Órdenes corre serializado
  (`DisableTestParallelization`; los otros proyectos tocan recursos disjuntos) y el assert
  de conteo exacto del sweep sobre la tabla compartida se relaja a `>= 1` (el conteo exacto
  no es determinista por diseño; la expiración de la compra propia sigue 100% asertada).
  Gate `ci.ps1` ×5 consecutivas exit 0 en árbol limpio.
- **F-V2-02**: `validate-workflow.ps1` matcheaba menciones de `ci.ps1` en comentarios — un
  `run:` editado a otro script pasaba 9/9. La invariante central ahora exige la línea
  `run:` real invocando `scripts/ci.ps1` (el drift del runner la rompe).
- **F-V2-03**: `provision.ps1 -Verify` crasheaba (exit 2) en emulador vacío — el emulador
  devuelve `Buckets=null` con 0 buckets. Null-guard: lista vacía ⇒ el bucket del manifiesto
  se reporta `FALTA` y el verify termina exit 1 con el diff (contrato documentado).
- **F-V2-04**: README stale — "155/155" → 186/186 y "-Demos corre las 3 demos" → 4.
- **F-V2-05** (Info): el reason del tier de contingencia zombi ahora advierte que el pago
  pudo aprobarse después ("verificar en Pagos antes de reintentar") — la ventana (pago
  APPROVED tardío sin ticket sobre compra EXPIRED, consistencia por CAS) sigue declarada en
  D-0017 §5.

- **F-V1-R3-01** (QA V1 ronda 3): `GET /admin/dlq` / `PeekDlqAsync` enumeraba UNA página de
  10 y `demo-resilience.ps1` flakeaba cuando la DLQ de Pagos acumulaba >10 entradas de
  corridas previas. Ahora enumera la DLQ completa sin borrar (páginas con visibilidad corta
  + dedup por MessageId, tope 500 documentado) y el demo encuentra SU entrega
  deterministicamente. Restaurados los asserts de contenido DLQ de los e2e (retirados por
  el mismo flake en el fix de F-V1-R2-01). Suite: 157/157.

## [0.2.0] - 2026-11-15

Entrega V1 (historias H-05..H-07 + H-10, slices S05..S08). QA: SHIP (3 rondas,
2026-11-15) — aprobado por el Negocio en G3.

### Agregado

- **Traza distribuida por compra (correlation-id = purchase_id)** (H-05): el id viaja
  en el envelope de la saga por SQS y aparece en el log JSON de los 4 servicios;
  `scripts\trace-purchase.ps1` imprime la traza cronológica y sale con error si
  encuentra un PAN o CVV en los logs.
- **Resiliencia observable bajo fallos inyectables** (H-06): reintentos con backoff
  0,5/1 s por entrega (3 intentos), DLQ aplicativa inspeccionable (`GET /admin/dlq`
  por servicio), circuit breaker propio fail-fast (3 fallos → OPEN 5 s → probe →
  CLOSE) e inyección de fallos por `POST /admin/failure` (transient/persistent por
  paso, sin editar código ni reiniciar). Toda compra termina en estado terminal con
  motivo — incluida la compensación que falla (ver Corregido).
- **Estrés concurrente sin oversell** (H-07): `stress-test.ps1` dispara N compradores
  simultáneos sobre un evento con X asientos y verifica `min(N,X)` `TICKETS_ISSUED`,
  cero oversell, disponibilidad nunca negativa y cierre `disponibilidad + emitidos == X`.
- **Pulido de portfolio** (H-10): frontera Content-Type estricta (415 sin efectos,
  charset tolerado), catálogo limpio de eventos de test, tests in-proc de los 4 `.Api`.
- Suite de tests: 131/131 al cierre de V1; 135/135 tras la ronda de fixes.

### Corregido (ronda 2 post-QA V1, 2026-11-15)

- **F-V1-R2-01**: el dedup id de los mensajes `step-failed` incluía solo el
  purchase_id — SQS FIFO deduplicaba el segundo fallo de la misma compra y el
  process manager nunca lo veía (con dos reglas persistent la compra quedaba
  eterna). Ahora el dedup id incluye el paso fallado (enmienda 2 de D-0012;
  tests de transporte + 2 e2e de doble fallo). Suite: 138/138.

### Corregido (ronda post-QA V1, 2026-11-15)

- **F-V1-01**: una compensación `release-seats` que agota sus reintentos ya no deja
  la compra en `SEATS_RESERVED` para siempre — ahora es terminal `FAILED` con el
  asiento huérfano declarado en el motivo y la entrega inspeccionable en la DLQ del
  Catálogo (enmienda D-0012; unit + e2e con inyección persistent en release-seats).
- **F-V1-04**: cobertura de los `.Api` reportada por el run estándar
  (`dotnet test TicketFlow.sln`, umbral ≥70% los cubre: 91,7-94,8%).

## [0.1.0] - 2026-11-14

Primera versión entregable del MVP (historias H-01..H-04, veredicto QA: SHIP).

### Agregado

- **Catálogo de eventos con disponibilidad y cache observable** (H-01): `GET /events`,
  `GET /events/{id}/availability` con header `X-Cache: HIT|MISS` (cache-aside in-app,
  TTL 30 s + invalidación en cada reserva), estado en DynamoDB con seed condicional.
- **Saga de compra multi-servicio con compensación** (H-02): `POST /purchases` recorre
  reserva → pago → emisión orquestada por process manager en Órdenes sobre SQS FIFO;
  el rechazo de pago compensa restaurando la disponibilidad. Estados observables por
  `GET /purchases/{id}`; entradas con payload QR en S3 (`GET /tickets/{id}`).
- **Idempotencia observable** (H-03): `Idempotency-Key` con claim condicional — replay
  de la misma key+body devuelve la misma compra (200) sin efectos nuevos; mismatch de
  body 422 `idempotency-key-reused`; dedup por correlación en cada worker de la saga.
- **Documentación de portfolio** (H-04): README con quickstart de un comando, mapa de
  arquitectura y patrones; scripts de demo reproducibles (`demo-cache.ps1`,
  `demo-idempotency.ps1`); ADRs en `brain/decisions/`.
- **Todo sobre el emulador oficial de AWS en local** (HTTPS + SigV4 en `:9297`): CA
  pineada por thumbprint, endpoint loopback-only verificado al arranque — cero nube
  real, cero secrets en el repo, cero costos.
- Suite de tests: 96/96 (unit + integración contra el emulador real) con umbral de
  cobertura ≥70% de líneas en los 4 proyectos Core (NFR-6).

### Seguridad

- Validación de entrada en todas las fronteras HTTP y del worker de saga (26 payloads
  hostiles auditados por QA: todos 400/404/405 accionables, cero 5xx, sin fuga de
  internals en los ProblemDetails).
- Timeouts efectivos de 10 s en toda la IO al emulador (NFR-3, fix post-QA F-01).

### Corregido (ronda post-QA, sin cambio funcional)

- F-01: timeout de 10 s efectivo contra dependencia colgada (el HttpClient del
  `PinnedCaHttpClientFactory` ahora lleva el `ClientConfig.Timeout`; antes quedaba
  suspendido 150-240 s con el emulador pausado).
- F-02: LICENSE (MIT), CHANGELOG y versión 0.1.0.
- F-03: umbral de cobertura NFR-6 cableado en la suite (falla el build si un Core
  baja de 70% de líneas).
