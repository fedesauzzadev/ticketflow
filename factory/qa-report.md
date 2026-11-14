# QA Report - TicketFlow MVP (H-01..H-04) - Fase 4 adversarial

Fecha: 2026-11-14 - Auditor: QA adversarial (factory) - Skill: security-audit (full)
Inputs: factory/division.md (gates), factory/plan.md (NFRs), factory/backlog.md (AC H-01..H-04),
references/security.md (checklist), README.md, brain/evidence/S01..S04, factory/learnings.md.
Evidencia cruda de esta auditoria: brain/evidence/2026-11-14-qa-f4-adversarial.md
(raw de payloads: %TEMP%\opencode\qa-f4\).

## Resumen ejecutivo

El MVP hace lo que el spec dice. Las 4 historias resistieron la auditoria adversarial con evidencia
propia: 96/96 tests desde build limpio, 26 payloads hostiles sin un solo 5xx ni fuga de internals,
idempotencia correcta bajo concurrencia real (4 POST simultaneos misma key = 1 compra), CERO oversell
en carreras sobre la ultima disponibilidad (incluida la carrera sucia donde todos los POST pasan el
precheck), quickstart frio desde contenedor detenido y .secrets borrado, y recovery limpio tras
congelar el emulador. Cero hallazgos criticos. Hay 2 Mayores (uno de resiliencia bajo fallo de
dependencia, uno de empaque comercial) y 5 Menores. Veredicto: SHIP (riesgo residual abajo).

## Hallazgos

| ID | Sev | Hallazgo | Evidencia | Fix sugerido |
|----|-----|----------|-----------|--------------|
| F-01 | Mayor | Timeout no efectivo contra dependencia colgada: con el emulador pausado (docker pause), GET availability no respondio en 150 s y POST /purchases no respondio en 240 s; cero entradas LogLevel=Error en logs durante la pausa (ni timeouts ni reintentos de workers). El Timeout=10s del ClientConfig no acota la latencia visible: PinnedCaHttpClientFactory crea el HttpClient con Timeout.InfiniteTimeSpan y la peticion queda suspendida. NFR-3 ("timeout explicito 10 s, 100%") falla en runtime, no en config. Sin crash ni corrupcion: al unpause todo recupera y la saga completa funciona. | evidencia seccion 12 (dos tandas medidas) | Timeout finito en el handler del PinnedCaHttpClientFactory (p.ej. 10 s) o CTS por request; re-verificar con docker pause |
| F-02 | Mayor | DoD vendible incompleto: sin LICENSE, sin CHANGELOG, sin version; repo sin git init. security.md lo exige como bloque "sin esto no hay SHIP". El README/quickstart/demos (la parte de dominio) si esta completa. | listado raiz: solo .gitignore, AGENTS.md, Directory.Build.props, global.json, README.md, TicketFlow.sln (+dirs); git absent | MIT en un archivo + CHANGELOG.md + 0.1.0 + git init (30 min, sin cambio funcional) |
| F-03 | Menor | Umbral de cobertura 70% NO cableado: coverlet.collector solo colecta; NFR-6 dice "umbral falla el build de verificacion". Hoy se cumple (medido), pero nada impide regresar. | grep coverlet/Threshold en csproj/props: solo 4 referencias collector, 0 thresholds; cobertura medida en evidencia seccion 2 | dotnet test /p:Threshold=70 /p:ThresholdType=line (coverlet.msbuild) o runsettings en CI |
| F-04 | Menor | POST /purchases acepta JSON valido con Content-Type: text/plain (201). El endpoint deserializa http.Body directo sin mirar el Content-Type. Lenient, sin impacto de seguridad. | evidencia seccion 4 caso a18 | Chequear Request.ContentType application/json, si no 415/400 |
| F-05 | Menor | Ventana no atomica claim-then-decremento en DynamoDbEventRepository.ReserveAsync (y state-then-incremento en ReleaseAsync): un crash de proceso entre las dos escrituras deja la compra avanzando sin decremento (drift de disponibilidad; NO es oversell: la reserva sigue siendo unica por compra). Probabilidad baja (ventana de ms). El redelivery confirma el estado inconsistente en vez de repararlo. | codigo: src/Services/Catalog/.../DynamoDbEventRepository.cs lineas 83-129 | TransactWriteItems (verificar soporte del emulador) o decremento-primero con reversal |
| F-06 | Menor | El estado del emulador acumula eventos it-* de la suite de integracion: tras correr los tests, GET /events lista ~39 eventos residuales frente a un evaluador. Declarado que el estado persiste, pero el ruido degrada la demo. | GET /events post-suite | Prefijo filtrable en el listado, cleanup en tests, o nota en README |
| F-07 | Menor | Payments.Api y Ticketing.Api con 0% de cobertura de linea en la suite (endpoints ejercitados solo e2e/manual). NFR-6 apunta a los Core (pasan todos), pero el hueco existe. | cobertura seccion 2 | 2-3 tests de endpoint in-proc como los de Catalog/Orders |

