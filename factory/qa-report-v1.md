# QA Report V1 - TicketFlow (H-05..H-07 + H-10) - Fase 4-bis adversarial

Fecha: 2026-11-14/15 - Auditor: QA adversarial (factory) - Skill: security-audit (guidance)
Inputs: factory/plan.md SS-V1 (NFRs vigentes, D-0011..D-0014), factory/backlog.md (ACs V1),
factory/division.md (gates), references/security.md, factory/qa-report.md (F4 + deuda F-04..F-07),
brain/evidence/ (S05..S08), brain/index.md. Repo: HEAD f1c9124, tag v0.1.0.
Evidencia cruda de esta auditoria: brain/evidence/2026-11-15-qa-v1-adversarial.md
(raw completo en %TEMP%\opencode\qa-v1\, logs de servicios respaldados en logs-run1\).
100% evidencia propia: cero confianza en la evidencia del dev.

## Resumen ejecutivo

V1 cumple casi todo lo que promete, con evidencia propia: suite 131/131 en 3 corridas limpias (flakiness
de cobertura NO reproducido: 0/4 corridas recientes), trazas H-05 perfectas (4/4 servicios, cronologicas,
aisladas, sin PAN/CVV), breaker robusto ante pausas repetidas (fail-fast 3-6 ms vs 10 s), stress sin oversell
en N>X y N<X, frontera Content-Type 415 sin efectos y key intacta, catalogo limpio post-suite, quickstart
frio verificado OTRA VEZ desde contenedor detenido y .secrets borrado (la entrada de portfolio funciona).

Pero la auditoria adversarial REFUTO un AC de H-06 en el paso compensatorio: inyeccion persistent en
release-seats (paso inyectable documentado en el README) deja la compra en SEATS_RESERVED para siempre,
sin reason, con asiento huerfano y drift permanente de disponibilidad (sin redelivery ni redrive). El AC2
dice literalmente "nada en PENDING/SEATS_RESERVED para siempre". Hallazgo Mayor F-V1-01 -> dev loop.
Ademas el empaquetado V1 esta incompleto (CHANGELOG sin entrada V1 ni [Unreleased], README con conteo de
tests stale 96/96) y hay un secret cosmetico en una evidencia commiteada que el grep de F4 no pesco.

Veredicto final: **NO-SHIP en esta ronda** -> dev loop (1 ronda, fix acotado) -> retest -> SHIP esperado.

## Hallazgos

