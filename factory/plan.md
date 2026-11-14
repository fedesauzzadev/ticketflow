# Plan Técnico — TicketFlow (Fase 2, Tech Lead)

**Fecha:** 2026-11-13 · **Estado:** para G1 · **Input:** spec.md, backlog.md (H-01..H-04 = MVP), roadmap.md, division.md, security.md · **Verificación del emulador:** brain/evidence/2026-11-13-emulator-verification.md

---

## 0. Verificación REAL del emulador (hecho, con evidencia)

**El emulador oficial AWS funciona y es usable.** Probe con AWS SDK for .NET v4.1.0 sobre la imagen buildeada del `aws-emulator/Dockerfile`: **19/20 operaciones OK**.

| Pilar | Servicio | Veredicto | Notas / uso |
|---|---|---|---|
| Estado | **DynamoDB** | ✅ 7/7 | Incluye decremento atómico con `ConditionExpression` + `ConditionalCheckFailedException` (anti-oversell). Estado en memoria: sobrevive reinicios de microservicios (H-01 AC4 ✓), NO de contenedor (declarado en quickstart). |
| Mensajería | **SQS FIFO** | ✅ 4/4 | FIFO + `ContentBasedDeduplication` + `MessageGroupId/DeduplicationId`. Canal de la saga. |
| Fan-out | SNS | ⚠️ control plane OK, **entrega NO** | `Subscribe`/`Publish` aceptan pero el mensaje no llega (2 corridas, FIFO y estándar). **Fallback spec**: notificación = evento interno + log; SNS → V1. |
| Artefactos | **S3** | ✅ 4/4 | Bucket + put/get object (payload QR de la entrada). |
| Cache | ElastiCache/Redis | ❌ sin data plane | Solo 9297 HTTPS; no hay protocolo Redis. **Fallback spec**: cache in-app + invalidación (el AC exige `X-Cache: HIT|MISS` observable, no un servicio). |

**Reglas del emulador que el código DEBE respetar** (hallazgos verificados, ADR D-0003):
1. HTTPS self-signed con CA propia (`ca.pem` dentro del contenedor) + **SigV4 obligatorio con credenciales regeneradas por corrida** (no valen dummies). El script de dev las extrae (`docker exec`/`docker cp`) e inyecta por env.
2. SDK v4: `ServiceURL` + `AuthenticationRegion` — **NUNCA `RegionEndpoint` junto a `ServiceURL`** (gana RegionEndpoint y el tráfico va a AWS real; hallazgo L-001).
3. TLS por `HttpClientFactory` custom con pin de thumbprint de la CA → además **imposibilita llamadas accidentales a AWS real** (gate de división por diseño).
4. Health: `GET https://127.0.0.1:9297/__admin/health` → `ok`.

---

## 1. Stack (impuesto por Negocio — ADR D-0003)

| Ítem | Decisión |
|---|---|
| Runtime | **.NET 10** (`net10.0`), ASP.NET Core **Minimal APIs** |
| SDK AWS | **AWSSDK v4.1.0** (`DynamoDBv2`, `SQS`, `S3`, `SimpleNotificationService`) |
| Almacén | DynamoDB del emulador (D-0005) |
| Mensajería | SQS FIFO (D-0006) |
| Cache | `Microsoft.Extensions.Caching.Hybrid` in-app + invalidación (D-0007) |
| Logging | `ILogger` + consola JSON, correlation-id `purchase_id` (cero deps extra) |
| Tests | xUnit + probes de integración contra el emulador real |

## 2. Arquitectura

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

    subgraph EMU[Emulador AWS :9297 — HTTPS+SigV4, CA pineada]
      DDB[(DynamoDB)]
      SQS[SQS FIFO]
      S3[(S3)]
    end

    CAT -.repositorios.-> DDB
    ORD -.repositorios+outbox.-> DDB
    PAY -.-> DDB
    TIC -.-> DDB
    Q1 & Q2 & Q3 & QE -.-> SQS
    TIC -.ticket QR.-> S3
