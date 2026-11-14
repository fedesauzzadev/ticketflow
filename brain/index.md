# Brain Index

> Entry map. Always read this first. Keep under 150 lines — if it grows, summarize or split.

## Project at a glance
TicketFlow: portfolio backend de venta de entradas para eventos de alta demanda — microservicios .NET 10 orquestados (saga), idempotencia observable, cache hit/miss observable, todo sobre servicios AWS del emulador oficial local (:9297, HTTPS+SigV4, CA propia). Dominio = PROPUESTA aprobada en G2. Plan técnico Fase 2: factory/plan.md.

## Active slice
**mvp** (V0, H-01..H-04 de factory/backlog.md): N-1001 → N-1002 → N-1003 → N-1004. Estado: **N-1001 verified** (S01: Catálogo+cache+DynamoDB) · **N-1002 verified** (S02: saga completa con compensación, 4 servicios, cierra H-01 AC2) · **N-1003 verified** (S03: idempotencia observable — replay/dedup/agotado, 96/96 tests ×2 corridas, H-03 cerrada) · **N-1004 verified** (S04: README+quickstart+scripts demo+p95 NFR-5 OK — MVP H-01..H-04 COMPLETO) - Gate QA F4 ejecutado: SHIP (factory/qa-report.md: 0 criticos; Mayores F-01 timeout bajo peer colgado, F-02 LICENSE/CHANGELOG/git faltantes). Sprints S01..S04 en factory/plan.md.

## Nodes
| ID | Title | Type | Size | Status | Key links |
|----|-------|------|------|--------|-----------|
| N-1001 | Catálogo y disponibilidad de eventos con cache observable | feature | M | verified | → D-0002, D-0005, D-0007 · evidencia 2026-11-13-S01 |
| N-1002 | Saga de compra de entradas multi-servicio con compensación | feature | L | verified | depends_on N-1001 · → D-0004, D-0006 · evidencia 2026-11-13-S02 |
| N-1003 | Idempotencia observable de la compra (replay y duplicados) | feature | M | verified | depends_on N-1002 · → D-0009 (claim condicional + hash canónico + 422s + log de dedup) · evidencia 2026-11-14-S03 |
| N-1004 | Documentación de portfolio y scripts de demo | feature | M | verified | depends_on N-1001..N-1003 · → D-0002, D-0008 · evidencia 2026-11-14-S04 |

## Decisions
| ID | Título | Reversibility |
|----|--------|---------------|
| D-0001 | init-brain no-interactivo — defaults asumidos | easy |
| D-0002 | dominio del ejercicio — TicketFlow (flash-sale ticketing) | medium |
| D-0003 | stack .NET 10 + AWS SDK v4 contra el emulador (reglas: ServiceURL+AuthenticationRegion, CA pin, timeouts) | hard |
| D-0004 | saga por ORQUESTACIÓN — process manager en Órdenes | medium |
| D-0005 | estado en DynamoDB del emulador (decremento condicional verificado) | medium |
| D-0006 | mensajería SQS FIFO; SNS fuera del MVP (fan-out sin entrega en emulador) | medium |
| D-0007 | cache in-app HybridCache + invalidación (X-Cache observable) | easy |
| D-0008 | estructura solución (Api+Core por servicio + Shared.Infrastructure) y mapa de patrones | medium |
| D-0009 | idempotencia observable S03: claim condicional de key, replay 200, mismatch/agotado 422, dedup por correlación | medium |

