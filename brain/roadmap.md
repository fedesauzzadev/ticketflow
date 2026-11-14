# Roadmap

> Vertical slices: each one works end-to-end before the next starts.

## Current slice
**mvp** — corte V0 del portfolio (H-01..H-04, factory/backlog.md). Nodos: N-1001 → N-1002 → N-1003 → N-1004. Bloqueado hasta: verificación de servicios del emulador (TL, Fase 2) + G2 (aprobación de dominio).

## Slice backlog
| # | Slice | Depends on | Status | Nodes |
|---|-------|------------|--------|-------|
| mvp | V0 "drop de entradas demostrable": catálogo+cache, saga con compensación, idempotencia, docs+scripts | G2 (dominio) + Fase 2 (servicios emulador) | planned | N-1001, N-1002, N-1003, N-1004 |
| v1 | V1 "producción creíble": trazas distribuidas, reintentos/DLQ/circuit breaker, estrés concurrente (H-05..H-07) | slice mvp done | candidate | — |
| v2 | V2 "mantenible": CI, IaC local, expiración de reservas TTL (H-08..H-09) | slice v1 done | candidate | — |

## Done
-
