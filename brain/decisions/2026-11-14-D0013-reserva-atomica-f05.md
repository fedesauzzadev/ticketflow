# D-0013: Crash-coherencia de la reserva (F-05) — decremento atómico idempotente de un solo ítem

Date: 2026-11-14
Reversibility: medium

## Context
F-05 (QA F4, absorbido por H-06 AC5): ventana no atómica claim→decremento en
`DynamoDbEventRepository.ReserveAsync` (y state→incremento en `ReleaseAsync`) — un crash
entre las dos escrituras deja la compra avanzando sin decremento (drift de
disponibilidad); el redelivery CONFIRMA el estado inconsistente. QA sugirió
`TransactWriteItems` (soporte del emulador NO verificado — el probe 7/7 cubrió ops
básicas y condicionales, no transacciones) o decremento-primero con reversal.

## Decision
Decremento **atómico e idempotente en UN solo UpdateItem** sobre el ítem del evento:
`ADD availability :-n` + `ADD decrement_pids :pid` con
`ConditionExpression: NOT contains(decrement_pids, :pid)` (más el guard
`availability >= n` existente). La condición falla ⇒ ya se decrementó por esa compra ⇒ el
redelivery responde `seats-reserved` SIN segundo decremento (exactamente lo que exige
AC5). `ReleaseAsync` simétrico: `ADD availability :+n` + `DELETE decrement_pids :pid`
bajo condición `contains(...)`.

- Elimina la ventana **por construcción**: no hay "entre dos escrituras".
- Funciona con los condicionales YA verificados del emulador (probe DynamoDB 7/7) —
  cero dependencia de TransactWriteItems.
- El set `decrement_pids` está acotado: |pids| ≤ disponibilidad inicial (el guard
  anti-oversell de D-0005 impide decrementar más) — 500 pids GUID ≈ 20 KB, dentro del
  límite de atributo. Límite documentado, no aplicable a escala demo.
- El claim `RESERVATION#pid` se conserva para lógica de negocio/compensación.
- AC5 se prueba con: test concurrente (doble `ReserveAsync` mismo pid → exactamente 1
  decremento) + redelivery del comando reserve-seats → sin drift (lecturas API
  antes/después).

## Alternatives considered
- `TransactWriteItems` (2 ítems): requeriría spike de soporte del emulador y no aporta
  nada sobre la variante single-item.
- Decremento-primero con reversal: re-introduce la ventana simétrica en el release.
- Tabla separada de decrementos con lectura-escritura: no atómica sin transacción.