| ID | Sev | Hallazgo | Evidencia | Fix sugerido |
|----|-----|----------|-----------|--------------|
| F-V1-01 | Mayor | **H-06 AC2 roto en el paso compensatorio**: POST /admin/failure {step:release-seats, mode:persistent} (:5080, paso inyectable documentado en README) + compra PAN 0002 => la compensacion falla 3+3 intentos (doble emision de release-seats), el 2do step-failed llega a Ordenes y se ignora ("fallo ya anotado"), y la compra queda **SEATS_RESERVED para siempre** con reason vacio, con **asiento huerfano** (disponibilidad 478->477) y **drift permanente**: tras DELETE de la regla + 45 s de gracia (> visibility timeout) no hay redelivery, self-heal ni redrive (la DLQ es peek-only). SagaProcessManager.cs: la transicion a FAILED solo ocurre via seats-released (l.94-110); TryNoteFailureAsync solo anota el motivo. El demo del dev solo inyecta en authorize-payment, donde la compensacion posterior funciona. | pur-96bf2a61937846469894d687e8a43223; traza completa en evidencia seccion 4; logs-run1 | Minimo: al agotarse la compensacion, transicionar a FAILED con reason "compensacion fallo - ver DLQ" (terminal explicita) en vez de quedar SEATS_RESERVED silencioso. Ideal: re-emitir release-seats con contador de compensaciones (p.ej. 2 rondas) antes de rendirse, y/o redrive manual (POST /admin/dlq/redrive). Test e2e cubriendo persistent en release-seats. |
| F-V1-02 | Menor | **Empaquetado V1 incompleto**: CHANGELOG.md solo tiene [0.1.0]; V1 (H-05..H-10, 4 commits) sin entrada ni [Unreleased] que declare lo pendiente. Version sigue 0.1.0 (bumpeo a 0.2.0 post-gate es por diseno, plan S08, pero el CHANGELOG ni siquiera menciona que V1 esta en el repo). README l.199: "Tests: 96/96" stale (real: 131/131); l.120 "Ambos exit 0" refiere a 2 scripts cuando documenta 4. | CHANGELOG.md (44 lineas, sin V1); README.md l.199/l.120 | CHANGELOG [Unreleased] o [0.2.0] con H-05..H-10 (agregado: trazas, resiliencia, stress, pulido; tests 131), bump 0.2.0 en Directory.Build.props + tag v0.2.0 al aprobar el gate; README conteo y "los 4 scripts". 30 min sin codigo. |
| F-V1-03 | Menor | **Secret cosmetico en evidencia commiteada**: brain/evidence/2026-11-13-emulator-verification.md l.24 contiene aws_secret_access_key literal (cred del emulador de 2026-11-13). Riesgo real ~nulo: inutil fuera de localhost:9297 (CA pineada + endpoint loopback) y ya rotada (regenerada por arranque), pero viola NFR-2 literal y dispara secret-scanners en un repo publico. El grep de F4 ("secret=") no lo detecto por el patron. | grep AKIA/secret/private key -> 2 hits, 1 real | Redactar el valor (placeholder) + amend; ensanchar el patron de grep en el checklist (aws_secret_access_key\\s*=). |
| F-V1-04 | Menor | **Cobertura .Api no reportada por el run estandar**: los csproj Exclude=[*.Api]* de la coleccion, entonces el 91,7%/92,3% de la evidencia S08 no es reproducible con dotnet test default. Los tests in-proc SI existen y ejercitan los endpoints (verificado: fallarian si rompen), y yo medi .Api=91,66/92,30 con Exclude reducido. AC3 de H-10 dice "cobertura reportada >0%": cumplido solo con medicion custom. | csproj l.20; medicion propia evidencia seccion 1 | Documentar en S08-evidencia el comando exacto que reproduce el numero (dotnet test <proj> /p:Threshold=0 /p:Exclude=[TicketFlow.Shared.Infrastructure]*), o mover el numero al README de verificacion. |

Sin hallazgo (verificado OK): input validation del admin (24 hostiles -> 400/415, cero 5xx, proceso vivo);
DELETE inexistente 404; /admin/dlq en los 4 servicios (200 con [] vacia); breaker ante pausas repetidas
(2 ciclos: fail-fast 3-6 ms vs 10.015 ms del timeout, recovery 40-57 ms, saga nueva completa); compra durante
pausa -> 500 explicito en ~10 s sin estado colgado; persistent reserve-seats y transient count=3 (limite)
-> FAILED con motivo + delta 0 exacto; stress hostil (evento inexistente/N=0/X negativo/X>avail/deadline
chico) -> exit codes correctos sin cuelgues; cero PAN/CVV en TODOS los logs (grep propio); cero vulns (13
proyectos); .gitignore correcto; TODO/FIXME reales 0.

## Flakiness de cobertura (cuantificacion pedida por el orquestador)

- Corridas propias full: **3** (todas 131/131, 0 omitidos, umbral >=70% verde). Mas la corrida limpia del
  dev en S08: **0/4 corridas recientes** con flake. El evento reportado por el dev (S06: 1 de 3 corridas
  margen <70 por timing contra el emulador compartido) NO se reprodujo.
- Varianza observada: SOLO Catalog.Core (81,34 / 80,22 / 81,34; amplitud 1,12 pts). Los otros 3 Core
  identicos en las 3 corridas. Margen minimo al umbral: 10,22 pts (Catalog run2).
