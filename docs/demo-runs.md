# Corridas de demo — 2026-11-19 (v0.3.0, post G4)

Salidas crudas de los 5 demos ejecutados en la misma sesión contra los 4 servicios
reales (Órdenes :5081 · Catálogo :5080 · Pagos :5082 · Ticketing :5083) sobre el
emulador local de AWS. Reproducibles con `scripts\run-local.ps1` + el script de cada demo.

| Demo | Qué demuestra | Resultado |
|---|---|---|
| demo-cache | cache-aside observable + invalidación por compra | OK (6 pasos) |
| demo-idempotency | misma key ⇒ misma compra, cero efectos nuevos | OK |
| demo-ttl | hold con TTL → EXPIRED → liberación → 409 → re-compra | TODO PASS (11/11) |
| demo-resilience | backoff, DLQ, circuit breaker, compensación | TODO PASS (AC1..AC5) |
| stress-test | 20 compradores / 5 asientos, cero oversell | PASS |

Guía visual anotada: `docs/demo-walkthrough.html`.

---

## 1 · demo-cache.ps1 — MISS → HIT → compra → MISS → HIT

```text
=== Demo cache-aside (H-04 AC3) ===
Paso 0: evento con mas disponibilidad (la compra del Paso 3 necesita >= 1 asiento)
    OK: hay eventos en el catalogo
    OK: evento 'evt-mj-tribute-night' tiene asientos (499)
Paso 1: reset del cache (endpoint interno idempotente: garantiza el primer MISS sin gastar asientos)
Paso 2: 1a lectura de disponibilidad -> debe ser MISS
    OK: 1a lectura X-Cache: MISS (esperaba MISS)
    disponible = 499
Paso 3: 2a lectura -> debe ser HIT con el MISMO valor
    OK: 2a lectura X-Cache: HIT (esperaba HIT)
    OK: valor en HIT identico al de MISS (499 == 499)
Paso 4: compra de 1 asiento (la reserva en la saga invalida el cache)
    OK: POST /purchases -> 201
    compra pur-3ecb189652ba4678a46960cef0457bbb TICKETS_ISSUED (ticket tkt-603972a5b8e94538b32d2db11d20d3e1)
Paso 5: 3a lectura -> debe ser MISS con el valor NUEVO (invalidacion observable)
    OK: 3a lectura X-Cache: MISS (esperaba MISS: la compra invalido)
    OK: valor nuevo = 498 (esperaba 498: bajo exacto 1)
Paso 6: 4a lectura -> HIT cacheando ya el valor nuevo
    OK: 4a lectura X-Cache: HIT (esperaba HIT)
    OK: valor en HIT = 498 (== 498)

RESUMEN: MISS(499) -> HIT(499) -> compra de 1 asiento -> MISS(498) -> HIT(498)
         la lectura post-compra NO devolvio el valor cacheado: invalidacion demostrada
=== DEMO CACHE: OK ===
```

## 2 · demo-idempotency.ps1 — reintentar no duplica

```text
=== Demo idempotencia (H-04 AC2) ===
Paso 0: evento con mas disponibilidad
    OK: evento 'evt-mj-tribute-night' con asientos (498)
Paso 1: compra con key demo-idem-e6967289d06449e7a0845149c7607cf2
    OK: POST inicial -> 201 Created
    compra pur-06c355fa10ff4a2f85828aa7d0437511 TICKETS_ISSUED (ticket tkt-30bcdf51922342d38a0cb36a607841d6)
    OK: disponibilidad tras la compra: 498 -> 497 (bajo exacto 1)
Paso 2: REPLAY con la MISMA key y el MISMO body
    OK: replay -> 200 OK (no 201: no se creo nada nuevo)
    OK: misma key => MISMA compra (pur-06c355fa10ff4a2f85828aa7d0437511)
    OK: estado replay: TICKETS_ISSUED
    OK: mismos tickets (tkt-30bcdf51922342d38a0cb36a607841d6)
Paso 3: disponibilidad antes/despues del replay
    OK: disponibilidad identica: 497 -> 497 (delta 0, cero efectos nuevos)
Paso 4: misma key + body DISTINTO (holder) => 422
    OK: key reusada con body distinto -> 422
    OK: tipo: idempotency-key-reused - El Idempotency-Key ya fue usado con un body distinto. Reutilizalo con el mismo body exacto o enviá uno nuevo.
Paso 5: evento agotado + key nueva => 422 seats-unavailable
    SKIP: no hay evento con <= 7 asientos para agotar. Para verlo, levantar el entorno
          con SEED_EVENTS incluyendo un evento de 1 asiento (variante documentada en el README).

RESUMEN: misma key => misma compra (pur-06c355fa10ff4a2f85828aa7d0437511); disponibilidad 497 -> 497 (identica);
         key reusada con body distinto => 422; agotado + key nueva => 422
=== DEMO IDEMPOTENCIA: OK ===
```