Sin hallazgo (verificado OK): cero secrets en codigo/logs (greps + PAN ausente de logs y respuestas);
validacion de entrada en todas las fronteras (HTTP 26 casos + SQS worker descarta malformados);
errores ProblemDetails sin internals; inyeccion inexistente (DDB parametrizado, sin shell);
auth/rate-limit N/A por diseno (producto local de demo).

## Checklist security.md (OK / FALLA / N-A + evidencia)

Seguridad:
- Entrada externa validada (tipo/rango/formato/tamano) ......... OK - 26 payloads hostiles -> 400/404/405 accionables, 0x5xx (evid. 4); SQS worker valida sobre y descarta malformado (codigo + suite)
- Cero secrets en codigo/tests/logs/commits; config por env ... OK - greps 0 hits; .secrets gitignored; PAN/keys ausentes de logs (380+ lineas) y respuestas; creds extraidas por corrida (evid. 3)
- Dependencias sin vulns criticas/altas; lockfile ............. OK / N-A lockfile - dotnet list --vulnerable --include-transitive: 13 proyectos 0 vulns; .NET no usa lockfile por defecto y no hay git todavia (F-02)
- Errores sin internals ....................................... OK - ProblemDetails {type,title,status,detail} sin stack/rutas/versiones en todos los casos de la bateria (evid. 4)
- Inyeccion (queries/comandos/shell por concatenacion) ........ OK - DynamoDB parametrizado (ExpressionAttributeValues); sin Process/Shell en el path HTTP
- Auth (passwords, tokens, rate limiting) ..................... N-A - sin auth por diseno (backend local de portfolio); documentado

Resiliencia:
- Toda IO externa con timeout explicito ....................... FALLA (F-01) - timeouts configurados (10 s clientes, 3 s gateway) pero NO efectivos contra peer colgado: 150-240 s+ medidos, sin respuesta ni error
- Errores manejados: codigos correctos, sin estados corruptos .. OK - 400/404/405/422 con motivo; saga nunca deja estado parcial silencioso (seats-unavailable -> FAILED con razon)
- Retry con backoff solo en idempotente/seguro ................. OK con nota - SDK retries por defecto sobre ops con claim condicional (seguras por key); workers reintentan receive; sin retry visible en el path POST (evid. logs)
- Fallback/degradacion de dependencias opcionales ............. OK - precheck de disponibilidad fail-open (gateway 3 s) con guard real en el decremento condicional (D-0009); notificacion -> evento interno + log (SNS gap declarado)
- Crash-coherencia: input malformado no tumba el proceso ...... OK - bateria A completa con 4 servicios vivos; pausa de emulador 7 min sin crash (F-01 es hang, no crash)

Performance:
- p95 del flujo principal MEDIDO vs objetivo ................... OK - QA midio propio (n=50): GET HIT 5,8 ms (<50) - POST 22,7 ms (<200) - saga 768,3 ms (<2000) (evid. 10)
- Sin O(n2) evitable / IO bloqueante en camino caliente ....... OK - cache-aside en el path de lectura; repos por key (GetItem), no scans en camino de compra (Scan solo en GET /events, path frio)
- Memoria estable en corrida repetida .......................... OK - 50 sagadas + 50 lecturas en bench sin degradacion (max ~ p95 * 1.07); sin leak evidente

