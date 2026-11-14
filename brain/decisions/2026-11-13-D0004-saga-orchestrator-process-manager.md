# D-0004: orquestación de la saga — orchestrator (process manager) en el servicio de Órdenes

Date: 2026-11-13
Reversibility: medium

## Context
El PO diseñó la compra como saga reserva→pago→emisión con compensación (pago rechazado ⇒ asiento liberado). El intake exige "microservicios orquestados". H-02 AC4 exige estado compartido de la compra observable por API (`GET /purchases/{id}` con PENDING/SEATS_RESERVED/PAYMENT_APPROVED/TICKETS_ISSUED/FAILED).

## Decision
**Orchestration (process manager centralizado)** en el servicio Órdenes:
- Órdenes es el dueño del estado de la compra (tabla `purchases` en DynamoDB) y el único que decide el próximo paso.
- Los pasos se ejecutan como **comandos SQS FIFO** hacia cada servicio (catalog-commands, payments-commands, ticketing-commands) y las respuestas vuelven como **eventos a orders-events** (misma cola FIFO, `MessageGroupId = purchase_id` ⇒ orden por compra preservada).
- Compensación: `payment-declined` → Órdenes emite `release-seats` a Catálogo y marca `FAILED` con motivo. Centralizada y legible.
- La mensajería es asíncrona; `POST /purchases` responde 201 inmediato con `purchase_id`; la evolución se observa con `GET /purchases/{id}` (patrón polling, suficiente para el MVP; webhooks/SSE no pedidos).

## Alternatives considered
- **Choreography** (cada servicio reacciona a eventos del anterior, sin coordinador): descartada porque (a) el "estado compartido observable" exigiría un agregador extra que re-inventa al orquestador; (b) la compensación queda distribuida y difícil de narrar en una demo de 10 min; (c) el requerimiento dice literalmente "orquestados".
- **Step Functions**: PROHIBIDO por spec (reemplazaría al orquestador .NET, contradiciendo el requerimiento del Negocio).
- Orquestación síncrona HTTP (Órdenes llama REST a cada servicio): acoplamiento temporal y no exhibe mensajería AWS (pilar del intake). Descartada; SQS FIFO verificado 4/4 (evidencia).
