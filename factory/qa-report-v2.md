# QA Report V2 - TicketFlow (H-08 CI+IaC, H-09 hold TTL) - Fase F6 adversarial
## RONDA 2 (retest post fix 1, commit 33006e9) - VEREDICTO FINAL: **SHIP**

Fecha: 2026-11-18 - Retest acotado por QA adversarial (dev loop fix 1). Evidencia cruda 100%
propia: brain/evidence/2026-11-18-qa-v2-r2-retest.md (logs en %TEMP%/opencode/qa-v2-r2/).
Declaracion del dev verificada punto por punto; cero confianza previa.

**SHIP.** El gate de CI - el hallazgo Mayor F-V2-01 - quedo determinista: 7/7 corridas del
hammer en arbol limpio exit 0 (35/35 etapas OK; ronda 1: 3/7 fallaban), el mecanismo esta
cerrado estructuralmente (DisableTestParallelization de Orders.Tests vigente en cualquier
regimen + -m:1 en el gate; grep replicado: los otros proyectos no tocan purchases/colas saga)
y empiricamente en el regimen README (suite x2 sin -m:1: 186/186). El riesgo residual
declarado por el dev (3 rojos intermedios en Persistent_failure_on_release_seats) NO se
reprodujo en ninguna corrida. Los 4 hallazgos menores/info cierran con repro propia.

### Hammer del gate (7x ci.ps1, arbol limpio, servicios abajo)
| corrida | exit | duracion | culpable |
|---------|------|----------|----------|
| 1 | 0 | 110,9 s | - |
| 2 | 0 | 91,0 s | - |
| 3 | 0 | 92,2 s | - |
| 4 | 0 | 91,6 s | - |
| 5 | 0 | 93,0 s | - |
| 6 | 0 | 90,2 s | - |
| 7 | 0 | 89,2 s | - |

### Fixes puntuales
| Hallazgo | Estado | Evidencia propia |
|----------|--------|------------------|
| F-V2-01 Mayor (gate flaky) | **CERRADO** | 7/7 hammer exit 0 + suite x2 sin -m:1 186/186 + analisis estructural del diff (grep: escritores saga = solo Orders.Tests, ahora serializado a nivel assembly) |
| F-V2-02 Menor (lint no pilla drift) | **CERRADO** | drift run:->ci-v2.ps1 con comentarios intactos: FAIL + exit 1; sin drift 9/9 exit 0 |
| F-V2-03 Menor (crash emulador vacio) | **CERRADO** | -Verify en emulador vacio: 15/15 FALTA (incluye el bucket), 0 crash, exit 1; apply exit 0 restaura + verify 15/15 OK |
| F-V2-04 Menor (README stale) | **CERRADO** | l.282 186/186 (0 hits 155/155); l.81 las 4 demos; "Los 5 scripts" = 4 demos + stress (correcto) |
| F-V2-05 Info (reason zombi sin advertencia) | **CERRADO** | reason advierte "(el pago puede haberse aprobado despues: verificar en Pagos antes de reintentar)"; unit assert dirigido PASS 1/1 (exit 1 del run = umbral cobertura con 1 test, esperado); corrio verde en las 9 suites de hoy |

### Regresion minima: TODO VERDE
Suite x2 186/186 exit 0 (regimen README sin -m:1: Payments 8 + Ticketing 5 + Provision 17 +
Catalog 64 + Orders 92) - demo-ttl exit 0 11 PASS (hold/expiracion/liberacion/409/replay
exactos) - demo-resilience exit 0 15/15 con DLQ sucia (F-V1-R3-01 sigue cerrado) -
stress N=20 X=5 PASS (0 sin terminal, cierre 0+5=5==X).

### Notas residuales (no bloqueantes)
1. Observabilidad del gate: Invoke-Stage descarta el stdout de dotnet test - si la etapa tests
   fallara se reporta el culpable (etapa) pero no el nombre del test. Exit contract intacto;
   propuesta dev de trx persistente razonable si un flake reaparece.
