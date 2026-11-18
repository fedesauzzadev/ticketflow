# QA V2 adversarial - evidencia cruda (2026-11-18, HEAD 171638d)

Auditor: QA adversarial NSDD (F6, entrega V2). 100% evidencia propia (cero confianza en evidencia del dev).
Logs completos: %TEMP%/opencode/qa-v2-18/*.log. Servicios levantados/matados por QA. Encoding ASCII.

## 0. Entorno

- Host con carga alta (~40 contenedores docker de otros proyectos, uno restarteando) - se nota en timings.
- Previo: aws-emulator Up 2 dias (sucio), 4 servicios TicketFlow corriendo desde 15/11 -> matados por QA antes de empezar.
- Nota: existe brain/evidence/2026-11-15-qa-v2-adversarial.md de una sesion QA V2 interrumpida (suite+ci+workflow-lint); NO se uso como fuente - todo re-ejecutado hoy.

## 1. Suite completa (emulador arriba, servicios abajo)

| Corrida | Resultado | Detalle | Tiempo |
|---|---|---|---|
| suite1 | **exit 1 (FLAKE)** | Orders 90/92: Scan_finds_only_due_holds Expected:["pur-89ebb691..."] Actual:[] + SagaEndToEnd Persistent_failure_on_release_seats Expected:FAILED Actual:SEATS_RESERVED (45 s, l.364) | 100 s |
| suite2 | exit 0 | **186/186** (P8+T5+C64+Prov17+O92), 0 omitidos | 73 s |
| suite3 | exit 0 | 186/186; cobertura Core: Payments 93.02 / Ticketing 87.32 / Catalog 81.34 / Orders 88.43 (umbral >=70 verde) | 160 s |
| orders-iso1 | exit 0 | Orders solo: 92/92 (el e2e flakey pasa en 9 s aislado) | 66 s |

## 2. ci.ps1 (N-3001)

| Corrida | Resultado | Tiempo |
|---|---|---|
| ci1 | **exit 1 CULPABLE: tests (FLAKE, arbol limpio)** - Persistent_failure_on_release_seats [FAIL] a los 2,95 s del host | 174 s |
| ci2-red (break propio v1: Assert.True(false)) | exit 1 CULPABLE: **build** (error xUnit2020 por -warnaserror) - bonus: culpable build demostrado | 21 s |
| ci3-red (break propio v2: Assert.Fail) | exit 1 **CULPABLE: tests** - solo mi test Qa_breaks_the_gate_on_purpose [FAIL]; revertido | 169 s |
| ci4-green | **exit 0** - preflight 0.6 / build 8.3 / tests 76.6 / infra-verify 4.9 / workflow-lint 0.5 | 91 s |
| ci5 -Demos | **exit 1 CULPABLE: tests (FLAKE)** - Persistent_release_seats + Seat_freed_by_expiry_is_rebuyable [FAIL]; demos ni corrieron | 161 s |
| ci6 -Demos | **exit 0** - 5 etapas + demos 158.2 s (cache/idempotency/resilience/ttl exit 0) | 261 s |

Flake en arbol limpio hoy: **3 de 7 corridas full-suite** (suite1, ci1, ci5). Sesion previa (11-15, mismo HEAD): 3 fallas mas (ci3 Scan, orders-run2 Scan+Hold_expires, pair-run1). Mecanismo (code read + repros): tabla purchases y colas saga COMPARTIDAS entre collections xUnit paralelas de Orders.Tests (PurchaseRepositoryTests paralelo al sweeper hosted de PaymentEndpointTests y a los e2e "orders-saga") y entre proyectos de test concurrentes contra un solo emulador. En V1 (63 tests Orders) estable; V2 agrego 29 tests con mas escritores paralelos de estado compartido.

## 3. AC5 / F-V1-R3-01 (DLQ sucia, evidencia propia)

- Emulador 2 dias sucio: DLQ payments **23 entries** (>10, trigger exacto), catalog 97, orders 0 (GET /admin/dlq).
- demo-resilience corrida x2 por QA sobre esa sesion: **exit 0, 15/15 PASS** (encuentra SU entrega; DLQ queda 24). 47 s c/u.

## 4. Provision from scratch (N-3002)

| Paso | Resultado | Tiempo |
|---|---|---|
| docker rm -f aws-emulator + provision.ps1 | exit 0, 15 recursos OK (6 tablas + 8 colas + bucket) | 225 s |
| -Verify | exit 0, 15/15 OK, 0 FALTA/DIFF/WARN, "coinciden con el manifiesto" | ~20 s |
| re-apply x2 + -Verify x3 | todos exit 0, verify identico | ~60 s |
| **ADVERSARIAL: emulador recreado VACIO + -Verify** | **exit 2 (esperado 1)**: 14 FALTA impresos y luego "ERROR: Value cannot be null. (Parameter 'source')" - crash en seccion buckets; el FALTA del bucket jamas se reporta (F-V2-03) | ~45 s |
| ADVERSARIAL: manifiesto editado a mano (rename cola) | ManifestGuardTests.Queues_match_code_constants_and_shape **FAIL** (1 err/2 ok) -> restaurado -> 3/3 OK. El guard SI pilla la edicion (en la etapa tests del CI) | ~1 min |

## 5. Hold TTL funcional + adversarial (N-3003) - servicios con HOLD_TTL_SECONDS=15, evt-tiny de 1 asiento

- **AC9 flujo completo** sobre infra recien aprovisionada: compra 2 asientos -> TICKETS_ISSUED + 2 tickets + qr_s3_key en bucket + pago 241 + disponibilidad 498 exacta.
- **demo-ttl x2** (TTL 15 s): exit 0, 20 s c/u, TODO PASS con valores (retencion 496->495, EXPIRED, liberacion, 409, replay, re-compra TICKETS_ISSUED). Tercera corrida via ci6 -Demos con TTL default 90: TODO PASS.
- **Bateria hold** (18 checks): H1-H4b, H6, H11, H12 = 18 PASS + H2 re-verificado por log:
  - H2 asiento agotado: hold B sobre evt-tiny en 0 -> **422 sincronico** ("compra rechazada por disponibilidad: evento evt-tiny tiene 0, se pidieron 1"), sin compra creada, sin drift; hold A expiro a tiempo; comprador C tomo el asiento liberado -> TICKETS_ISSUED. CORRECTO (los 3 [FAIL] iniciales eran expectativas malas de mi script: esperaba FAILED async, el precheck sync rechaza antes y mas fuerte).
  - H3: 4 holds concurrentes -> delta exacto = #SEATS_RESERVED.
  - H4a pago a 0,8 s del vencimiento: 202 -> TICKETS_ISSUED (claim protege contra sweeper).
  - H4b pago a +2,5 s del vencimiento: 409 hold-expired + EXPIRED reason "reserva expirada sin pago (TTL 15 s)" + avail restaurada exacta + sin ticket + sin segunda liberacion.
  - H6 replay misma key sobre EXPIRED: 200 misma compra EXPIRED, delta 0.
  - H11 doble submit: 202 luego 200 replay; payment sobre TICKETS_ISSUED: 409 invalid-state.
  - H12: id malformado 400; pur-...dead inexistente 404 purchase-not-found; body basura ignorado (estado decide).
- **H7 hostiles**: 18 payloads basura (JSON roto/escapado) -> 400 invalid-body, 0x5xx, proceso vivo. Re-test tipado con JSON valido: hold:"yes"/1/{}/[true] -> **400 invalid-purchase-request** (como declara el nodo); hold:null -> **201** (tratado como ausente -> compra V1 normal; Info); card.hold=true anidado -> 201 (hold ignora campo extra del card).
- **H10 / AC7 crash**: hold -> 4 servicios matados a t=7 s de 15 -> hold expiro DURANTE el downtime -> servicios rearrancados -> sweeper lo expira: EXPIRED "reserva expirada sin pago (TTL 15 s)" + disponibilidad restaurada 492 exacta.
- **H8 fail-fast**: HOLD_TTL_SECONDS=abc / 4 / 3601 -> InvalidOperationException accionable al arranque (Purchase.cs:87, Program.cs:20); proceso muere (abc y 4 verificados HasExited; 3601 con excepcion en err - unit test HoldMechanics cubre el throw).
- **H9 zombi-tier A (PAN aprobada)**: Payments muerto ANTES del claim -> claim 202 (authorize trabado en cola) -> avail 491 -> a los ~60 s zombie cutoff: **EXPIRED "pago iniciado hace mas de 60 s sin completarse - reserva reclamada por contingencia"** + avail 492 restaurada -> Payments rearrancado -> authorize procesado: **pago APPROVED registrado**, payment-approved IGNORADO por CAS (compra sigue EXPIRED), SIN ticket, avail 492 estable. Ventana destructiva confirmada: pago aprobado sin ticket sobre compra EXPIRED; el reason NO menciona que el pago pudo haberse aprobado despues (F-V2-05).
- **H9 zombi-tier B (PAN 0002, compensacion tardia / AC6)**: mismo setup -> EXPIRED + release #1 (492) -> Payments rearranca -> authorize DECLINED -> compensacion release-seats LLEGAN TARDE -> **avail queda 492 exacta (sin doble liberacion)**, compra EXPIRED estable (CAS ignora eventos tardios). AC6 segundo Given VERIFICADO.
- **Mini-stress mixto** (6 holds concurrentes en evt-legacy 250): 6 SEATS_RESERVED delta 6 exacta -> 3 pagadas (202) + 3 abandonadas -> final 3 TICKETS_ISSUED + 3 EXPIRED, avail 247 = 250-3 exacta.

## 6. Regresion V1

- stress-test N=20 X=5: PASS (5+15, 0+5=5==X, 0 sin terminal), 67 s. NOTA: -X alinea disponibilidad pre-comprando 487 (evt-mj quedo en 0 por diseno del script - no es over-sell).
- Idempotencia 4x misma key: 201,200,200,200 -> 1 compra TICKETS_ISSUED, avail 246 exacta.
- trace-purchase: exit 0, 4 servicios, cronologica, saga completa 588 ms, aislada, replays visibles.
- demo-resilience sobre DLQ sucia: 15/15 x2 (arriba). demo-cache/idempotency exit 0 via ci6.
- p95: GET availability x40 HIT: p50 13,4 ms / **p95 24,25 ms** (<50 OK, host cargado); saga completa x10: **p95 850 ms** (<2000 OK).

## 7. Workflow-lint adversarial (F-V2-02, repro propia)

- Copia de ci.yml con line 35 `run: pwsh -File scripts/other-gate.ps1` (comentarios intactos) -> validate-workflow.ps1 -Path copia: **exit 0, 9/9 invariantes OK** (la "invariante central" pasa por menciones en COMENTARIOS lines 3/7). El lint no detecta que el run real ya no invoca el gate. Restaurado (el original nunca se toco).

## 8. Estatico / DoD

- Secrets grep (AKIA/aws_secret/private_key en src,tools,scripts,infra,.github): 0 hits. TODO/FIXME: 0. shortcut: 3 documentados (aceptables).
- CHANGELOG [Unreleased] documenta V2 completo (hold/CI/IaC, suite 186). Version 0.2.0 en Directory.Build.props.
- **README stale**: l.282 "Tests: 155/155" (real 186/186); l.81 "-Demos... corre las 3 demos" (corre 4: +demo-ttl) -> F-V2-04.
- Estado final: emulator Up con 15 recursos provisionados, servicios detenidos, working tree limpio.