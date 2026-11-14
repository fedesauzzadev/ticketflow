# D-0014: Script de estrés concurrente (H-07) — PS 5.1 + helper Add-Type con HttpClient compartido

Date: 2026-11-14
Reversibility: easy

## Context
H-07 (N-2003): script con N compradores simultáneos sobre X asientos, pasable por
parámetro, que mide invariantes (cero oversell, no-negativa, terminal 100%, cierre
catálogo==ticketing) con salida imprimible. Restricciones: PS 5.1 (L-003: sin pwsh, sin
ForEach -Parallel), medición fiel exige HttpClient reutilizado (L-010: Invoke-WebRequest
mide el cliente, no el servidor).

## Decision
`scripts/stress-test.ps1 -N <n> [-X <asientos>] [-EventId <id>] [-DeadlineSeconds 120]`
(PS 5.1) + helper C# inline vía `Add-Type`: N POST concurrentes (`Task.Run`,
`HttpClient` compartido, `Idempotency-Key` única por comprador, seats=1, PAN aprobada).

- **X = disponibilidad al inicio de la carrera.** `-X` opcional alinea pre-comprando
  (A−X) asientos en secuencia; si A < X → error explícito (no existe endpoint de creación
  de eventos — no se fabrica disponibilidad). Documentado en README.
- Muestreo de disponibilidad en background durante la corrida (assert ≥0 por muestra,
  AC2) + lectura final.
- Polling de las N compras a estado terminal con deadline documentado (default 120 s;
  saga p95 ≈ 0,8 s ⇒ margen ×150) — AC3.
- Resumen imprimible (AC1/AC4): emitidos, rechazados (422 seats-unavailable / FAILED con
  motivo), mínima disponibilidad observada, disponibilidad final, invariante
  `disponibilidad_final + emitidos == X`, PASS/FAIL con exit code no-cero si rompe.
- Cero código de servicio nuevo: el guard anti-oversell ya existe (D-0005); el script es
  pura medición + reporte. Reglas L-006/L-009 para nativos/paths aplican.

## Alternatives considered
- k6/bombardier: runtime externo extra para el evaluador, sale del stack del repo.
- Runspaces PS puros: cliente no fiel (L-010) y verboso para parsear JSON concurrente.
- Test xUnit de carga: no es "script documentado del evaluador" (el AC lo pide operable
  por él).