DoD vendible:
- README (que es, instalar, probar en 5 min, comandos) ......... OK - quickstart frio de 1 comando verificado por QA desde contenedor detenido (evid. 11); flujo manual copy-paste funciona
- LICENSE definida ............................................ FALLA (F-02)
- Version (semver) + CHANGELOG ................................. FALLA (F-02)
- Suite verde desde instalacion limpia ......................... OK - bin/obj borrados -> build 0/0 -> 96/96 (evid. 1)
- Cero TODO/FIXME criticos .................................... OK - 0 en src (matches son "todo" en espanol en docs)
- El producto hace lo que el spec dice (AC con evidencia) ...... OK - ver veredicto por nodo abajo

## Gates de division (division.md)

- Idempotencia demostrada con evidencia: OK - demo-idempotency.ps1 exit 0 (corrida QA); ademas: 4 POST simultaneos misma key = 1 compra (delta -1, 1 ticket), replay post-FAILED sin efectos, mismatch 422, logs "sin efectos nuevos"
- Cache demostrada con evidencia: OK - demo-cache.ps1 exit 0 (corrida QA): MISS(499) -> HIT(499) -> compra -> MISS(498); invalidacion en cada reserva (65 entradas de log en el run)
- Todo AWS al emulador (localhost:9297), cero nube real: OK - estatico (0 refs amazonaws/RegionEndpoint; endpoint loopback validado al arranque; pin CA por thumbprint) + runtime (endpoint AWS real => rechazo en startup, exit -532462766)
- Cero secrets: OK - greps 0 hits; .secrets gitignored; CA vieja en evidence es certificado publico (sin clave privada)

## NFRs del plan

| NFR | Veredicto | Medicion QA |
|-----|-----------|-------------|
| NFR-1 validacion 100% fronteras | OK | 26 payloads -> 400/404/405; env fail-fast al arranque; SQS frontera validada |
| NFR-2 cero secrets + audit | OK | 0 vulns (13 proyectos); 0 secrets; lockfile N-A (sin git, F-02) |
| NFR-3 timeouts explicitos | FALLA (F-01) | 10 s configurados pero inefectivos vs peer colgado (150-240 s+ medidos); recovery OK |
| NFR-4 errores sin internals | OK | ProblemDetails uniformes; codigos 400/404/405/422 correctos |
| NFR-5 p95 | OK | GET HIT 5,8 - POST 22,7 - saga 768,3 ms (n=50, cliente fiel) |
| NFR-6 cobertura 70% Core | OK hoy / cableado FALLA (F-03) | Catalog 81,2 - Orders 85,5 - Payments 92,8 - Ticketing 87,3 |
| NFR-7 AWS 100% local | OK | rechazo runtime de endpoint real + pin CA + loopback check |
| NFR-8 demos imprimibles | OK | ambos scripts exit 0 corridos por QA |

## Bordes adversariales ejecutados por QA (todos con evidencia propia)

1. 26 payloads maliciosos/invalidos (JSON roto, vacio, array, null, anidamiento profundo, seats
   negativos/9/1e20/string, pan con letras, holder de 100.000 chars, unicode/null en event_id, cvv
   de 4, headers faltantes/gigantes/10.000 chars) -> todos 400 accionables, 0x5xx, procesos vivos.
2. Colision de Idempotency-Key concurrente: 4 POST simultaneos misma key -> exactamente 1 creacion
   (1x201 + 3x200 misma compra), delta disponibilidad -1, 1 ticket.
3. Carrera misma key + body distinto -> 422 idempotency-key-reused.
4. Replay tras FAILED -> 200 misma compra, disponibilidad intacta, sin efectos nuevos.
5. Oversell: 3 escenarios de compra concurrente sobre ultima disponibilidad, incluida la carrera
   donde 6 POST reciben 201 con 3 asientos -> 3 emitidas + 3 FAILED, disponibilidad nunca negativa,
   deltas coherentes. CERO oversell.