## Evidence
- fix-2026-11-14-qa-f01-f02-f03.md — RONDA FIX 1/2 POST-QA (F-01/F-02/F-03, dev): F-01 timeout efectivo (HttpClient.Timeout=ClientConfig.Timeout + MaxErrorRetry=0; unit 2/2 + e2e docker pause: GET/POST 500 a los 10,05 s —antes 150-240 s+—, workers loguean errores durante pausa, unpause ⇒ GET 200 + saga TICKETS_ISSUED completa); F-02 LICENSE MIT + CHANGELOG 0.1.0 + Version 0.1.0 en props (.gitignore verificado); F-03 umbral cobertura ≥70% Core cableado (coverlet.msbuild+CollectCoverage, condicional al env del emulador): suite 98/98 (96+2 nuevos) con 81,2/85,5/92,8/87,3% por Core, gate probado fallando (exit 1) y pasando; build 0/0 ×3. Enmienda D-0003, learnings L-011/L-012.
- 2026-11-14-qa-f4-adversarial.md - QA F4 VERIFICADO (gate adversarial del MVP, veredicto SHIP): build limpio 0/0 + 96/96 x2 propia; cobertura medida (Core 81-93%, umbral no cableado F-03); 26 payloads hostiles 0x5xx; idempotencia concurrente 4xmisma key = 1 compra; CERO oversell en 3 carreras (incluida 6x201 sobre 3 asientos); replay post-FAILED sin efectos; demos exit 0 propios; p95 propio 5,8/22,7/768 ms; quickstart frio OK; endpoint AWS real rechazado al arranque; docker pause: requests cuelgan 150-240 s+ sin timeout efectivo (F-01 Mayor) con recovery limpia.
- 2026-11-14-S04-n1004-verification.md — S04/N-1004 VERIFICADO (MVP completo): quickstart corrido desde cero (emulador detenido → run-local → flujo manual consulta→compra→ticket+compensación); demo-cache.ps1 exit 0 (MISS 499→HIT 499→compra→MISS 498); demo-idempotency.ps1 exit 0 (replay 200 misma compra+mismos tickets, disponibilidad 497→497, mismatch 422, agotado 422 con variante SEED_EVENTS); NFR-5 medido n=50 con HttpClient fiel: GET HIT p95 1,4 ms · POST p95 20,7 ms · saga p95 790 ms (los 3 < objetivo, raw en evidencia); 96/96; fix blocker run-local.ps1 camino frío (L-009) y lección de fidelidad del cliente de medición (L-010).
- 2026-11-14-S03-n1003-verification.md — S03/N-1003 VERIFICADO: build 0 err/0 warn; 96/96 tests (×2 corridas contra emulador real, 8 nuevos de S03); AC1 replay misma key+body ⇒ 200 misma compra (TICKETS_ISSUED, mismos ticket_ids), disponibilidad 498→498 delta 0, mismatch body ⇒ 422 idempotency-key-reused; AC2 reserve-seats duplicado inyectado (misma correlación, dedup id distinto) ⇒ un solo decremento (498 intacta) + log real del Catálogo "duplicado detectado: efecto descartado"; AC3 key nueva sobre evento agotado ⇒ 422 seats-unavailable con motivo, sin purchase_id ni efectos parciales; fixes de tests L-008 documentados.
- 2026-11-13-S02-n1002-verification.md — S02/N-1002 VERIFICADO: build 0 err/0 warn; 88/88 tests (4 proyectos, ~25 de integración contra emulador real incl. saga e2e a nivel componente); 4 servicios reales vía run-local.ps1; AC1 TICKETS_ISSUED con ticket+QR S3+pago APPROVED (~1,8 s de saga); AC2 500→499 exacto + X-Cache MISS (cierra H-01 AC2); AC3 PAN 0002 ⇒ FAILED con motivo + disponibilidad restaurada 499 (compensación en logs); AC4 los 5 estados visibles por GET /purchases/{id}; NFR-4 spots 400/404 sin internals.
- 2026-11-13-S01-n1001-verification.md — S01/N-1001 VERIFICADO: build 0 err/0 warn; 34/34 tests (24 unit + 10 integración contra emulador real); AC1 MISS→HIT mismo valor, AC3 lista ≥2, AC4 byte-identical tras restart del microservicio (emulador intacto), AC2 mecanismo (invalidación + valor nuevo en test de integración); 404/400 ProblemDetails sin internals.
- 2026-11-13-emulator-verification.md — verificación real del emulador: 19/20 OK (DynamoDB 7/7 incl. condicionales, SQS FIFO 4/4, S3 4/4, SNS fan-out FAIL con fallback). + probe Program.cs, output, CA de la corrida.

## Open risks / blockers
- F-01 (QA F4, Mayor): RESUELTO 2026-11-14 (ronda fix 1/2) — timeout 10 s efectivo (`HttpClient.Timeout = ClientConfig.Timeout` + `MaxErrorRetry=0` en PinnedCaHttpClientFactory/EmulatorAwsClients, enmienda D-0003, L-011): docker pause ⇒ 500 a los 10,05 s (antes 150-240 s+); recovery total. Evidencia fix-2026-11-14-qa-f01-f02-f03.md.
- F-02 (QA F4, Mayor): RESUELTO 2026-11-14 salvo git init/commit — LICENSE (MIT), CHANGELOG 0.1.0 y `<Version>0.1.0</Version>` creados; git init queda para post-G2 por decisión del orquestador.
- Dominio pendiente de aprobación del Negocio (gate G2).
- **`pwsh` no instalado** (solo PS 5.1): `commands.run` de config.yaml requiere ajuste o instalación de PS7 (L-003). run-local.ps1 es compatible con ambos.
- Estado del emulador es en memoria: NO reiniciar el contenedor durante la demo (declarado en plan §7).
- SNS fan-out sin entrega en el emulador → notificación por evento interno + log; revisar en V1 (L-002).
- Cobertura NFR-6 (≥70% de los Core): umbral CABLEADO desde 2026-11-14 (coverlet.msbuild + CollectCoverage, scope=solo Core por suite, activo con env del emulador; L-012). Deuda V1: .Api sin tests in-proc (QA F-07).
- Mensaje SQS recibido y no borrado (consumidor muerto) demora la compra ~30 s (visibility timeout FIFO); handlers idempotentes lo toleran — backoff/DLQ en H-06 (V1).

## Registry highlights
- `EmulatorAwsClientFactory` → `src/Shared/TicketFlow.Shared.Infrastructure/EmulatorAwsClients.cs` (+ `PinnedCaHttpClientFactory`): composición SDK v4 contra el emulador (D-0003/L-001) — DynamoDB, **SQS y S3** desde S02. En registry.
- `Saga` (Shared.Infrastructure/Saga) → `SagaMessage.cs` (envelope+contratos) + `SqsSagaTransport.cs` (ensure FIFO tolerante a carreras, send con group/dedup ids, `SqsSagaWorker` base con guard L-005): transporte completo de la saga — los 3 servicios nuevos + catálogo lo consumen. Candidato a registry.
- `DynamoDbTables.EnsureAsync` → Shared.Infrastructure/DynamoDbTables.cs: creación idempotente tolerante a carreras (L-007) — la usan los 4 repos nuevos.
