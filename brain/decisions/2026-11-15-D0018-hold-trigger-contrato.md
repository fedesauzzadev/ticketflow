# D-0018: Contrato de operación del hold — `hold` en el request + `POST /purchases/{id}/payment` + estado terminal `EXPIRED`

Date: 2026-11-15
Reversibility: medium

## Context
H-09 exige dos triggers "operables por el evaluador sin editar código" (el PO dejó la forma al
TL): crear una compra cuyo pago NO se completa de inmediato, y completar el pago después
(antes o después del TTL). Además `EXPIRED` es un estado terminal NUEVO que debe preservar la
garantía V1 "toda compra termina terminal con motivo" y no romper los invariantes del estrés
(H-07: los holds son opt-in → las corridas de estrés sin holds nunca ven `EXPIRED`).

## Decision
1. **Crear compra en hold**: body de `POST /purchases` gana campo opcional
   `"hold": true|false` (default `false`; no-booleano ⇒ 400 `invalid-purchase-request`,
   NFR-1). Con `hold=true` el agregado `Purchase` persiste la bandera; al llegar
   `seats-reserved` el process manager **NO emite `authorize-payment`**: escribe
   `expires_at = now + HOLD_TTL_SECONDS` en la transición a `SEATS_RESERVED` y la compra queda
   no-terminal con expiración observable. Las compras sin hold no cambian en nada (happy path
   V1 intacto).
2. **Expiración observable**: `GET /purchases/{id}` expone `expires_at` (ISO-8601) solo en
   compras hold (`null`/ausente en el resto — mismo criterio que `reason`/`ticket_ids`).
   El `Idempotency-Key` del POST original cubre el replay: repetir el POST de una compra
   expirada devuelve la MISMA compra `EXPIRED` sin efectos (AC6 primera mitad — camino
   existente de `ResolveExistingKeyAsync`, sin cambios).
3. **Completar el pago**: endpoint nuevo **`POST /purchases/{id}/payment`** (sin body).
   Frontera validada (NFR-1):
   - id inválido ⇒ 400 `invalid-purchase-id` (mismo formato que GET).
   - no existe ⇒ 404 `purchase-not-found`.
   - claim CAS gana (`SEATS_RESERVED` + hold + `expires_at > now` + sin
     `payment_initiated_at`): escribe `payment_initiated_at` y emite `authorize-payment`
     (PAN persistida del agregado) ⇒ **202** `{purchase_id, status, expires_at}` — la saga
     sigue el flujo V1 (aprobado ⇒ `TICKETS_ISSUED`; PAN `0002` ⇒ compensación ⇒ `FAILED`).
   - ya expirada ⇒ **409 `hold-expired`** "La reserva expiró antes de completar el pago"
     (AC4: 4xx accionable; sin ticket — el guard CAS del process manager ignora cualquier
     evento tardío; sin segunda liberación — `ReleaseAsync` idempotente, D-0017 §3).
   - no-hold o estado ya avanzado ⇒ **409 `invalid-state`** con detalle del estado actual.
   - replay del endpoint (segundo POST mientras el pago vuela) ⇒ **200** con el estado actual
     (claim ya tomado — idempotente, no error).
4. **`EXPIRED` en el enum `PurchaseStatus`** (valor nuevo, wire/persistencia = nombre).
   Transición: `SEATS_RESERVED → EXPIRED` solo por el sweeper (D-0017). Terminal con `reason`
   "reserva expirada sin pago (TTL Ns)" — mismo campo `FailureReason` que FAILED.
5. **Guards del process manager con EXPIRED en el tablero**:
   - `payment-approved`/`payment-declined` sobre compra `EXPIRED`: el CAS
     `SEATS_RESERVED→PAYMENT_APPROVED` / `TryNoteFailure` pierde o se filtra por estado ⇒
     ignorado con log "duplicado descartado" (patrón existente). Nada de tickets, nada de
     liberaciones extra.
   - `step-failed` de la compensación sobre compra `EXPIRED`: ambas transiciones a FAILED
     pierden (estado terminal) ⇒ ignorado — coherente con F-V1-01 (la entrega queda en la DLQ
     del Catálogo para redrive manual, sin mutar lo terminal).
6. **Demo**: `scripts/demo-ttl.ps1` (AC8) narra retención → vencimiento → liberación
   (disponibilidad antes/después) → pago tardío 409 → re-compra con key nueva →
   `TICKETS_ISSUED`. Exit 0 solo si todas las comprobaciones pasan; corre con el TTL default
   (90 s) o el env que herede del run-local.

## Alternatives considered
- **Trigger por PAN centinela** (p. ej. termina `0003` = diferido): sobrecarga semántica de la
  PAN (rompe "cualquier otra PAN ⇒ aprobada"), el body no puede pedir "deferred + aprobado",
  y el hash del body mezcla política de pago con intención de hold. Rechazado.
- **Header `X-Hold`**: la intención de negocio viaja escondida del body hasheado — el replay
  con misma key + distinta intención no es detectable. Rechazado.
- **Endpoint `PATCH /purchases/{id}` para completar pago**: PATCH de estado parcial es un
  contrato más ancho del necesario (¿qué más se parchea?). Un verbo concreto y cerrado
  (`/payment`) es la frontera mínima. Rechazado.
- **Pago sincrónico en el POST /payment**: reintroduciría bloqueo HTTP en una saga asíncrona
  (el POST /purchases devuelve 201 inmediato por diseño, D-0004). 202 + polling es el patrón
  del producto. Rechazado.