6. Endpoints inexistentes, metodos incorrectos, path traversal, %00 en path -> 404/405/400 limpios.
7. Inyeccion de falla: emulador congelado (docker pause) 7+ min -> sin crash, recovery total (F-01
   por el hang), saga completa post-recuperacion.
8. Quickstart frio: contenedor detenido + .secrets borrado -> 1 comando del README -> 4 servicios
   + flujo completo + re-seed correcto.

## Veredicto por nodo

| Nodo | Veredicto QA | Base |
|------|--------------|------|
| N-1001 (catalogo + cache) | verified (re-verificado por QA) | 39/39 suite propia; MISS/HIT/invalidacion con corrida propia; persistencia observada (estado del emulador sobrevivio a mis restarts de servicios) |
| N-1002 (saga + compensacion) | verified | saga completa propia (multiple, incluida post-pausa); compensacion restaurando disponibilidad (bateria C); 5 estados observados; cero oversell |
| N-1003 (idempotencia) | verified | replay/mismatch/concurrencia misma key con evidencia propia; dedup interno cubierto por suite (96/96 incluye CatalogSagaDedupTests) + logs de replay |
| N-1004 (documentacion/demos) | verified | quickstart frio propio; ambos demos exit 0 propios; README verificado item por item (arquitectura, ADR, quickstart, mapa de patrones) |

## Veredicto final: SHIP

**SHIP.** Cero hallazgos criticos: sin oversell, sin corrupcion de estado, sin secrets, sin crash
por input externo, sin fuga de internals; las 4 historias del MVP verificadas con evidencia propia
de esta auditoria y la suite 96/96 reproducible desde build limpio.

Riesgo residual (declarado):
- F-01 (Mayor): bajo dependencia colgada los requests cuelgan >150 s en vez de fallar a los 10 s.
  Escenario artificial en un demo local (docker pause), recuperacion limpia, pero NFR-3 no se cumple
  en runtime. Recomendado para la proxima ronda dev (fix de ~1 linea + verificacion con pause).
- F-02 (Mayor): resolver ANTES de publicar el repo publicamente (LICENSE/CHANGELOG/version/git);
  no afecta el funcionamiento ni la evaluacion tecnica del MVP.
- F-03..F-07 (Menores): no bloquean; idoneos para una ronda de pulido junto a F-01.
- Limitaciones ya declaradas por el equipo (documentadas en README/plan): estado del emulador en
  memoria, SNS sin entrega (V1), visibility timeout ~30 s sin DLQ (H-06 V1), cache in-app.

Recomendacion al orquestador: no se exige dev loop para SHIP interno del MVP, pero F-01 y F-02 son
de bajo costo y alto valor de portfolio; una unica ronda dev cierra los 2 Mayores + F-03 antes de
publicar. Si se prefiere rigor maximo sobre el checklist comercial ("sin esto no hay SHIP"), F-02
es el unico item literalmente bloqueante y se resuelve sin tocar codigo.

---

# Ronda 2 - Retest de fixes (2026-11-14, QA adversarial)

Scope cerrado: verificacion independiente de los fixes F-01/F-02/F-03 del dev loop + regresion minima.
Evidencia 100% propia (cero confianza en la evidencia del dev): brain/evidence/2026-11-14-qa-r2-retest.md.

## Resultados por fix

