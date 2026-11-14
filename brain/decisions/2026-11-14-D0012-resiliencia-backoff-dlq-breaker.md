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