```

**Flujo de la saga (feliz + compensación):**
1. `POST /purchases` (header `Idempotency-Key`) → Órdenes valida, guarda compra `PENDING` con key+hash del body (put condicional = dedup HTTP), emite `reserve-seats` (outbox→SQS). → 201.
2. Catálogo: decremento condicional `availability > 0` (anti-oversell), reserva `RESERVATION#purchase_id`, **invalida cache**, responde `seats-reserved`.
3. Órdenes: `SEATS_RESERVED` → emite `authorize-payment`.
4. Pagos: simulador determinístico (PAN termina `0002` ⇒ `declined`; otro ⇒ `approved`), idempotente por `AUTH#purchase_id`, responde.
5a. `payment-approved` → `PAYMENT_APPROVED` → emite `issue-tickets`. Ticketing: crea ticket, sube payload QR a S3, evento interno de notificación + log, responde `tickets-issued` → **`TICKETS_ISSUED`** con `ticket_ids`.
5b. `payment-declined` → **compensación**: emite `release-seats`; Catálogo revierte el decremento, invalida cache, responde → compra **`FAILED`** con motivo. Disponibilidad observable vuelve al valor previo (H-02 AC3).
6. Replay de `POST` con misma key → misma compra, cero efectos nuevos (H-03 AC1). Duplicado de comando interno → dedup id + idempotency store + log "duplicado descartado" (H-03 AC2).

### Contratos MVP (fronteras de confianza — validación de entrada obligatoria)

| Endpoint | Contrato |
|---|---|
| `GET /events` → 200 | `[{id, name, date, price, available}]` — seed ≥2 eventos |
| `GET /events/{id}/availability` → 200 | `{event_id, available}` + header **`X-Cache: HIT\|MISS`**; 404 si no existe |
| `POST /purchases` → 201 / 409 / 422 | Header requerido `Idempotency-Key` (1..200 chars). Body `{event_id: string, seats: [1..8], card: {pan: 13..19 dígitos, holder: 2..80, expiry: MM/YY, cvv: 3}}`. 422 si asientos agotados/ya reservados (H-03 AC3); 400 si body inválido. Respuesta: `{purchase_id, status, reason?, ticket_ids?}` |
| `GET /purchases/{id}` → 200/404 | `{purchase_id, status, reason?, ticket_ids?, created_at}` — status ∈ {PENDING, SEATS_RESERVED, PAYMENT_APPROVED, TICKETS_ISSUED, FAILED} |
| `GET /payments/{purchase_id}` → 200/404 | `{purchase_id, status: APPROVED\|DECLINED, reason?}` (evidencia del paso) |
| `GET /tickets/{id}` → 200/404 | `{id, purchase_id, event_id, seat, qr_s3_key}` (QR referenciable) |

**Validación**: cada endpoint valida tipo/rango/formato/tamaño y rechaza 400/422 con mensaje accionable (sin internals). Crash-coherencia probada con payload basura (NFR-4).

### Persistencia / configuración / logging

- **DynamoDB** (tablas por agregado, D-0005); creación idempotente al arranque. **Outbox** solo en Órdenes (D-0008). **S3**: bucket `ticketflow-tickets` con payload QR del ticket.
- **Config 100% por env** (cero secrets en repo): `AWS_EMULATOR_ENDPOINT`, `AWS_EMULATOR_ACCESS_KEY_ID/SECRET`, `AWS_EMULATOR_CA_PATH`, `SERVICE_PORT`, `SEED_EVENTS`. `scripts/run-local.ps1` extrae credenciales+CA del contenedor → `.secrets/` (gitignored) → env.
- **Logging** JSON consola + `purchase_id` en scope: la saga se narra sola en logs (evidencia de duplicados descartados, H-03 AC2).

## 3. NFRs medibles (de security.md — se reportan OK/FALLA con evidencia en cada gate)