2. Ventana zombi (pago APPROVED sobre compra EXPIRED) sigue existiendo POR DISENO (D-0017) -
   consistencia verificada en ronda 1; ahora el reason advierte (F-V2-05).
3. Heredados declarados en ronda 1 sin cambios: /admin sin auth localhost-only, techo de
   escritura del emulador.

Todo lo demas de la ronda 1 (17 ACs funcionales, adversarial de hold, IaC, regresion V1)
queda verificado como consto abajo - este retest no lo re-audito, solo confirmo que nada
regreso. El veredicto NO-SHIP de la ronda 1 queda reemplazado por este.

---
Fecha: 2026-11-18 - Auditor: QA adversarial (factory) - Repo: HEAD 171638d (sin cambios por QA).
Inputs: brain/nodes/N-300{1,2,3}.md (ACs), factory/backlog.md SS-V2, factory/plan.md SS-V2 (NFR-1..8
vigentes), factory/qa-report-v1.md (formato/linea base), factory/spec.md.
Evidencia cruda (100% propia): brain/evidence/2026-11-18-qa-v2-adversarial.md
(logs en %TEMP%/opencode/qa-v2-18/). Cero confianza en la evidencia del dev: todo re-ejecutado.

## Resumen ejecutivo

V2 entrega lo que promete en lo funcional: hold TTL correcto de punta a punta (16 checks adversariales
propios: borde del TTL, crash de servicios, zombis, doble liberacion, replay, hostiles - todos los
invariantes exactos), IaC from-scratch/idempotente/1:1, CI rojo/verde con culpable, DLQ sucia tolerada,
regresion V1 verde y p95 holgado. El zombi-tier mantiene consistencia de asientos/sin ticket aunque el
pago apruebe tarde (ventana declarada por el dev, verificada).

PERO el gate de CI - el entregable central de H-08, "todo el repo en un comando" - es NO determinista:
**3 de 7 corridas full-suite en arbol limpio fallaron hoy** (suite1, ci1, ci5; la sesion interrumpida
del 11-15 registro 3 mas con el mismo mecanismo). Los mismos tests pasan aislados (Orders solo: 92/92).
Causa estructural: la tabla `purchases` y las colas de la saga del emulador son COMPARTIDAS entre
collections xUnit paralelas de Orders.Tests (e2e "orders-saga", PurchaseRepositoryTests con fixtures de
holds due, sweeper hosted de PaymentEndpointTests) y entre proyectos de test concurrentes. V1 era
estable con 63 tests de Orders; V2 sumo 29 con mas escritores paralelos de estado compartido. Un
evaluador que corre `ci.ps1` una vez tiene ~43% de probabilidad de un roto falso. **F-V2-01 (Mayor) ->
NO-SHIP.**

## Hallazgos

