# D-0017: Mecanismo del TTL de holds — sweeper aplicativo persistente sobre `expires_at` (no SQS DelaySeconds)

Date: 2026-11-15
Reversibility: medium

## Context
H-09/N-3003 exige liberar asientos de compras no pagadas tras un TTL configurable (default
≤120 s), con expiración terminal `EXPIRED`, idempotencia y **AC7: la expiración sobrevive
reinicios de microservicios** (el PO prohíbe implícitamente un timer puramente in-memory). El
PO dejó el mecanismo al TL y exigió probe del emulador antes de decidir si dependía de
`DelaySeconds` en FIFO. **Probe hecho (2026-11-15-ttl-probe-verification.md, 2 corridas):**
- `SendMessage` con `DelaySeconds` a cola FIFO → **RECHAZADO** por el emulador con el error
  exacto de SQS real ("The request include parameter that is not valid for this queue type").
- `CreateQueue` FIFO con atributo `DelaySeconds` → ACEPTADO (SQS real lo rechaza) — infieles
  del emulador, además inutilizable (aplicaría a todos los mensajes de la cola).
- Control en cola estándar: delay por mensaje SÍ honrado → el rechazo FIFO es semántica
  deliberada del emulador (paridad con AWS).
- `Scan` + `FilterExpression "st = :v AND expires_at < :now"` con ítems SIN `expires_at` →
  OK exacto (solo el vencido esperado): primitiva del sweeper verificada.

## Decision
**Sweeper aplicativo persistente en Órdenes**, dueño único de la expiración:

1. **Estado**: la compra hold persiste `expires_at` (epoch segundos, N) escrito en la
   transición `PENDING → SEATS_RESERVED` SOLO para holds (`hold=true` en el request, D-0018).
   Las compras normales nunca llevan `expires_at` → el happy path y el estrés (H-07) quedan
   intactos por construcción.
2. **Sweeper**: `HoldExpirySweeper` (BackgroundService en Orders.Api, intervalo fijo 5 s, sin
   config — anti-flexibilidad). Por iteración: Scan con filtro
   `#st = :sr AND expires_at < :now` (variante P4a, verificada) sobre `purchases`;
   por cada candidato: transición CAS `SEATS_RESERVED → EXPIRED` (reason "reserva expirada
   sin pago (TTL Ns)") y, solo si gana, outbox `release-seats`. Expiración efectiva ≤ TTL + 5 s
   + procesamiento — apta para demo con TTL default 90 s.
3. **Sin doble liberación, por construcción**: `DynamoDbEventRepository.ReleaseAsync`
   (D-0013) solo incrementa bajo `contains(decrement_pids, pid)` y saca el pid del set — una
   segunda liberación de la MISMA compra (compensación tardía, redelivery, expiración +
   compensación) es no-op. Quien emita `release-seats` primero gana; el resto cae en el guard.
   AC6 (expiración y compensación no acumulan) no necesita código nuevo en Catálogo.
4. **Gara contra la carrera pago-justo-antes-de-vencer** (AC3/AC4 deterministas): el endpoint
   de pago (D-0018) CLAIMa `payment_initiated_at` con condición
   `#st=:sr AND expires_at > :now AND attribute_not_exists(payment_initiated_at)`. El sweeper
   excluye los claims: la transición a EXPIRED exige `attribute_not_exists(payment_initiated_at)`.
   Si el claim gana, la saga procede aunque el evento `payment-approved` aterrice después del
   TTL (el sweeper no puede expirarla); si el sweeper gana, el pago tardío recibe 409 (AC4).
   Resolución determinista en la capa de datos, sin relojes distribuidos.
5. **Tier de contingencia (garantía V1: toda compra terminal con motivo)**: compras con
   `payment_initiated_at` viejo (`< now − 2×TTL − 30 s`, gracia documentada) y aún
   `SEATS_RESERVED` — ventana de crash entre claim y outbox — también expiran (reason declara
   pago iniciado no completado). Cierra el único hueco de terminalidad que introduce el hold.
6. **Config**: `HOLD_TTL_SECONDS` (env, default 90, rango validado fail-fast 5..3600 al
   arranque de Órdenes, NFR-1). Solo Órdenes lo lee (es el dueño del ciclo de vida).
7. **Trazabilidad**: cada expiración loguea `PurchaseId` con paso `hold-expired`
   (contrato D-0011) — la expiración es visible en `trace-purchase.ps1`.

## Alternatives considered
- **`DelaySeconds` por mensaje en FIFO**: imposible — el emulador lo rechaza con el error de
  SQS real (probe P2). Descartado por capacidad.
- **`DelaySeconds` como atributo de cola FIFO**: el emulador lo acepta pero SQS real NO
  (infieles P1) y aplicaría delay a TODOS los mensajes de la cola de la saga. Descartado por
  fidelidad + semántica.
- **Cola ESTÁNDAR dedicada con delay por mensaje** (probe P3 la honra): viable, pero exige
  re-encolar cuando el TTL cambia por env entre compra y vencimiento, agrega semántica
  estándar al manifiesto IaC y duplica el guard CAS que igual necesitamos. Más piezas, mismo
  comportamiento observable.
- **Timer in-memory (`ITimer`/`Task.Delay`) por compra**: viola AC7 (muere con el proceso).
- **TTL nativo DynamoDB (`UpdateTimeToLive`)**: en AWS real borra ítems de forma asíncrona con
  granularidad de hasta ~48 h — inútil para un TTL observable de ≤120 s; además NO transiciona
  estado ni libera asientos. Descartado por semántica.
