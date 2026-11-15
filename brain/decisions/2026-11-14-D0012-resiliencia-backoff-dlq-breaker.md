# D-0012: Resiliencia H-06 — backoff en worker, DLQ aplicativo, circuit breaker propio, inyección por endpoint admin

Date: 2026-11-14
Reversibility: medium

## Context
H-06 (N-2002). Datos verificados: el redrive de SQS NO se probó en Fase 2 (probe 4/4 =
ops básicas); F-01 fijó timeout efectivo 10 s + `MaxErrorRetry=0` → el retry ya vive en la
app (redelivery SQS ~30 s fijo, sin backoff visible); los handlers ya son idempotentes
(claims condicionales). PO dejó al TL: política de backoff, DLQ nativa vs secundaria,
librería vs patrón del breaker, y forma de la inyección de fallos.

## Decision
1. **Backoff en `SqsSagaWorker.HandleOneAsync`** (Shared, un solo lugar para los 4
   servicios): hasta 3 intentos por entrega (1 original + 2 reintentos), esperas
   0,5 s / 1 s (crecientes — AC1 exige ≥2 reintentos con backoff visible en timestamps).
   Log por intento con `PurchaseId`, intento n/3 y causa. Constantes documentadas, sin
   config (anti-flexibilidad). Seguro: handlers idempotentes.
2. **DLQ aplicativo** (no nativa): agotados los reintentos → cuerpo original a cola FIFO
   secundaria `<cola>-dlq.fifo` (creada por el `EnsureQueueAsync` existente) + delete del
   origen + log "mensaje → DLQ". Además emite evento `step-failed{step, reason}` a
   orders-events → `SagaProcessManager` (caso nuevo): PENDING → FAILED directo;
   SEATS_RESERVED/PAYMENT_APPROVED → nota de fallo + `release-seats` (reusa la
   compensación de payment-declined) → FAILED al llegar seats-released. Compra siempre
   terminal con motivo (AC2) y sin reservas en limbo (AC3). **Inspección**: `GET
   /admin/dlq` por servicio (peek sin borrar, `VisibilityTimeout=0`) — comando curl
   documentado; no requiere creds del emulador. Redrive NO se implementa (el AC no lo
   exige); soporte de `RedrivePolicy` del emulador queda declarado no-verificado.
3. **Circuit breaker propio** (Shared.Infrastructure): `DelegatingHandler` dentro de
   `PinnedCaHttpClientFactory` → un breaker POR downstream (DynamoDB, SQS, S3, gateway
   Catálogo) gratis por instancia de HttpClient. Umbral 3 fallos consecutivos
   (timeout/excepción/5xx) → OPEN 5 s → HALF_OPEN 1 probe → CLOSE. OPEN = fail-fast
   inmediato con excepción explícita + log de apertura/cierre (AC4). **Polly
   descartado**: wiring ×4 hosts + dependencia nueva en un proyecto cero-deps-terceros
   (D-0003) para ~80 líneas propias, testeables determinísticamente sin reloj. Trade-off
   honesto documentado: en producción multi-dependencia elegiríamos
   Polly / Microsoft.Extensions.Http.Resilience.
4. **Inyección de fallos sin editar código**: `FailureInjector` singleton en memoria +
   endpoint admin por servicio: `POST /admin/failure {step, mode: transient|persistent,
   count 1..10 (default 2)}` · `GET /admin/failure` · `DELETE /admin/failure/{step}`. Lo
   consulta `SqsSagaWorker` antes del handler (step exacto o `*`). Transient con
   count=2 + 2 reintentos = AC1 exacto. Validado como toda frontera (NFR-1); sin auth
   (producto local de demo, N-A en QA F4). El "downstream colgado" de AC4 es `docker
   pause` del emulador (ya verificado en F-01) — no se inyecta hang por endpoint.

## Alternatives considered
- DLQ nativa SQS (RedrivePolicy + maxReceiveCount): soporte del emulador no verificado; la
  aplicativa es determinista, demoable por HTTP y no depende del emulador.
- Reintentos solo por redelivery SQS: esperas fijas ~30 s — backoff no creciente ni
  visible en timestamps (AC1 imposible).
- Polly v8: ver decisión 3.
- Inyección por env vars: requiere restart del servicio, menos demoable que curl en vivo.

## Enmienda (2026-11-15, fix post-QA F-V1-01)

El camino 2 dejaba un hueco refutado por QA adversarial (F-V1-01): si la compensación
`release-seats` MISMA agotaba sus 3 intentos, el segundo `step-failed` llegaba a Órdenes
con el fallo ya anotado (`TryNoteFailureAsync` = false por `attribute_not_exists(
pending_failure_reason)`) y el evento se ignoraba → compra `SEATS_RESERVED` eterna sin
`failure_reason`, asiento huérfano y drift permanente de disponibilidad.

**Fix (SagaProcessManager, caso step-failed):** `step = release-seats` → transición
directa `SEATS_RESERVED|PAYMENT_APPROVED → FAILED` (CAS) con reason que declara la reserva
huérfana — nunca silencio. NO se re-emite la compensación (ya agotó 3 intentos por
entrega; re-emitir solo alarga la agonía de un fallo persistent). El asiento queda
explícitamente tomado: la disponibilidad lo refleja y el motivo lo nombra; la entrega
espera en la DLQ del Catálogo (`ticketflow-catalog-commands-dlq.fifo`, peek-only) para una
liberación por reintento futuro — un `seats-released` tardío (redrive manual) libera el
asiento en el Catálogo sin mutar la compra ya terminal. Duplicados del `step-failed` de
compensación caen en el guard CAS (compra ya FAILED) → ignorados, idempotente.

Redrive automático de la DLQ sigue fuera de alcance (el AC no lo exige; D-0012 original).
