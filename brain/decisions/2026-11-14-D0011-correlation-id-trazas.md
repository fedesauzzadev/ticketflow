# D-0011: Correlation-id = purchase_id + logs agregados a archivos + script de traza

Date: 2026-11-14
Reversibility: easy

## Context
H-05 (N-2001): el evaluador sigue una compra punta a punta por correlation-id con log
estructurado, filtrando con un comando documentado. Hoy: los 4 servicios loguean JSON
consola (`AddJsonConsole` 4/4), `SagaEnvelope.PurchaseId` ya propaga el id por SQS, y casi
todas las líneas de saga ya lo llevan — pero `run-local.ps1` arranca con `-NoNewWindow` sin
redirect: la salida es efímera e in filtrable post-hoc. El PO dejó al TL: mecanismo,
formato y si correlation-id es propio o coincide con purchase_id.

## Decision
1. **correlation_id == purchase_id.** Ya nace en `POST /purchases` (está en la respuesta
   201 → "obtenible de la respuesta" ✓), ya viaja en el envelope SQS a los 4 servicios,
   ya es el `MessageGroupId`, y es único por compra → AC4 (aislamiento entre compras
   paralelas) gratis. Cero propagación nueva; NO se agrega campo al envelope.
2. **Formato**: JSON consola existente (Timestamp ISO → ordenable lexicográficamente);
   el servicio se deriva de `Category` (`TicketFlow.<Servicio>.*`). Sweep mínimo en S05:
   cada paso de la saga loguea ≥1 línea con la propiedad `PurchaseId` (el patrón ya
   existente) en los 4 servicios.
3. **Agregación**: `run-local.ps1` redirige stdout de cada servicio a
   `.artifacts/logs/<svc>.log` (+ `<svc>.err.log`; PS 5.1 no permite ambos al mismo
   archivo). Tail en vivo documentado: `Get-Content -Wait`. `.artifacts/` al .gitignore.
4. **Filtro documentado**: `scripts/trace-purchase.ps1 -PurchaseId <id>` — Select-String
   sobre `*.log`, sort por Timestamp ISO, tabla `timestamp | servicio | paso`; asserta que
   la salida no contiene PAN/CVV (AC2, defensa extra sobre lo ya garantizado por QA F4).
5. Sin PAN/CVV en logs: ya se cumple (QA F4, 380+ líneas auditadas); el script lo
   re-asserta por corrida.

## Alternatives considered
- Correlation-id propio (GUID) en el envelope: propagación nueva en 4 servicios para cero
  ganancia observable — el purchase_id YA es el identificador de la compra.
- OpenTelemetry/APM: emulador sin data plane de telemetría; fuera de alcance (N-2001).
- Agente/collector de logs: infra nueva para un producto local de demo; PS basta.
