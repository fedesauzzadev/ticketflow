# D-0006: mensajería — SQS FIFO directo; SNS fuera del MVP (gap de fidelidad verificado)

Date: 2026-11-13
Reversibility: medium

## Context
La saga necesita un canal de confianza entre servicios. Candidatos del PO: SQS (comandos) + SNS (fan-out de notificaciones), ambos "a verificar".

## Decision
- **Comandos y eventos de la saga: SQS FIFO con `ContentBasedDeduplication`** — verificado 4/4 (create/send/receive/delete, `MessageGroupId` + `MessageDeduplicationId`). Una cola FIFO de comandos por servicio + una cola de eventos para Órdenes:
  - `catalog-commands.fifo` (reserve-seats, release-seats)
  - `payments-commands.fifo` (authorize-payment)
  - `ticketing-commands.fifo` (issue-tickets)
  - `orders-events.fifo` (seats-reserved, payment-approved, payment-declined, tickets-issued)
  - `MessageGroupId = purchase_id` (orden por compra), `MessageDeduplicationId = {comando}:{purchase_id}` (dedup natural de replay — H-03 AC2).
- **SNS: NO se usa en el MVP.** Verificado (2 corridas, FIFO y estándar, recursos únicos): `Subscribe`+`Publish` aceptan pero el mensaje NUNCA llega a la cola (gap de fidelidad del emulador: control plane sin data plane de entrega). Fallback del spec: la "notificación" de entradas emitidas es un **evento interno del servicio Ticketing + log estructurado visible** (cumple lo observable del flujo). SNS pasa a V1 (H-05+).
- El endpoint de Subscribe además exige ARN de cola (hallazgo documentado por si V1 lo usa).

## Alternatives considered
- HTTP síncrono + outbox como transporte: no exhibe mensajería AWS (pilar del intake); SQS FIFO verificado lo hace innecesario.
- Colas estándar (no FIFO): pierden orden por compra y la dedup por deduplication-id; la semántica FIFO está verificada disponible.
- SNS→SQS fan-out para notificaciones: gap del emulador (evidencia §3); no bloqueante para ningún AC.
