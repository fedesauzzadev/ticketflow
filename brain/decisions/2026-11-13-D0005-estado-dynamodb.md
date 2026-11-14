# D-0005: estado de negocio en DynamoDB del emulador (verificado)

Date: 2026-11-13
Reversibility: medium

## Context
Se necesita un almacén de estado que (a) viva en el emulador :9297 (H-01 AC4: sobrevive reinicios de los microservicios), (b) permita decremento atómico de disponibilidad sin oversell, (c) sea mainstream (spec prohíbe exóticos). Candidato del PO: DynamoDB "a verificar".

## Decision
**DynamoDB del emulador, verificado 7/7** (evidencia: 2026-11-13-emulator-verification.md) — incluyendo la primitiva crítica del dominio: `UpdateItem` con `ConditionExpression` (decremento atómico) y rechazo correcto con `ConditionalCheckFailedException` cuando la condición no cumple. Esa única primitiva implementa anti-oversell Y el caso 409/422 de H-03 AC3 (asientos agotados) sin bloqueos ni transacciones distribuidas.

- Una tabla por agregado/servicio (prefijo `pk` discriminada): `catalog_events`, `catalog_reservations`, `orders_purchases`, `payments_auths`, `orders_outbox`, `tickets`, `orders_idempotency` / `catalog_idempotency` / `payments_idempotency` / `ticketing_idempotency` (ver D-0008 idempotency store).
- Creación de tablas idempotente al arrancar cada servicio (con poll de la ventana DELETING del emulador — hallazgo de fidelidad).
- Acceso vía **repositorios** (patrón exhibido) que ocultan DynamoDB; la interfaz permite cambiar el almacén sin tocar dominio (regla de robustez del spec).
- Limitación asumida y documentada: el estado del emulador es EN MEMORIA — sobrevive reinicios de microservicios (lo que exige el MVP) pero se borra si se reinicia el contenedor del emulador. El quickstart lo declara explícito.

## Alternatives considered
- Otro almacén del emulador: no hace falta — DynamoDB cumple 7/7 con la primitiva condicional exacta que el dominio necesita.
- RDBMS en contenedor aparte: viola "todo lo AWS apunta al emulador".
- JSON en S3: sin escrituras condicionales atómicas (oversell garantizado bajo concurrencia). Descartado.
