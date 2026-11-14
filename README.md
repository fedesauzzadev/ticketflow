# TicketFlow

**Backend de venta de entradas para eventos de alta demanda** (flash-sale ticketing): 4 microservicios .NET 10 orquestados con saga y compensación, cache observable, idempotencia observable — **100% sobre servicios AWS corriendo en un emulador oficial en local** (`https://127.0.0.1:9297`). Cero nube real, cero costos, cero secrets en el repo.

> Un evaluador clona, corre un comando, y ve con curl: catálogo con cache (`X-Cache: HIT|MISS`), una compra que atraviesa **reserva → pago → emisión**, el rechazo que compensa, y la repetición que no duplica efectos.

**Decisión de dominio:** venta de entradas de alta demanda — razonada en el ADR [D-0002](brain/decisions/2026-11-13-D0002-dominio-ticketflow.md) (el resto de las decisiones: `brain/decisions/`).

## Arquitectura

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

Detalle completo (servicios, tablas, saga paso a paso, ADRs): [docs/architecture.md](docs/architecture.md).

## Quickstart

**Prerrequisitos:** Docker Desktop · SDK de **.NET 10** · Windows con PowerShell 5.1+ (o PS 7).

**Un comando** (desde la raíz del repo):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
```

Eso: (1) levanta el contenedor `aws-emulator` si está caído (lo buildea si no existe), (2) extrae las credenciales y la CA **de esa corrida** del emulador (se regeneran en cada arranque — nada hardcodeado; caen en `.secrets/`, que está gitignored), (3) buildea la solución (0 warnings) y (4) levanta los 4 servicios:

| Servicio | URL | Prueba |
|---|---|---|
| Catálogo | http://localhost:5080 | `GET /events` |
| Órdenes | http://localhost:5081 | `GET /health` |
| Pagos | http://localhost:5082 | `GET /health` |
| Ticketing | http://localhost:5083 | `GET /health` |

Cuando veas `==> Todo listo:` el flujo completo está operativo. `Ctrl+C` detiene los 4 servicios (el emulador queda; su estado persiste mientras no reinicies el contenedor).

## Probar el flujo a mano

Copia y pega en otra ventana de PowerShell. Cada bloque es independiente salvo los ids que generan los anteriores.

**1) Catálogo y cache observable** — dos lecturas seguidas: la primera `MISS`, la segunda `HIT` con el mismo valor:

```powershell
Invoke-RestMethod http://localhost:5080/events   # lista de eventos (seed: evt-mj-tribute-night, evt-legacy-tour)

$a = Invoke-WebRequest -UseBasicParsing http://localhost:5080/events/evt-mj-tribute-night/availability
"$($a.Headers['X-Cache']) $($a.Content)"          # MISS {"event_id":...,"available":500}
$a = Invoke-WebRequest -UseBasicParsing http://localhost:5080/events/evt-mj-tribute-night/availability
"$($a.Headers['X-Cache']) $($a.Content)"          # HIT  (mismo valor)
```

**2) Compra aprobada** (PAN que NO termina en `0002` ⇒ aprobada). La saga corre async: `PENDING → SEATS_RESERVED → PAYMENT_APPROVED → TICKETS_ISSUED`:

```powershell
$key  = 'demo-' + [guid]::NewGuid().ToString('N')
$body = @{ event_id = 'evt-mj-tribute-night'; seats = 1
           card = @{ pan = '4111111111111111'; holder = 'Evaluador'; expiry = '12/29'; cvv = '123' } } | ConvertTo-Json -Depth 4
$r = Invoke-WebRequest -UseBasicParsing http://localhost:5081/purchases -Method Post `
     -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = $key } -Body $body
$r.StatusCode; $r.Content                                 # 201 {"purchase_id":"pur-...","status":"PENDING",...}
$pid = ($r.Content | ConvertFrom-Json).purchase_id

