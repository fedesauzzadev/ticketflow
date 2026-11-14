# TicketFlow — Arquitectura

> Backend de venta de entradas para eventos de alta demanda (flash-sale ticketing). 4 microservicios .NET 10 orquestados, 100% sobre el emulador oficial de AWS en local (`https://127.0.0.1:9297`).

## Visión en una frase

Un evaluador clona el repo, corre un comando, y ve con curl: catálogo con cache (`X-Cache: HIT|MISS`), una compra que atraviesa **reserva → pago → emisión** como saga orquestada con compensación, **idempotencia** sin duplicar efectos, y estado persistido en DynamoDB/S3 del emulador (sobrevive reinicios de los servicios).

## Diagrama

```mermaid
flowchart LR
    E[Evaluador<br/>curl / scripts] -->|GET /events| CAT[Catálogo :5080<br/>cache-aside + X-Cache]
    E -->|POST /purchases<br/>Idempotency-Key| ORD[Órdenes :5081<br/>process manager]
    E -->|GET /purchases/{id}| ORD
    E -->|GET /tickets/{id}| TIC[Ticketing :5083]

    ORD -->|reserve-seats / release-seats| Q1[catalog-commands.fifo]
    ORD -->|authorize-payment| Q2[payments-commands.fifo]
    ORD -->|issue-tickets| Q3[ticketing-commands.fifo]
    CAT -->|seats-reserved| QE[orders-events.fifo]
    PAY -->|payment-approved / declined| QE
    TIC -->|tickets-issued| QE
    QE --> ORD

    subgraph EMU[Emulador AWS :9297 — HTTPS + SigV4, CA pineada]
      DDB[(DynamoDB)]
      SQS[SQS FIFO]
      S3[(S3)]
    end

    CAT -.-> DDB
    ORD -.-> DDB
    PAY[Pagos :5082] -.-> DDB
    TIC -.-> DDB
    Q1 & Q2 & Q3 & QE -.-> SQS
    TIC -.ticket QR.-> S3
```

## Los 4 servicios

| Servicio | Puerto | Responsabilidad | Estado propio (DynamoDB) |
|---|---|---|---|
| **Catálogo** | 5080 | Eventos y disponibilidad; cache-aside con TTL 30s + invalidación al reservar/liberar; worker `reserve-seats`/`release-seats` con decremento condicional (anti-oversell) | `catalog_events`, `catalog_reservations`, `catalog_idempotency` |
| **Órdenes** | 5081 | Process manager de la saga; estado de la compra (`PENDING→SEATS_RESERVED→PAYMENT_APPROVED→TICKETS_ISSUED \| FAILED`); idempotencia HTTP por `Idempotency-Key`; outbox | `orders_purchases`, `orders_outbox`, `orders_idempotency` |
| **Pagos** | 5082 | Autorización simulada determinística (PAN `…0002` = rechazo; otro = aprobada); idempotente por compra | `payments_auths`, `payments_idempotency` |
| **Ticketing** | 5083 | Emisión de entradas + payload QR en S3 + evento interno de notificación (log visible) | `tickets`, `ticketing_idempotency` |

## La saga (camino feliz y compensación)

1. `POST /purchases` con `Idempotency-Key: K1` → 201 `{purchase_id, status: PENDING}`. Órdenes persiste key+hash del body (put condicional ⇒ replay detectado).
2. `reserve-seats` → Catálogo verifica `availability > 0` con update condicional atómico, registra la reserva, **invalida el cache** de disponibilidad, responde `seats-reserved`.
3. `authorize-payment` → Pagos decide por PAN y responde `payment-approved` / `payment-declined` (idempotente por `purchase_id`).
4. **Aprobado** → `issue-tickets` → Ticketing crea el ticket, sube el QR a S3, notifica (evento interno + log), responde `tickets-issued` → estado final `TICKETS_ISSUED`.
5. **Rechazado (compensación)** → Órdenes emite `release-seats`: Catálogo revierte el decremento, invalida cache, y la compra termina `FAILED` con motivo. La disponibilidad observable vuelve a su valor previo.

Todo el tráfico entre servicios es **SQS FIFO** (`MessageGroupId = purchase_id` ⇒ orden por compra; `MessageDeduplicationId = {comando}:{purchase_id}` ⇒ comandos duplicados no duplican efectos). El estado vive en el emulador: reiniciar los microservicios no pierde datos.

## Patrones aplicados (dónde vive cada uno)

| Patrón | Dónde |
|---|---|
| Repository | `*.Core/Application` (interfaces) + `Infrastructure` (impl DynamoDB), por agregado y servicio |
| Mediator-equivalente (in-process) | `TicketFlow.Shared.Infrastructure` command dispatcher (handlers por DI; sin dependencias comerciales) |
| Saga orchestrator / process manager | `TicketFlow.Orders` (máquina de estados + outbox) |
| Transactional outbox | `orders_outbox` (DynamoDB) + relay publicador SQS en `TicketFlow.Orders.Api` |
| Idempotency-key store | Puts condicionales DynamoDB (`*_idempotency`) en los 4 servicios; `POST /purchases` y cada worker |
| Cache-aside + invalidación explícita | `TicketFlow.Catalog` (HybridCache, TTL 30s, header `X-Cache`) |
| Adapter de infraestructura | `EmulatorAwsClientFactory` en `Shared.Infrastructure`: endpoint + CA pineada + timeouts; imposible llamar a AWS real por accidente |

## Decisiones (ADRs)

- [D-0002] Dominio: venta de entradas de alta demanda
- [D-0003] Stack .NET 10 + AWS SDK v4 contra el emulador (reglas verificadas)
- [D-0004] Saga por orquestación (process manager en Órdenes)
- [D-0005] Estado en DynamoDB del emulador (decremento condicional verificado)
- [D-0006] SQS FIFO para mensajería; SNS fuera del MVP (gap de fidelidad del emulador)
- [D-0007] Cache in-app con invalidación (el AC exige X-Cache observable, no un servicio)
- [D-0008] Estructura de la solución y mapa de patrones

> ADRs completos en `brain/decisions/`. Evidencia de la verificación del emulador en `brain/evidence/2026-11-13-emulator-verification.md`.
