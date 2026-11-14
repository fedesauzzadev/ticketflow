# D-0009 — Idempotencia observable S03: claim condicional de key, replay 200, mismatch/agotado 422, dedup por correlación

**Fecha:** 2026-11-13 · **Estado:** aceptada · **Reversibilidad:** medium · **Contexto:** H-03 / N-1003 (S03).

## Decisión

1. **Idempotency-Key store en DynamoDB** (tabla `orders_idempotency`, pk `IDEMPOTENCY#<key>`,
   en el servicio Órdenes). Claim por `PutItem` condicional `attribute_not_exists(pk)` —
   primitiva ya verificada en el emulador (D-0005). Sin `TransactWriteItems`: no verificada
   en el emulador; el claim-first + writes condicionales cierra las ventanas sin apostar a
   fidelidad no probada (misma regla que L-004/L-005).

2. **Hash del body = SHA-256 de la re-serialización canónica (snake_case) del request
   validado**, no del body crudo: dos bodies semánticamente iguales (distinto
   whitespace/orden de keys) son el mismo pedido. Estándar Stripe/IETF recurre al crudo;
   elegimos canónico por robustez de demo (el replay de un evaluador con `curl` repetido es
   byte-idéntico de todas formas).

3. **Flujo del POST**: peek de la key → si existe: hash igual ⇒ **200 con el estado actual
   de la MISMA compra** (mismo `purchase_id`, cero efectos nuevos; sin re-validar
   disponibilidad — el replay gana sobre el agotado); hash distinto ⇒ **422
   `idempotency-key-reused`**. Si no existe: validación sincrónica de disponibilidad ⇒
   agotado **422 `seats-unavailable`** (sin consumir la key ni crear nada — reintentar con
   la misma key tras liberarse asientos debe funcionar); recién entonces claim condicional →
   create + outbox → **201**. Ventana de crash claim-sin-compra: el replay completa la
   creación con el `purchase_id` que la key registró (el claim es dueño del id).

4. **Status codes**: mismatch de body = **422** (lo fija plan §S03 "hash de body → mismatch
   = 422"; además es la semántica del IETF draft de Idempotency-Key). Nota: el prompt de S03
   mencionaba 409; prevalece el plan (G1) — desvío documentado en la evidencia. Agotado =
   **422** (plan §2 contratos: "422 si asientos agotados/ya reservados"). Replay exitoso =
   **200** (no 201: no se creó nada nuevo).

5. **Validación sincrónica de disponibilidad vía la API pública del Catálogo**
   (`GET /events/{id}/availability`, env `CATALOG_BASE_URL`, default
   `http://localhost:5080`, timeout 3 s explícito — NFR-3) con **fail-open**: Catálogo
   caído/404/tiempo agotado ⇒ no bloquear la compra; el guard definitivo sigue siendo el
   decremento condicional de la saga (fallo explícito `FAILED` async). El pre-check es UX,
   no el anti-oversell. HTTP entre servicios (no lectura cross-tabla): el contrato público
   del Catálogo es la frontera, como en el diagrama del plan.

6. **Dedup de comandos internos**: dos capas ya existentes (SQS FIFO
   `ContentBasedDeduplication` con `MessageDeduplicationId={type}:{purchase_id}` + claims
   condicionales del receptor `RESERVATION#`/`PAYMENT#`/`ISSUANCE#`). Delta S03: **log
   observable** "duplicado detectado: efecto descartado" en los handlers cuando el outcome
   es duplicado (H-03 AC2 pide el log, no solo el efecto).

## Consecuencias
- El replay de una compra `PENDING` no re-emite `reserve-seats` (sin heal de outbox): la
  ventana claim→enqueue ya existía en S02 y queda fuera de alcance (documentada en N-1003).
- `POST /purchases` pasa de `201` a `201|200|422` — el contrato del plan §2 ya lo anticipaba.
- Un 422 por agotado NO registra la key: reintentar con la misma key cuando haya asientos
  debe crear la compra (semántica de "probá de nuevo").