do { $p = Invoke-RestMethod http://localhost:5081/purchases/$pid; Start-Sleep -Milliseconds 300 }
while ($p.status -notin 'TICKETS_ISSUED','FAILED')
$p                                                        # TICKETS_ISSUED + ticket_ids
Invoke-RestMethod http://localhost:5082/payments/$pid     # evidencia del pago: APPROVED, amount
Invoke-RestMethod http://localhost:5083/tickets/$($p.ticket_ids[0])   # entrada + qr_s3_key (S3)
```

**3) Compra rechazada ⇒ compensación** (PAN terminada en `0002` ⇒ `declined`; el asiento se libera y la disponibilidad vuelve a su valor previo):

```powershell
$body = @{ event_id = 'evt-legacy-tour'; seats = 1
           card = @{ pan = '4000000000000002'; holder = 'Evaluador'; expiry = '12/29'; cvv = '123' } } | ConvertTo-Json -Depth 4
$antes = (Invoke-RestMethod http://localhost:5080/events/evt-legacy-tour/availability).available
$r = Invoke-WebRequest -UseBasicParsing http://localhost:5081/purchases -Method Post `
     -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = 'demo-' + [guid]::NewGuid().ToString('N') } -Body $body
$pid2 = ($r.Content | ConvertFrom-Json).purchase_id
do { $p2 = Invoke-RestMethod http://localhost:5081/purchases/$pid2; Start-Sleep -Milliseconds 300 }
while ($p2.status -notin 'TICKETS_ISSUED','FAILED')
$p2                                                       # FAILED + reason "pago rechazado por el emisor..."
(Invoke-RestMethod http://localhost:5080/events/evt-legacy-tour/availability).available -eq $antes   # True (restaurada)
```

## Scripts de demo (evidencia imprimible)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-cache.ps1          # MISS -> HIT -> compra -> MISS con valores
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-idempotency.ps1    # misma key => misma compra; 422s
```

Ambos exit 0 si la demo pasa (distinto de 0 si falla cualquier check). `demo-idempotency` demuestra también el caso **agotado** cuando existe un evento con ≤ 7 asientos: para verlo, levanta el entorno con un evento chico antes de correr el script:

```powershell
$env:SEED_EVENTS = 'evt-mj-tribute-night|Michael Jackson Tribute Night|2027-03-12T21:00:00Z|120.50|500;evt-demo-soldout|Demo Sold Out|2027-06-01T20:00:00Z|10.00|1'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
```

(formato `id|nombre|fecha|precio|disponibilidad`; el seed es condicional — no pisa eventos existentes)

## Traza distribuida de una compra (correlation-id)

Cada compra es su propio correlation-id (`purchase_id`, D-0011): nace en `POST /purchases`, viaja en el envelope de la saga por SQS y aparece en el log JSON de los 4 servicios. `run-local.ps1` redirige los logs a `.artifacts\logs\<svc>.log` (cada arranque los trunca), así que la traza de punta a punta se filtra post-hoc:

```powershell
# compra aprobada o compensada (PAN termina 0002): pasos en orden cronológico + guardián anti-PAN/CVV
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\trace-purchase.ps1 -PurchaseId pur-<32hex>

# comando crudo equivalente (sin script):
Select-String ".artifacts\logs\*.log" -Pattern 'pur-<32hex>' -SimpleMatch

# seguir los logs de un servicio en vivo mientras comprás:
Get-Content .artifacts\logs\orders.log -Wait
```

`trace-purchase.ps1` imprime la tabla `timestamp | servicio | nivel | mensaje` ordenada cronológicamente, un resumen por servicio y sale con código distinto de 0 si la traza contiene un PAN (13-19 dígitos) o un CVV.

## Mapa de patrones aplicados

| Patrón | Dónde vive |
|---|---|
| **Repository** (por agregado, puertos en Application) | Interfaces `src/Services/*/Core/Application/` (p.ej. `IEventRepository.cs`, `Orders` → `Ports.cs`) · impl DynamoDB en `src/Services/*/Core/Infrastructure/DynamoDb*.cs` |
| **Cache-aside + invalidación explícita** | `src/Services/Catalog/TicketFlow.Catalog.Core/Application/AvailabilityService.cs` (HybridCache, TTL 30 s) · header `X-Cache` y endpoint interno de invalidación en `Catalog.Api/Program.cs` · invalida el worker `CatalogSagaHandler.cs` al reservar/liberar |
| **Transactional outbox** | `src/Services/Orders/TicketFlow.Orders.Core/Infrastructure/DynamoDbOutbox.cs` (tabla `orders_outbox`) + relay publicador a SQS en `src/Services/Orders/TicketFlow.Orders.Api/Workers.cs` (`OutboxRelayWorker`) |
| **Process manager / saga orchestrator** | `src/Services/Orders/TicketFlow.Orders.Core/Application/SagaProcessManager.cs` + máquina de estados en `Domain/Purchase.cs` |
| **Idempotency-key store** (put condicional + hash de body) | `src/Services/Orders/TicketFlow.Orders.Core/Application/Idempotency.cs` + `Infrastructure/DynamoDbIdempotencyStore.cs` (tabla `orders_idempotency`) · claims por servicio en las tablas `*_idempotency` de cada uno |
| **Dedup handler** (comandos duplicados, dos capas) | Capa transporte: `src/Shared/TicketFlow.Shared.Infrastructure/Saga/SqsSagaTransport.cs` (SQS FIFO `ContentBasedDeduplication`, dedup id `{type}:{purchase_id}`) · capa aplicación: `CatalogSagaHandler.cs`, `PaymentsSagaHandler.cs`, `TicketingSagaHandler.cs` (log "duplicado detectado: efecto descartado") |
| **Adapter de infraestructura** (aísla el emulador) | `src/Shared/TicketFlow.Shared.Infrastructure/EmulatorAwsClients.cs` + `EmulatorAwsOptions.cs` (endpoint + CA pineada + timeouts; una llamada a AWS real es imposible por diseño) |
| **Mediator-equivalente** (dispatcher in-process) | Workers base de `Shared.Infrastructure/Saga/` → handlers registrados por DI en cada `*.Api/Program.cs` |

## Verificación

- **Tests:** `dotnet test TicketFlow.sln` → **96/96** (unit + integración contra el emulador real; requiere el emulador arriba y `AWS_EMULATOR_*` en el env — `run-local.ps1` muestra cómo se extraen).
- **Evidencias por slice** (comando + salida archivada): [S01 catálogo/cache](brain/evidence/2026-11-13-S01-n1001-verification.md) · [S02 saga+compensación](brain/evidence/2026-11-13-S02-n1002-verification.md) · [S03 idempotencia](brain/evidence/2026-11-14-S03-n1003-verification.md) · [S04 quickstart/demos/mediciones (este slice)](brain/evidence/2026-11-14-S04-n1004-verification.md) · [verificación del emulador](brain/evidence/2026-11-13-emulator-verification.md).

### Performance medida (NFR-5, local, 50 iteraciones tras warmup)

| Operación | p50 | p95 | Objetivo | Veredicto |
|---|---|---|---|---|
| `GET /events/{id}/availability` (cache HIT) | 0,6 ms | **1,4 ms** | < 50 ms | ✅ |
| `POST /purchases` (201 inmediato) | 9,3 ms | **20,7 ms** | < 200 ms | ✅ |
| Saga completa `POST → TICKETS_ISSUED` | 667 ms | **790 ms** | < 2 s | ✅ |

Números crudos y método: [evidencia S04](brain/evidence/2026-11-14-S04-n1004-verification.md).

## Limitaciones conocidas (fidelidad del emulador)

- **El estado del emulador vive en memoria del contenedor**: reiniciar los microservicios NO pierde datos; reiniciar el **contenedor** sí (todo se re-seedea al arrancar). No reinicies el contenedor en medio de una demo.
- **SNS sin entrega** (control plane OK, data plane no): la notificación del ticket es un **evento interno + log visible** en Ticketing (gap verificado: [evidencia](brain/evidence/2026-11-13-emulator-verification.md)); SNS queda para V1.
- **Sin Redis real** (el emulador no expone protocolo Redis): la cache es **in-app** (`HybridCache`) con TTL 30 s + invalidación explícita — el header `X-Cache: HIT|MISS` es observable igual que en producción.
- Un mensaje SQS recibido y no borrado demora ~30 s (visibility timeout FIFO) en re-procesarse; los handlers son idempotentes y lo toleran. Backoff/DLQ: V1.
- **No correr la suite de tests con los servicios levantados**: comparten colas SQS y se roban mensajes (ver [L-008](factory/learnings.md)).