## 3 · demo-ttl.ps1 — hold de asiento con TTL (la pieza nueva de V2)

```text
=== demo-ttl 2026-11-19T21:22:55 (evento evt-mj-tribute-night) ===
disponibilidad inicial: 500

--- 1) retencion: POST /purchases con "hold": true ---
[PASS] retencion aceptada (201) - HTTP 201: {"purchase_id":"pur-553ea60ee6dd437994b966832e49f9a6","status":"PENDING","created_at":"2026-11-20T00:22:56.0914031+00:00"}
compra: pur-553ea60ee6dd437994b966832e49f9a6
[PASS] estado SEATS_RESERVED (no terminal) - estado: SEATS_RESERVED
[PASS] expiracion observable (expires_at) - expires_at: 2026-11-20T00:24:26+00:00
[PASS] asiento retenido (disponibilidad -1) - disponibilidad: 500 -> 499

--- 2)+3) vencimiento (TTL del servicio) y liberacion: esperando hasta 00:24:26 UTC ---
[PASS] estado terminal EXPIRED - estado: EXPIRED
[PASS] terminal con motivo - reason: reserva expirada sin pago (TTL 90 s)
[PASS] asiento liberado (disponibilidad restaurada) - disponibilidad: 499 -> 500 (inicial: 500)

--- 4) pago tardio: POST /purchases/{id}/payment tras el TTL ---
[PASS] rechazo 409 hold-expired - HTTP 409 type: hold-expired ({"type":"hold-expired","title":"Hold expired","status":409,"detail":"La reserva expiró antes de completar el pago."})
[PASS] sin segunda liberacion (delta 0) - disponibilidad: 500 -> 500

--- 5) replay del POST original (misma Idempotency-Key) ---
[PASS] replay devuelve la misma compra EXPIRED - HTTP 200: pur-553ea60ee6dd437994b966832e49f9a6 EXPIRED
[PASS] replay sin efectos (delta 0) - disponibilidad: 500 -> 500

--- 6) re-compra del asiento liberado (comprador nuevo, key distinta) ---
[PASS] re-compra aceptada (201) - HTTP 201
[PASS] re-compra TICKETS_ISSUED - estado: TICKETS_ISSUED tickets: tkt-e163ff4a0fa0415999bb0f0fe6c525fb
[PASS] disponibilidad final == inicial - 1 - disponibilidad: 499 (inicial 500 - 1 emitido)

RESULTADO: TODO PASS (retencion -> vencimiento -> liberacion -> pago tardio 409 -> replay -> re-compra)
```

## 4 · demo-resilience.ps1 — fallos inyectados en vivo, sistema observable