- Lectura: riesgo residual bajo. El canal de varianza existe (branches dependientes de timing del emulador,
  compartido con otra carga docker en el host) pero esta lejos del umbral. Si reaparece, mirar Catalog.Core
  (unico modulo que varia). Recomendacion: mantener monitoreo por corrida, sin accion.

## Checklist security.md (OK / FALLA / N-A + evidencia)

Seguridad:
- Entrada externa validada en TODA frontera nueva ...... OK - 24 hostiles /admin/failure -> 400/415 accionables, 0x5xx, vivo (evid. 4-B6); stress y purchases: bordes hostiles exit 1/2 limpios; 415 sin efectos con key intacta (evid. 6)
- Cero secrets en codigo/logs/commits ................. OK con nota (F-V1-03) - codigo/scripts 0 hits; 1 cosmetico en evidencia commiteada (cred de emulador muerta); logs sin PAN/CVV (grep propio)
- Dependencias sin vulns criticas/altas .............. OK - dotnet list --vulnerable --include-transitive: 13 proyectos 0 vulns; lockfile N-A (.NET)
- Errores sin internals .............................. OK - ProblemDetails 400/404/415 accionables (echo del content-type recibido, motives); sin stack/rutas
- Inyeccion .......................................... OK - DDB parametrizado; step del injector validado [a-zA-Z0-9-_] 1..64, count 1..10; path-traversal rechazado
- Auth en superficie nueva (/admin/*) ................ N-A documentado - sin auth por diseno (demo local localhost-only, comentario en ResilienceAdminEndpoints.cs). Riesgo portfolio BAJO: un visitante de la demo puede inyectar fallos o dejar drift (ver F-V1-01), pero no hay datos sensibles ni acceso mas alla de localhost. Recomendacion: mencionarlo en "Limitaciones" del README o gatear los /admin tras un env (ADMIN_ENDPOINTS=off). No es blocker.

Resiliencia:
- Toda IO externa con timeout explicito .............. OK - pausa del emulador: GET/POST ~10,0 s exactos (timeout efectivo, F-01 sigue cerrado); breaker fail-fast 3-6 ms tras 3 fallos
- Errores manejados sin estados corruptos ............ FALLA (F-V1-01) - compensacion agotada deja SEATS_RESERVED eterno + asiento huerfano; el resto de caminos (reserve/authorize/issue + transient/persistent/pausa) termina SIEMPRE explicito con motivo y delta exacto
- Retry con backoff solo idempotente/seguro .......... OK - worker 3 intentos 0,5/1 s visible en timestamps (gaps 510-560/1014 ms medidos); handlers idempotentes soportan redelivery
- Fallback/degradacion ................................ OK - precheck fail-open + guard real en decremento (D-0009/D-0013); DLQ aplicativa independiente del emulador (D-0012)
- Crash-coherencia: input malformado no tumba proceso . OK - bateria B6 completa con 4 servicios vivos; pausas repetidas sin crash

Performance:
- p95 medido vs objetivo ............................. OK - GET HIT 0,9 ms (n=40, <50) - saga completa 858 ms (n=25, <2000); POST spot incluido en saga (F4: 22,7 ms)
- Camino caliente sin O(n2)/IO bloqueante ............ OK - sin cambios estructurales vs F4; stress 20 compradores responde en 14,7 s (techo de escritura del emulador, conocido)
- Memoria estable .................................... OK - bench 25 sagas + 40 GETs sin degradacion (max ~ p95 x1,14); sin leak evidente

DoD vendible:
- README (que es, instalar, probar <=5 min) ........... OK - quickstart FRIO re-verificado por QA (contenedor detenido + .secrets borrado -> "Todo listo" ~1 min -> flujo completo). Con stale menor (F-V1-02)
- LICENSE ............................................. OK - MIT (sin cambios)
- Version + CHANGELOG ................................. FALLA (F-V1-02) - CHANGELOG sin V1; version 0.1.0 con V1 en el repo (bumpeo post-gate por diseno, pero sin [Unreleased] que lo declare)
- Suite verde desde instalacion limpia ................ OK - bin/obj wiped -> build 0/0 -> 131/131 x3
- Cero TODO/FIXME criticos ........................... OK - 0 reales (1 falso positivo: "todo" en espanol)
- El producto hace lo que el spec dice (AC V1) ........ FALLA en H-06 AC2 adversarial (F-V1-01); H-05/H-07/H-10 OK - ver veredicto por nodo

## Gates de division (division.md)

- Idempotencia demostrada con evidencia: **OK** - 4 POST simultaneos misma key -> 201,200,200,200 / 1 compra / delta -1 exacto; replay 200 sin efectos; demo-idempotency exit 0 (corrida propia)
- Cache demostrada con evidencia: **OK** - demo-cache exit 0 propio (MISS 471 -> HIT 471 -> compra -> MISS 470 -> HIT 470); invalidacion en cada reserva confirmada en logs
- Todo AWS al emulador, cero nube real: **OK** - arranque frio re-extrajo CA+creds del contenedor local (pin activo); sin cambios en EmulatorAwsClientFactory desde F4 (verificado entonces estatico+runtime)
- Cero secrets en repo: **OK con nota** - codigo/scripts limpios; F-V1-03 cosmetico en una evidencia (cred muerta del emulador)

## NFRs vigentes (plan SS-V1: siguen sin cambio)

| NFR | Veredicto | Medicion QA propia |
|-----|-----------|---------------------|
| NFR-1 validacion 100% fronteras | OK | 24 hostiles admin -> 400/415; 415 sin efectos + key intacta; stress hostil exit 1/2; env fail-fast |
| NFR-2 cero secrets + audit | OK con nota | 0 vulns (13 proyectos); 0 secrets en codigo; F-V1-03 cosmetico en evidencia |
| NFR-3 timeouts 10 s efectivos | OK | pausa: 10,005-10,059 s medidos en GET y POST; breaker fail-fast 3-6 ms tras OPEN |
| NFR-4 errores sin internals | OK | ProblemDetails uniformes 400/404/415/422 con motives accionables |
| NFR-5 p95 | OK | GET HIT 0,9 ms (<50) - saga 858 ms (<2000); n=40/25 cliente fiel |
| NFR-6 cobertura >=70% Core | OK | x3 corridas: 93,02 / 87,32 / 86,35 / 80,22-81,34; umbral verde y mordiendo (F-03); flakiness 0/4 recientes |
| NFR-7 AWS 100% local | OK | arranque frio con extraccion local de CA/creds; pin sin cambios |
| NFR-8 demos imprimibles | OK | demo-cache, demo-idempotency, demo-resilience y stress: exit 0 en corridas propias |

## Bordes adversariales ejecutados por QA (todos con evidencia propia)

1. B6: 24 payloads hostiles contra POST /admin/failure (tipos, rangos, unicode, 10k chars, anidado 80,
   text/plain) -> 400/415/201-por-diseno, 0x5xx, proceso vivo.
2. B1: persistent en release-seats durante compensacion -> compra no-terminal eterna + huerfano (F-V1-01).
3. B2/B3: persistent reserve-seats y transient count=3 (limite exacto de reintentos) -> FAILED con motivo,
   delta 0, DLQ inspeccionable.
4. B4/B5: DELETE inexistente 404; /admin/dlq en servicios con DLQ vacia -> 200 [].
5. B7: 2 ciclos completos de docker pause: timeout 10 s exacto, fail-fast 3-6 ms, recovery 40-57 ms,
   breaker re-OPEN correcto, saga nueva completa; compra durante pausa -> 500 explicito sin colgado.
6. H-05: 4 compras (aprobada/rechazada/2 paralelas), trazas cronologicas 4/4, aislamiento 0 menciones
   cruzadas, grep PAN/CVV sobre TODOS los logs = 0.
7. H-07: stress N>X (20/5: 5+15, invariante 0+5==5) y N<X (4/476: 472+4==476) + 5 hostiles del script.
8. F-04: 5 variantes de Content-Type 415 sin efectos, charset tolerado, key no consumida, delta -1 exacto.
9. Idempotencia concurrente 4x misma key -> 1 compra (201,200,200,200).
10. Quickstart frio: contenedor detenido + .secrets borrado -> 1 comando -> flujo completo + traza.

## Veredicto por nodo

| Nodo | Veredicto QA | Base |
|------|--------------|------|
| N-2001 (trazas H-05) | **verified** (re-verificado por QA) | AC1-AC4 con corridas y trazas propias (4 compras, orden cronologico, aislamiento, grep PAN/CVV propio); trace-purchase exit 0 x5; quickstart frio incluye traza 4/4 |
| N-2002 (resiliencia H-06) | **implemented** (degradado desde verified; blocker F-V1-01) | AC1 OK (backoff 560/1014 ms propios), AC2 REFUTADO en el camino compensatorio (release-seats persistent -> SEATS_RESERVED eterno), AC3 OK en los caminos cubiertos (drift solo via F-V1-01), AC4 OK (2 ciclos de pausa + recovery + saga nueva), AC5 OK (delta exacto en todos los casos + invariantes del stress + suite) |
| N-2003 (stress H-07) | **verified** (re-verificado por QA) | N>X y N<X con exit 0 e invariantes exactos (corridas propias); 5 hostiles con exit correcto |
| N-2004 (pulido H-10) | **verified** (re-verificado por QA) | AC1 415 sin efectos + charset + key intacta; AC2 0 it-* tras 3 suites; AC3 .Api medido propio 91,66/92,30 y Core >=70 x3 (nota F-V1-04) |

## Veredicto final: NO-SHIP (ronda fix 1/2 obligatoria)

**NO-SHIP en esta ronda.** La entrega V1 es solida en 3 de 4 historias y en todo el happy path + la mayoria
del adversarial, PERO:

1. **F-V1-01 (Mayor)**: H-06 AC2 esta literalmente refutado en un escenario que el evaluador puede construir
   con la superficie documentada (README lista release-seats como paso inyectable). El valor de V1 es "se
   que pasa cuando falla"; una compra colgada en SEATS_RESERVED sin reason y sin camino de recuperacion es
   exactamente lo que la historia promete que no puede pasar. Fix acotado (1 ronda dev).
2. **F-V1-02 (Menor, DoD)**: sin CHANGELOG de V1 ni [Unreleased], version 0.1.0 con 4 commits de V1 encima,
   README stale. El gate de versionado/bump (tag v0.2.0) pende de ESTE gate: no puedo firmarlo con el
   empaquetado sin actualizar.
3. **F-V1-03 (Menor)**: redaccion de un secret cosmetico antes de publicar.

Despues de esa ronda fix (estimada en 1-2 h: transicion FAILED-on-compensacion-agotada + test e2e +
CHANGELOG/README/bump + redaction) y un retest acotado (B1 + suite + smoke), el SHIP es lo esperado:
todo lo demas quedo verificado con evidencia propia y reproducible.

Dev loop pedido (max 2): **F-V1-01 + F-V1-02 + F-V1-03** (una ronda conjunta). F-V1-04 queda como nota de
documentacion, no bloquea.

Riesgo residual declarado (no bloqueante):
- /admin/* sin auth en demo local (localhost-only, sin datos sensibles): documentar en README o toggle env.
- Estado del emulador en memoria + SNS sin entrega + cache in-app: limitaciones ya declaradas del equipo.
- Techo de escritura concurrente del emulador (~14-15 s para 20 POSTs): conocido, no es del producto.