| ID | Sev | Hallazgo | Evidencia | Fix sugerido |
|----|-----|----------|-----------|--------------|
| F-V2-01 | **Mayor** | **El gate de CI flakea en arbol limpio (falsos rojos ~43%)**: `SagaEndToEndTests.Persistent_failure_on_release_seats_ends_terminal_failed_with_explicit_orphan` fallo 3 veces (suite1: Expected FAILED / Actual SEATS_RESERVED tras 45 s; ci1 a los 2,95 s del host; ci5), `PurchaseRepositoryTests.Scan_finds_only_due_holds` (Expected 1 due / Actual [] - el sweeper/scan de OTRA collection paralela expira el fixture en la tabla compartida) y `SagaEndToEndTests.Seat_freed_by_expiry_is_rebuyable` (ci5). 3/7 corridas propias hoy; 3 mas en la sesion del 11-15 (mismo HEAD). Orders aislado: 92/92 verde. Refuta el sentido inverso de H-08 AC1 ("exit 0 si TODAS pasan" => tambien "todo OK => exit 0") y el valor del gate como comando unico del portfolio. | suite1.log l.54-60 (Expected:["pur-89ebb..."] Actual:[] / Expected: FAILED Actual: SEATS_RESERVED, SagaEndToEndTests.cs:364); ci1.log, ci5-demos.log; mecanismo en evidencia S2 | Aislar estado compartido: (a) prefijo/tabla propia por collection (como Catalog ya hace con it-*), (b) serializar las collections de Orders que tocan purchases/colas ([CollectionDefinition] comun o DisableTestParallelization en el proyecto), o (c) `-m:1`/ejecutar proyectos de test secuenciales en ci.ps1. Minimo riesgoso: (b) o (c) + re-corrida x5 del gate como AC de la ronda |
| F-V2-02 | Menor | **workflow-lint no detecta deriva semantica del gate**: con el `run:` real del workflow reemplazado por otro script (YAML valido, comentarios intactos), validate-workflow.ps1 da 9/9 OK exit 0 - la "invariante central (cero deriva)" matchea menciones de `scripts/ci.ps1` en COMENTARIOS (l.3/7). Un edit silencioso del runner deja de invocar el gate sin que el lint lo pille. | repro propia: copia drifted -> exit 0 9/9 (evidencia S7) | Anclar la invariante a la linea `run:` (p.ej. exigir `'^\s*run:.*ci\.ps1'` en vez de match global), o quitar ci.ps1 de los comentarios |
| F-V2-03 | Menor | **`provision.ps1 -Verify` crashea en emulador vacio**: recrear contenedor sin aprovisionar y correr -Verify imprime 14 FALTA y luego `ERROR: Value cannot be null. (Parameter 'source')` en la seccion buckets -> **exit 2** (contrato documentado: falta => exit 1 con diff). El FALTA del bucket `ticketflow-tickets` jamas se reporta. El gate igual queda rojo (exit != 0), asi que no rompe el CI. | prov7-verify-empty.log (evidencia S4) | Nulo-guard en el listado de buckets reales (`ListBuckets` con 0 buckets devuelve null en el emulador) y tratarlo como lista vacia |
| F-V2-04 | Menor | **README stale (DoD)**: l.282 "Tests: 155/155" (real 186/186, el CHANGELOG lo dice bien); l.81 "-Demos... corre las 3 demos" (corre 4: cache/idempotency/resilience/ttl). Misma clase que F-V1-02. | README.md l.81/l.282 vs CHANGELOG | 2 lineas de edicion |
| F-V2-05 | Info | **Ventana zombi: pago APPROVED sobre compra EXPIRED, sin advertencia en el reason**: verificado con Payments muerto >2xTTL+30 s - zombie-tier expira/libera correctamente, el payment-approved tardio pierde el CAS (sin ticket, sin over-sell, disponibilidad exacta), pero queda un registro APPROVED en Pagos por una compra EXPIRED y el reason ("pago iniciado hace mas de X s sin completarse - reserva reclamada por contingencia") no menciona que el pago pudo/aprobo despues. Ventana declarada por el dev (D-0017 S5); consistencia verificada. Tambien Info: `hold:null` -> 201 tratado como ausente (compra V1 normal) mientras "yes"/1/{}/[true] -> 400 invalid-purchase-request. | zombi A: pur-dc3ffbd6... pago status APPROVED tras EXPIRED (evidencia S5); H7b.5 | Para el portfolio: una linea en el reason ("verificar Pagos antes de reintentar") o nota en README; hold:null -> 400 si se quiere tipado estricto |

Sin hallazgo (verificado OK): asiento agotado -> 422 sincronico sin drift y sin compra huerfana;
4 holds concurrentes delta exacta; pago a 0,8 s del vencimiento -> 202 + TICKETS_ISSUED (claim protege);
pago +2,5 s -> 409 + sin segunda liberacion; compensacion tardia post-zombi sin doble incremento (AC6);
crash de los 4 servicios dentro del TTL -> expiracion igualmente efectiva (AC7, estado 100% DynamoDB);
HOLD_TTL_SECONDS abc/4/3601 fail-fast con mensaje accionable; 18 payloads hostiles -> 400, proceso
vivo; id malformado 400 / inexistente 404; doble submit 202+200; guard de manifiesto pilla edicion a
mano (fallo el test, restaurado, verde); -Verify detecta 14/15 FALTA (ver F-V2-03 por el bucket 15);
stress N=20/X=5 PASS; idempotencia 4x 201,200,200,200; traza 4 servicios cronologica; p95 24 ms/850 ms;
0 secrets, 0 TODO/FIXME; DLQ payments 23 entradas -> demo-resilience 2x 15/15.

