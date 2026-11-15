# Evidencia cruda QA V1 (F4-bis) - 2026-11-14/15, QA adversarial

Raw completo: %TEMP%\opencode\qa-v1\ (logs de suites, trazas, stress logs, respaldo de .artifacts\logs de la corrida 1 en logs-run1\).

## 1. Suite (3 corridas propias, bin/obj wiped antes de la 1ra)
- build limpio: 0 err / 0 warn, exit 0 (45 s).
- run1: 131/131 (P8+T5+O57+C61), exit 0. Core: Payments 93,02 / Ticketing 87,32 / Orders 86,35 / Catalog 81,34.
- run2: 131/131, exit 0. Catalog 80,22 (resto identico).
- run3: 131/131, exit 0. Catalog 81,34 (resto identico).
- Flakiness: 0/3 corridas QA (0 omitidos). Varianza solo Catalog.Core +-1,12 pts; margen minimo al umbral 70: 10,22 pts. Evento dev (S06 1/3 margen <70) NO reproducido; con la corrida limpia S08 del dev: 0/4 recientes.
- .Api (medicion propia con Exclude reducido + Threshold=0): Payments.Api 91,66% linea / Ticketing.Api 92,30% linea. El run estandar EXCLUYE *.Api de la coleccion (csproj Exclude): el numero no aparece en el run default.
- deps: dotnet list --vulnerable --include-transitive: 13 proyectos, 0 vulnerables.

## 2. Cold quickstart (entrada de portfolio)
- docker stop aws-emulator + rm .secrets + servicios muertos => run-local.ps1 => "Todo listo" ~1 min.
- Flujo completo sobre entorno fresco: GET /events 2 seeds (500/250); MISS->HIT 500==500; aprobada pur-dcb7463918364bf9b834646d64f15e6f -> TICKETS_ISSUED + pago APPROVED + ticket tkt-4618e3e81773430cb16571e4fb12fa37 + qr_s3_key; rechazada -> FAILED con motivo, 250 -> 250 restaurada; traza 4/4 servicios + guardian OK.

## 5. H-07 stress (corridas propias)
- N=20 X=5 evt-legacy-tour: PASS exit 0. 5 TICKETS_ISSUED + 15 FAILED con motivo, min disponibilidad 0, final 0, invariante 0+5==5, 20/20 terminales en 14,7 s.
- N=4 X=476 (default) evt-mj-tribute-night: PASS exit 0. 4+0, final 472, invariante 472+4==476.
- Hostiles: evento inexistente -> exit 1 FALLO limpio (404 event-not-found); N=0 -> exit 2; X=-5 -> exit 2; DeadlineSeconds=5 -> exit 2; X=99999>avail -> exit 2. Sin cuelgues.

## 6. H-10
- F-04: text/plain, text/plain;charset=utf-8, application/xml, x-www-form-urlencoded y sin Content-Type -> 415 ProblemDetails accionable (echo del recibido); application/json;charset=utf-8 -> 201; key NO consumida por los 415 (replay posterior 201->200 misma compra); delta disponibilidad -1 exacto (1 sola compra efectiva).
- F-06: tras 3 suites completas, GET /events default: 2 seeds, 0 it-*.
- F-07: medicion propia en seccion 1 (91,66 / 92,30). Tests in-proc existen y ejercitan endpoints.

## 7. Regresion MVP
- demo-cache.ps1: exit 0 (MISS 471 -> HIT 471 -> compra -> MISS 470 -> HIT 470).
- demo-idempotency.ps1: exit 0 (replay 200 misma compra, delta 0, mismatch 422, agotado 422 sobre evento agotado real del stress).
- Idempotencia concurrente: 4 POST simultaneos misma key -> 201,200,200,200 / 1 purchase_id unico / delta -1 exacto / TICKETS_ISSUED.
- p95 spot: GET availability HIT 0,9 ms (n=40, objetivo <50); saga completa 858 ms (n=25, objetivo <2000).

## 8. Seguridad superficie nueva
- /admin/failure y /admin/dlq sin auth, localhost-only (Kestrel default), demo local; documentado en codigo. Riesgo portfolio bajo: documentar en README o toggle env.
- Secrets: 0 en codigo/scripts; 1 cosmestico en brain/evidence/2026-11-13-emulator-verification.md:24 (aws_secret_access_key del emulador 2026-11-13, inutil fuera de localhost:9297 y ya rotada) => F-V1-03. El grep F4 (secret=) no lo detecto por el patron.
- .gitignore: .secrets/ .artifacts/ TestResults/ coverage*.json OK. TODO/FIXME reales en src: 0 (1 falso positivo, "todo" en espanol).

## 3. H-05 trazas (evidencia propia)
- Aprobada pur-9f06f7277bf64f4b911be9eff8bd4b5b: 10 lineas, 4/4 servicios (orders 7, catalog 1, payments 1, ticketing 1), timestamps ISO estrictamente ascendentes, exit 0.
- Rechazada pur-05830b81d6c140cb9dd40c6599e2babd: reserva -> DECLINED(motivo) -> release-seats(compensacion) -> seats-released -> FAILED identificable.
- Paralelas: traza de A (pur-f091b745a01447e6aa88592914a07496) menciona a B (pur-3dfb17cee12845db8c1dc7a2122ffbc8): 0 veces.
- Grep propio sobre TODOS los logs: runs 13-19 digitos 0 (ids enmascarados previo), cvv+3digitos 0.

## 4. H-06 adversarial (mas alla del demo)
- demo-resilience.ps1 (corrida QA): 15/15 PASS, exit 0 (backoff 560 ms, DLQ, fail-fast 32 ms, invariante 478==480-2).
- B1 (HALLAZGO F-V1-01): POST :5080/admin/failure release-seats persistent + compra PAN 0002:
  compra pur-96bf2a61937846469894d687e8a43223 queda SEATS_RESERVED para siempre (reason vacio), disponibilidad 478->477 permanente (>2 min tras DELETE de la regla + 45 s de gracia), DLQ catalog con 2 release-seats (doble compensacion), 2do step-failed ignorado (fallo ya anotado), sin redelivery/self-heal/redrive. Logs: logs-run1\catalog.log y orders.log.
- B2 persistent reserve-seats: FAILED con reason, delta 0. OK.
- B3 transient count=3 (limite): FAILED con reason + compensacion delta 0 + mensaje en DLQ payments. OK.
- B4 DELETE /admin/failure/no-existe: 404 ProblemDetails. OK.
- B5 GET /admin/dlq en 4/4 servicios: 200 (5080: 3 msg, 5081: 0, 5082: 2, 5083: 0). OK.
- B6 24 payloads hostiles contra POST /admin/failure: count string/-5/0/11/1e9/2.7 -> 400; count null -> 201 (default 2, por diseno); mode explode/TRANSIENT/5 -> 400; step 10k/65/path-traversal/unicode/null/numero -> 400; body vacio/null/array/string/JSON roto/anidado 80 -> 400; campos extra -> 201 (ignorados); text/plain -> 415. Cero 5xx, proceso vivo.
- B7 breaker 2 ciclos de pausa: ciclo1 GET 10.051/10.015/10.015 s + fail-fast 3 ms; compra durante pausa 500 en 10,059 s (explicito, nada colgado); unpause -> 200 en 57 ms. Ciclo2 idem (10,008/10,014/10,020 + 6 ms; recovery 40 ms). OPEN log x3, CLOSED x3. Saga nueva post-ciclos TICKETS_ISSUED pur-38000cc8947d4764bc747e678fba0b1d.