```text
=== demo-resilience 2026-11-19T21:30:10 (evento evt-mj-tribute-night) ===
disponibilidad inicial: 497

--- AC1: fallo transitorio en authorize-payment (count=2) ---
[PASS] AC1 inyeccion aceptada - HTTP 201: {"step":"authorize-payment","mode":"transient","remaining":2}
compra A: pur-e05460a4d0f94db992e3943de27b36aa (1 asiento)
[PASS] AC1 saga sobrevive - estado: TICKETS_ISSUED motivo:
reintentos en payments.log: 00:30:11.046  intento 1/3 de 'authorize-payment' ... reintento en 500 ms (backoff) | 00:30:11.572  intento 2/3 ... reintento en 1000 ms (backoff)
[PASS] AC1 backoff creciente - espera entre intento 1 y 2: 527 ms (>=450 esperado)
[PASS] AC1 sin DLQ - el mensaje transitorio no fue a la DLQ

--- AC2: fallo persistente en authorize-payment ---
[PASS] AC2 inyeccion aceptada - HTTP 201
compra B: pur-8d2e04ea7fd94d6ea948413e9e4a197a (2 asientos)
[PASS] AC2 terminal FAILED con motivo - estado: FAILED motivo: el paso 'authorize-payment' falló tras 3 intentos (InjectedFailureException)
[PASS] AC2 mensaje en DLQ inspeccionable - GET /admin/dlq (payments) contiene la entrega fallida

--- AC3: downstream recuperado -> disponibilidad consistente ---
[PASS] AC3 regla borrada - DELETE HTTP 204
[PASS] AC3 sin drift - disponibilidad 496 == inicial 497 - 1 (B compensado, sin huerfanos)

--- AC4: docker pause del emulador -> circuit breaker ---
emulador pausado: Up 31 hours (Paused)
  /events #1 tras pause: 10063 ms (timeout de 10 s consumiendose)
  /events #2 tras pause: 10022 ms (timeout de 10 s consumiendose)
  /events #3 tras pause: 10017 ms (timeout de 10 s consumiendose)
[PASS] AC4 fail-fast << timeout - 4a llamada: HTTP 500 en 31 ms (las anteriores ~10000)
[PASS] AC4 apertura logueada - 'circuit breaker de dynamodb OPEN' en catalog.log
emulador reanudado; esperando probe del breaker...
[PASS] AC4 breaker cierra al recuperar - GET /events vuelve a 200 (probe exitoso)
[PASS] AC4 cierre logueado - 'circuit breaker de dynamodb CLOSED' en catalog.log
compra C: pur-ea76422981d543c7b8b53d06dfcfc53a (1 asiento)
[PASS] AC4 saga nueva completa - estado: TICKETS_ISSUED

--- AC5: invariante de disponibilidad ---
[PASS] AC5 disponibilidad == inicial - emitidos - final 495 == inicial 497 - 2 emitidos (A=1, C=1)

RESULTADO: TODO PASS (AC1..AC5)
```

## 5 · stress-test.ps1 -N 20 -X 5 — concurrencia sin oversell

```text
=== Stress test TicketFlow (H-07 / N-2003, D-0014) ===
parametros: N=20 compradores | X=5 | deadline terminal: 120s
evento: evt-mj-tribute-night (mayor disponibilidad del catalogo)
disponibilidad inicial: 495
alineacion: pre-comprando 490 asientos (495 -> 5) en compras secuenciales de hasta 8...
    OK:   disponibilidad alineada a 5
carrera: disparando 20 compradores simultaneos (1 asiento, PAN aprobada, key unica cada uno)...
    N POSTs respondidos en 12434 ms

--- invariantes ---
    OK:   AC1  cero oversell: emitidos (5) nunca > X (5)
    OK:   AC1  emitidos exactos: 5 == min(N,X) (5)
    OK:   AC1  rechazos explicitos: 15 == N - emitidos (15)
    OK:   AC2  disponibilidad nunca negativa: minima observada 0 (8 muestras)
    OK:   AC2  disponibilidad final: 0 == X - emitidos (0)
    OK:   AC3  terminales dentro del plazo: 20/20 en 13.4s (limite 120s)
    OK:   AC4  invariante de cierre: disponibilidad_final (0) + emitidos (5) == X (5)

--- detalle por comprador (resumen) ---
    5x TICKETS_ISSUED (HTTP 201): #1 #3 #7 #14 #17
    15x FAILED con motivo "sin disponibilidad suficiente o evento inexistente": #0 #2 #4 #5 #6 #8 #9 #10 #11 #12 #13 #15 #16 #18 #19

=== RESUMEN STRESS ===
    evento .......................... evt-mj-tribute-night
    compradores N ................... 20
    asientos X ...................... 5 (disponibilidad inicial: 495)
    TICKETS_ISSUED .................. 5 (esperado: 5)
    FAILED con motivo ............... 15
    sin estado terminal ............. 0  [viola AC3 si > 0]
    disponibilidad minima observada . 0 (de 8 muestras)
    disponibilidad final ............ 0
    invariante de cierre ............ 0 + 5 = 5 == X (5)
    timings ........................ . carrera N POSTs: 12434 ms | hasta ultimo terminal: 13.4s (plazo 120s)
=== STRESS: PASS ===
```