### F-01 (Mayor, NFR-3) - CERRADO, verificado con corrida propia
- Codigo: Timeout = clientConfig.Timeout u 10 s en PinnedCaHttpClientFactory (l.28) y MaxErrorRetry = 0 en los 3 clientes (l.49/65/81), con comentario de justificacion en cada sitio.
- Documentacion: D-0003 enmienda 2026-11-14 documenta la capa timeout (factory custom no impone ClientConfig.Timeout). La decision MaxErrorRetry=0 vive en L-011 + comentarios de codigo en vez de la enmienda: paper trail suficiente, matices anotados.
- Es seguro para los AC: el retry de SDK multiplicaba el timeout (4x10 s = 40,2 s medidos por dev); el retry applicativo real son los loops L-007 + redelivery SQS con handlers idempotentes. Verificado en runtime: saga completa post-unpause y replay sin efectos.
- Timing MEDIDO por QA con emulador en docker pause: GET availability (MISS) HTTP 500 ProblemDetails en 10 181 ms; POST /purchases HTTP 500 ProblemDetails en 10 130 ms. Ambos bajo ~11 s, sin internals. NFR-3 efectivo.
- Visibilidad: 27 entradas LogLevel=Error durante la pausa (SqsSagaWorker + OutboxRelayWorker con stack), vs CERO en ronda 1.
- Recovery: unpause, GET 200 en 59 ms; POST 201 en 97 ms, compra llega a TICKETS_ISSUED con ticket + QR en S3; replay misma key 200 misma compra.

### F-02 (Mayor, DoD) - CERRADO
- LICENSE MIT (texto completo estandar), CHANGELOG.md [0.1.0] 2026-11-14, Version 0.1.0 en Directory.Build.props confirmada en el binario (FileVersion 0.1.0.0 del DLL del build limpio propio).
- .gitignore cubre bin/, obj/, TestResults/, .secrets/, *.user, coverage*.json.
- Residual declarado: git init + commit inicial sigue pendiente (deferido a post-G2 por el dev). No bloquea SHIP interno; hacerlo antes de publicar.

### F-03 (Menor, NFR-6) - CERRADO, umbral verificado QUE MUERDE
- Wiring estatico en los 4 csproj: CollectCoverage=true, Threshold condicional 70/0 por AWS_EMULATOR_ACCESS_KEY_ID, ThresholdType=line, scope = Core por Exclude.
- Suite completa con env (QA propio, servicios abajo): 98/98 verdes (48 Orders, 41 Catalog, 6 Payments, 3 Ticketing), 0 omitidos, exit 0.
- Cobertura recomputada por QA desde los coverage.json de su propia corrida: Catalog 81,2% (407/501), Orders 85,5% (645/754), Payments 92,8% (77/83), Ticketing 87,3% (124/142). Coincide con dev y con ronda 1.
- El gate muerde (reproducido): run filtrado con env, 2/2 tests verdes, error de coverlet.msbuild (total line coverage below the specified 70), exit 1.
- Nota: umbral condicionado al env (threshold=0 sin emulador porque la integracion se auto-omite). Trade-off honesto documentado en L-012; el modo de verificacion corre con env.

## Regresion minima (propia)
- bin/obj wiped (13+13) - build limpio: 0 warnings, 0 errores, exit 0.
- Suite completa con env: 98/98, exit 0 (arriba).
- demo-idempotency.ps1 con servicios reales (post-pausa/post-recovery): exit 0 con todos los checks (201 a TICKETS_ISSUED, delta disponibilidad exacto, replay 200 delta 0, mismatch 422, agotado 422).
- No re-verificado por sin cambios desde ronda 1: p95 (NFR-5) y bateria de 26 payloads hostiles.

## VEREDICTO FINAL: SHIP

Los 2 Mayores (F-01, F-02) y el Menor F-03 estan cerrados con verificacion independiente de QA.
Cero hallazgos criticos o mayores abiertos. El MVP cumple el spec con evidencia reproducible.

Riesgo residual actualizado (deuda documentada V1, no bloqueante):
- F-04 (Content-Type leniente en POST), F-05 (ventana no atomica claim/decremento), F-06 (ruido it-* en el emulador), F-07 (.Api sin tests in-proc): Menores, quedan como deuda V1 documentada.
- git init + commit inicial: pendiente (pre-publicacion).
- Limitaciones declaradas del equipo: SNS sin entrega (V1), visibility timeout ~30 s sin DLQ (H-06 V1), cache in-app, estado del emulador en memoria.

Este veredicto FINAL reemplaza al de ronda 1 (que queda arriba como registro).
