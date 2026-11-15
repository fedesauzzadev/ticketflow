# Evidencia — Probe TTL V2: DelaySeconds en FIFO + Scan filtrado (mecanismo del hold, N-3003)

**Fecha:** 2026-11-15 · **Autor:** Tech Lead (Fase TL V2) · **Resultado: DECISIVO** — 2 corridas idénticas contra el emulador de esta sesión (`aws-emulator` Up, creds de la corrida extraídas como en F2).

## Pregunta que responde

H-09/N-3003 necesita un mecanismo de expiración de holds. El PO exigió verificar contra el
emulador ANTES de elegir: **¿SQS FIFO del emulador soporta `DelaySeconds`** (por mensaje o por
cola)? En SQS REAL las colas FIFO NO lo soportan — si el emulador lo permitiera, sería una
infieles a AWS real sobre la que NO se puede diseñar un portfolio creíble. Además: el mecanismo
candidato "sweeper aplicativo" necesita **Scan + FilterExpression sobre un atributo AUSENTE en
parte de los ítems** (los holds tienen `expires_at`, el resto no) — forma NO verificada en el
probe de F2 (los filtros verificados eran sobre atributos siempre presentes).

## Método

Mismo patrón que el probe de F2 (`2026-11-13-emulator-probe-Program.cs`): proyecto console
desechable net10.0 + AWSSDK 4.1.0 (SQS+DynamoDBv2), CA pineada por thumbprint, SigV4 con creds
de la corrida (`docker exec` + `docker cp`). Código: `2026-11-15-ttl-probe-Program.cs`. Salida:
`2026-11-15-ttl-probe-output.txt`. Recursos del probe con sufijo timestamped, borrados al final.

## Resultados (idénticos en 2 corridas)

| ID | Test | Resultado | Implicancia |
|---|---|---|---|
| P1 | `CreateQueue` FIFO con atributo `DelaySeconds=3` | **ACEPTADO** (SQS real lo rechaza) | Infieles del emulador: NO diseñar sobre esto — un diseño que dependa del atributo a nivel de cola solo funcionaría contra el emulador |
| P2 | `SendMessage` a FIFO con `DelaySeconds=4` por mensaje | **RECHAZADO**: `AmazonSQSException: Value 4 for parameter DelaySeconds is invalid. Reason: The request include parameter that is not valid for this queue type.` | **Fiel a SQS real**: delay por mensaje en FIFO NO existe. El "timer via SQS FIFO" está descartado por capacidad, no por gusto |
| P3 | Control: cola ESTÁNDAR + `DelaySeconds=3` por mensaje | **HONRADO** (invisible antes de la ventana, visible después) | El emulador SÍ implementa el mecanismo de delay — el rechazo de P2 es semántica FIFO deliberada (paridad con AWS), no una capacidad faltante |
| P4a | `Scan` con `FilterExpression "#st = :st AND expires_at < :now"` en tabla con ítems SIN `expires_at` | **OK exacto**: devuelve solo el ítem vencido esperado (`S#1`), sin error por atributo ausente | La primitiva del sweeper aplicativo está verificada: filtro directo sobre `status + expires_at` con atributo ausente en no-holds |
| P4b | Variante `attribute_exists(expires_at) AND expires_at < :now` | **OK exacto** (`S#1`) | Forma defensiva también disponible si hiciera falta |

## Conclusión para el diseño (→ ADR D-0017)

1. **DelaySeconds por mensaje en FIFO: IMPOSIBLE** (P2) — y el atributo a nivel de cola (P1)
   es una infieles del emulador que además aplicaría el delay a TODOS los mensajes de la cola
   (inutilizable para la saga). El TTL **no puede** vivir en el transporte.
2. **Sweeper aplicativo persistente** (BackgroundService en Órdenes + `expires_at` en la tabla
   `purchases` + Scan filtrado + transición CAS): todas sus primitivas verificadas (P4a/P4b +
   condicionales ya verificados 7/7 en F2). Cumple AC7 (sobrevive reinicio: el estado vive en
   DynamoDB del emulador, el sweeper es stateless).
3. Una cola ESTÁNDAR dedicada con delay por mensaje (P3) sería técnicamente viable, pero se
   descarta: re-encolado ante cambio de TTL, mezcla de semánticas FIFO/estándar en el manifiesto
   y un mecanismo paralelo al guard CAS existente — más piezas para el mismo comportamiento
   observable (ver alternativas en D-0017).
