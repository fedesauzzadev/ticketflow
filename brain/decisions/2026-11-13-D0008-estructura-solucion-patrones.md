# D-0008: estructura de la solución .NET — layering por servicio y patrones a exhibir

Date: 2026-11-13
Reversibility: medium

## Context
El Negocio impuso clean architecture + patrones + buen código, exhibidos en un portfolio legible. 4 servicios (D-0002): Catálogo, Órdenes, Pagos, Ticketing. El MVP debe poder construirse en slices verticales sin montar 20 proyectos el día 1.

## Decision
**Solución `TicketFlow.sln` — 2 proyectos por servicio + 1 shared + 4 de tests (13 csproj):**

```
src/
  Shared/TicketFlow.Shared.Infrastructure/        # UNO solo: composición de SDK AWS (endpoint+CA pin+timeouts,
                                                   # D-0003), command dispatcher, outbox relay, idempotency store
  Services/Catalog/TicketFlow.Catalog.Api/        # host: Minimal APIs + workers SQS + DI
  Services/Catalog/TicketFlow.Catalog.Core/       # dominio + application + interfaces de repositorio
  Services/Orders/TicketFlow.Orders.Api/          # host + process manager (D-0004)
  Services/Orders/TicketFlow.Orders.Core/
  Services/Payments/TicketFlow.Payments.Api/
  Services/Payments/TicketFlow.Payments.Core/
  Services/Ticketing/TicketFlow.Ticketing.Api/
  Services/Ticketing/TicketFlow.Ticketing.Core/
tests/
  TicketFlow.Catalog.Tests / Orders.Tests / Payments.Tests / Ticketing.Tests
scripts/  (run-local.ps1, seed.ps1, demo-idempotency.ps1, demo-cache.ps1)
docs/     (architecture.md …)
aws-emulator/ (Dockerfile, dado)
```

- Layering dentro de cada Core: `Domain/` (entidades + reglas, cero deps) ← `Application/` (casos de uso + interfaces de infra) ← `Infrastructure/` (impl DynamoDB/SQS/S3 tras las interfaces) ← `Api/` (composición). **Dirección de dependencia única, verificable**: Api→Infrastructure→Application→Domain.
- Puertos: Catalog **5080**, Orders **5081**, Payments **5082**, Ticketing **5083** (config por env).

**Patrones a exhibir (mapa patrón→dónde vive, para el README H-04 AC4):**
1. **Repository** — repos DynamoDB por agregado en cada Core (interfaces en Application, impl en Infrastructure).
2. **Mediator-equivalente** — command dispatcher in-process (~1 interfaz + 1 clase en Shared, registra handlers por DI). NO MediatR (v13+ comercial; licencia en un portfolio es ruido). Equivalente honesto y sin deps.
3. **Outbox** — tabla `orders_outbox` en DynamoDB: Órdenes persiste estado+comando a emitir, un relay lo publica a SQS. Al-lease-una-vez consumidor + dedup id ⇒ sin doble efecto (H-03 AC2).
4. **Idempotency-key store** — puts condicionales (`attribute_not_exists`) en tablas `*_idempotency`: HTTP `Idempotency-Key` en `POST /purchases` (H-03 AC1) y dedup de comandos por correlación. Primitiva condicional de DynamoDB verificada.
5. **Process manager / saga orchestrator** — Órdenes (D-0004).
6. **Cache-aside + invalidación** — Catálogo (D-0007).
7. **Adapter de infraestructura** — `EmulatorAwsClientFactory` (endpoint+CA pin+timeout), aísla el emulador y lo vuelve inyectable/testeable.

## Alternatives considered
- 4-5 proyectos por servicio (Domain/Application/Infrastructure/Contracts separados): ~20 csproj, ceremonia que no suma a la demo y frena el slice 1.
- Un único proyecto monolítico: no exhibe microservicios (imposición del Negocio).
- Sin shared (todo duplicado por servicio): duplica el código más propenso a bugs (SDK v4 + TLS); un shared delgado es el punto justo.