## Checklist (OK / FALLA / N-A + evidencia)

Seguridad:
- Validacion de entrada en fronteras nuevas (hold, payment endpoint, provision tool) ... **OK** - 18 hostiles + tipado hold -> 400 accionables; payment 400/404/409; provision args fail-fast (tests) y crash controlado (F-V2-03)
- Cero secrets en codigo/logs ............ **OK** - grep AKIA/secret/private_key: 0 hits (src/tools/scripts/infra/.github)
- Dependencias sin vulns graves .......... **N-A esta ronda** - sin cambios de dependencias vs V1 (auditado 0 en r3 V1); lockfile N-A (.NET)
- Errores sin internals .................. **OK** - ProblemDetails uniformes (400/404/409/422); el crash del provision tool (F-V2-03) no expone internals ni secretos
Resiliencia:
- Expiracion sobrevive reinicios (AC7) .... **OK** - 4 servicios matados dentro del TTL -> EXPIRED + liberacion tras rearrancar
- Estados siempre consistentes ........... **OK** - zombi + pago tardio: CAS gana siempre, sin ticket fantasma, sin doble liberacion, disponibilidad exacta en TODOS los escenarios
- Input malformado no tumba procesos ..... **OK** - bateria hostil viva; provision -Verify crash es exit 2 limpio, no corrompe
Performance:
- p95 medido vs NFR ...................... **OK** - GET HIT 24,25 ms (<50, n=40) - saga 850 ms (<2000, n=10); host cargado
DoD vendible:
- README instalar/probar .................. **OK con nota** - quickstart + seccion hold + IaC + CI documentadas; stale F-V2-04
- LICENSE / version / CHANGELOG ........... **OK** - MIT; 0.2.0; [Unreleased] completo con 186
- Suite verde desde instalacion limpia .... **FALLA intermitente (F-V2-01)** - 3/7 en arbol limpio
- Cero TODO/FIXME criticos ................ **OK** - 0 reales; 3 shortcut: declarados no criticos
- El producto hace lo que el spec dice ..... **OK** - ACs funcionales 17/17 verificadas (abajo); AC1 de H-08 con el asterisco de F-V2-01

## Veredicto por AC (contrato = nodos)

### N-3001 (CI, H-08 AC1..AC5)
| AC | Veredicto | Evidencia propia |
|----|-----------|------------------|
| AC1 gate un comando | **OK con asterisco** | ci4/ci6 exit 0 (5 etapas + resumen, PS 5.1); PERO determinismo refutado por F-V2-01 (3/7 arbol limpio exit 1) |
| AC2 rojo/verde real | **OK** | break propio (Assert.Fail) -> exit 1 CULPABLE: tests (solo mi test fallo); revertido -> ci4 exit 0; bonus CULPABLE: build con xUnit2020-as-error |
| AC3 workflow artefacto | **OK con nota** | existe, valida (9/9), README documenta runner + decision de emulador (Docker del job); nota F-V2-02 (lint no pilla drift del run:) |
| AC4 -Demos | **OK** | ci6: exit 0, 4 demos (cache/idempotency/resilience/ttl), cierre ordenado de lo propio; nota README "3 demos" (F-V2-04) |
| AC5 no flakea con DLQ sucia | **OK** | DLQ payments 23 entries (trigger exacto) -> demo-resilience 2x exit 0 15/15 (peek paginado encuentra SU entrega) |