| # | NFR | Medible |
|---|---|---|
| NFR-1 | Validación de entrada en TODA frontera (HTTP body/params, mensajes SQS, env al arranque) | 100% de endpoints/commands con validación; fuzz manual de payload basura → 400/422, proceso vivo |
| NFR-2 | Cero secrets en código/logs/commits; config solo por env; `.env`/`.secrets` en `.gitignore` | grep de patrones (AKIA/secret) → 0 hits; lockfile commiteado; `dotnet list package --vulnerable` sin Críticas/Altas |
| NFR-3 | Toda IO externa con timeout explícito; retry solo en ops idempotentes (SQS receive/delete, DynamoDB reads) | Clientes AWS: Timeout 10s (100%); sin retry en `POST` no-idempotentes |
| NFR-4 | Errores sin fuga de internals; códigos correctos | Respuestas de error = ProblemDetails `{type, title, status, detail}` sin stack/caminos/versiones; suite de errores 400/404/409/422 |
| NFR-5 | Performance: p95 `GET /events/{id}/availability` (HIT) **< 50 ms**; p95 `POST /purchases` (201/202 inmediato) **< 200 ms**; saga completa→`TICKETS_ISSUED` p95 **< 2 s** | Medido con script (50 iteraciones, percentil reportado), en `brain/evidence/` |
| NFR-6 | Tests: unit verde y **cobertura ≥ 70%** de los 4 `Core` (líneas); integración contra emulador real verde | `dotnet test` + coverlet; umbral falla el build de verificación |
| NFR-7 | AWS 100% local | Pin de CA del emulador en TODOS los clientes (llamada a AWS real imposible por diseño) + `AWS_EC2_METADATA_DISABLED=true` |
| NFR-8 | Idempotencia y cache demostrables por script | `demo-idempotency.ps1` y `demo-cache.ps1` imprimen la evidencia (H-04 AC2/AC3) — parte de DoD |

## 4. Sprints (1 slice por sprint, vertical end-to-end, AC heredadas del backlog)

> Cada slice: AC escritas antes de codificar → build+tests → verificación funcional con evidencia en `brain/evidence/` → refutación por verifier. Nodos del brain en `brain/index.md`.

### S01 — Catálogo con cache observable y estado persistido (H-01 / N-1001)
- Esqueleto de solución completa (13 csproj, D-0008), `Shared.Infrastructure` con `EmulatorAwsClientFactory` (pin CA, timeouts, `AuthenticationRegion`), script `run-local.ps1` (extrae creds+CA, setea env, levanta Catálogo), seed ≥2 eventos al arranque.
- `GET /events` + `GET /events/{id}/availability` con `X-Cache: MISS|HIT` (TTL 30s) + repositorio DynamoDB + tests unit (Core ≥70%) + test integración contra emulador real.
- **AC heredadas:** H-01 AC1 (MISS→HIT mismo valor), AC3 (lista ≥2), AC4 (reiniciar microservicio NO emulador → datos vivos). AC2 (invalidación) se cierra con S02 (la dispara la compra); en S01 se testea la invalidación interna del worker con un comando sintético.

### S02 — Saga completa con compensación (H-02 / N-1002)
- Órdenes (process manager + outbox + purchases), Pagos (simulador PAN `0002`), Ticketing (ticket + payload S3 + evento interno notificación), colas SQS FIFO (D-0006), workers en los 4 servicios.
- **AC heredadas:** H-02 AC1 (`TICKETS_ISSUED` con ticket), AC2 (disponibilidad −1 exacto), AC3 (PAN `0002` ⇒ `FAILED` + asiento liberado + disponibilidad restaurada), AC4 (estados expuestos en `GET /purchases/{id}`). Cierra H-01 AC2 (invalidación real por compra).

### S03 — Idempotencia observable (H-03 / N-1003)
- Idempotency-key store HTTP (put condicional por key + hash de body → mismatch = 422), dedup de comandos por correlación (dedup id + `*_idempotency`), log "duplicado descartado", caso agotado ⇒ 409/422.
- **AC heredadas:** H-03 AC1 (replay ⇒ misma compra, disponibilidad/tickets idénticos), AC2 (comando duplicado ⇒ decremento único + log), AC3 (key distinta + asientos tomados ⇒ 409/422 con motivo).

### S04 — Documentación de portfolio y scripts de demo (H-04 / N-1004)
- README raíz: qué es, decisión de dominio (link ADR D-0002), diagrama (docs/architecture.md), quickstart copy-paste (emulador + 4 servicios en 1 script), mapa de patrones (D-0008), NFRs medidos.
- `demo-cache.ps1` (MISS→HIT→invalidación con valores), `demo-idempotency.ps1` (misma key ⇒ misma compra; disponibilidad antes/después), medición p95 (NFR-5) a `brain/evidence/`.
- **AC heredadas:** H-04 AC1..AC4. Gate final: QA adversarial (división + security.md completo OK/FALLA).

**Dependencias:** S01 → S02 → S03 → S04 (estrictas: cada una construye sobre la anterior; S02 es la más gruesa — L).

## 5. Estrategia QA

| Nivel | Qué | Umbral de aprobación |
|---|---|---|
| **Unit** (por servicio) | Dominio + handlers: máquina de estados de la saga, validadores, política del simulador de pagos, dedup (sin red) | Verde + cobertura ≥70% de cada `Core` (NFR-6); test que falle sin la implementación (AC antes de código) |
| **Integración** (emulador real) | Repos DynamoDB (condicionales), colas SQS FIFO end-to-end, S3 put/get, idempotency store con put condicional; arranque con emulador vivo (docker) | Verde contra `aws-emulator` corriendo; fallo = no done |
| **Funcional/e2e** | Script de flujo completo: consulta→compra aprobada→ticket; compra rechazada→compensación; replay; reinicio de microservicios (NO emulador) → estado vivo | Cada AC del backlog con comando+salida archivada en `brain/evidence/` |
| **Cómo se DEMUESTRA idempotencia** | 1) Replay HTTP misma key ⇒ mismo `purchase_id`, delta disponibilidad = 0, tickets = mismos ids. 2) Comando interno duplicado (publicación doble con igual correlación) ⇒ un solo decremento + log `duplicado descartado` visible | Evidencia impresa por `demo-idempotency.ps1` (H-04 AC2) |
| **Cómo se DEMUESTRA cache** | MISS→HIT (mismo valor) → compra → lectura devuelve valor NUEVO (invalidación); header `X-Cache` en cada respuesta | Evidencia impresa por `demo-cache.ps1` (H-04 AC3) |
| **Adversarial (QA F4)** | Payload basura/fuzz manual (crash-coherencia), PANs inválidos, seats duplicados en un mismo request, key repetida con body distinto (⇒422), gate de división: cero llamadas a nube real (pin CA), checklist security.md completo | Reporte OK/FALLA por ítem con evidencia |

## 6. Comandos de verificación (estampados en brain/config.yaml)

```yaml
commands:
  build: ["dotnet", "build", "TicketFlow.sln"]
  test:  ["dotnet", "test", "TicketFlow.sln", "--nologo"]
  run:   ["pwsh", "./scripts/run-local.ps1"]   # crea S01: emulador check + creds + 4 servicios
```

## 7. Riesgos técnicos residuales

| Riesgo | Mitigación |
|---|---|
| Estado del emulador en memoria (se borra al reiniciar contenedor) | Declarado en quickstart; AC4 solo exige sobrevivir microservicios; seed idempotente al arranque |
| Credenciales/CA regeneradas por corrida del emulador | `run-local.ps1` las extrae dinámicamente; cero hardcodeo |
| Fidelidad DynamoDB (ventana DELETING, timing ACTIVE) | Creación de tablas con poll (verificado en probe); sin recreate en caliente |
| Saga asíncrona → flakiness en tests e2e | Polling con timeout explícito en scripts; `MessageGroupId=purchase_id` da orden determinística por compra |
| SNS fan-out sin entrega (gap) | Fuera del MVP (D-0006); evento interno + log; revisar en V1 con release nuevo del emulador |