### N-3002 (IaC, H-08 AC6..AC9)
| AC | Veredicto | Evidencia propia |
|----|-----------|------------------|
| AC6 manifiesto declarativo | **OK** | infra/aws-local.json legible (6 tablas pk-HASH + 8 colas FIFO CDB + bucket) |
| AC7 from-scratch 1:1 | **OK** | docker rm -> provision exit 0 (15 recursos, 225 s) -> -Verify 15/15 OK 0 FALTA/DIFF/WARN |
| AC8 idempotente | **OK** | 2 re-aplicaciones exit 0; -Verify identico x3 |
| AC9 operable | **OK** | run-local sobre infra nueva -> consulta->compra 201->TICKETS_ISSUED + 2 tickets + qr_s3_key + disponibilidad 498 exacta |

### N-3003 (hold TTL, H-09 AC1..AC8)
| AC | Veredicto | Evidencia propia |
|----|-----------|------------------|
| AC1 retencion + TTL config | **OK** | hold:true -> SEATS_RESERVED + expires_at + avail -1; HOLD_TTL_SECONDS default 90 (ci6 con 90), 15 usado, rango/fail-fast verificado; README lo documenta |
| AC2 liberacion automatica | **OK** | EXPIRED terminal con reason + disponibilidad restaurada (demo-ttl x2 + H4b), sin intervencion manual |
| AC3 pago a tiempo prospera | **OK** | 202 al borde (t-0,8 s) -> TICKETS_ISSUED, avail permanece -1 (H4a, H11) |
| AC4 pago tardio rechazo limpio | **OK** | 409 hold-expired accionable a t+2,5 s, sin ticket, delta 0 exacto |
| AC5 asiento reutilizable | **OK** | re-compra con key nueva -> TICKETS_ISSUED (demo-ttl paso 6, x3 corridas) |
| AC6 idempotencia + no doble liberacion | **OK** | replay misma key -> misma EXPIRED delta 0; compensacion tardia (zombi PAN 0002) NO acumula: avail exacta |
| AC7 no vive en memoria de proceso | **OK** | 4 servicios matados dentro del TTL -> expiracion efectiva al rearrancar, avail 492 exacta |
| AC8 demo imprimible | **OK** | demo-ttl 2x exit 0 con valores (TTL 15) + 1x via CI (TTL 90); README referencia |

## Regresion V1/MVP (todo OK)

stress N=20 X=5 PASS (0+5=5==X, 0 sin terminal, 67 s) - idempotencia 4x 201,200,200,200 (1 compra,
delta -1) - trace-purchase 4/4 servicios cronologica (saga 588 ms) - breaker+DLQ via demo-resilience
15/15 x2 - demo-cache/idempotency exit 0 (ci6) - p95 24/850 ms - grep secrets 0 - 0 TODO/FIXME.
Nota: stress -X 5 "alinea" disponibilidad pre-comprando (487 compras) y dejo evt-mj en 0 POR DISENO
del script (no es over-sell; invariante de cierre PASS).

## Veredicto final: **NO-SHIP** (ronda fix 1 obligatoria, acotada)

1. **F-V2-01 (Mayor)**: el gate de CI - el entregable central de H-08 - falla en arbol limpio ~43% de
   las veces (3/7 hoy, mecanismo identificado y reproducible). Bloquea: un hallazgo Mayor abierto y
   AC1 de H-08 con el sentido "todo OK => exit 0" intermitentemente refutado. Sin excepciones.
2. F-V2-02/03/04 (Menores) y F-V2-05 (Info) no bloquean: no refutan ACs en el escenario portfolio y
   tienen fix de horas.
3. Todo lo demas quedo verificado con evidencia propia: 17 ACs funcionales OK, adversarial de hold
   impecable (incluida la ventana zombi destructiva: consistente), IaC solidisimo, regresion V1 verde.

Dev loop pedido (1 ronda): **F-V2-01** (aislar estado compartido de tests o serializarlos + gate
estable x5 corridas) + F-V2-02/F-V2-03/F-V2-04 (cada uno <= 1 h, triviales). Retest acotado: ci.ps1 x5
+ suite x2 + los 3 fixes puntuales. Con eso, SHIP es lo esperado.

Riesgo residual declarado (no bloqueante): ventana zombi con pago aprobado tarde (F-V2-05, consistencia
verificada, razon no advierte); /admin sin auth localhost-only (heredado V1); techo de escritura del
emulador; estado del emulador en memoria.