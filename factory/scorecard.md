# Scorecard — Review G4 (V2)

**Fecha:** 2026-11-18 · **Corrida:** V2 completa (PO → TL probe+plan → S09..S11 → QA F6 2 rondas) · **Veredicto QA final V2: SHIP** · **G4 aprobado por el Negocio: 2026-11-19 — cierre de corrida, bump v0.3.0**

## Entrega

| Métrica | Valor |
|---|---|
| Historias entregadas / planificadas | 2/2 (H-08 CI+IaC, H-09 hold TTL) + F-V1-R3-01 absorbido y cerrado |
| AC cumplidas | 17/17 funcionales (9 H-08 + 8 H-09) — todas re-verificadas por QA con evidencia propia; AC1 de H-08 con asterisco en ronda 1, determinista tras fix (7/7) |
| Nodos brain | N-3001..N-3003 verified (level full) |
| Commits V2 | 050cdd4 → 858a9e0 (6 commits: plan, iac, ci, hold, qa r1, fix, qa r2) |

## Tests y performance

| Métrica | Valor |
|---|---|
| Tests | 186/186 (138 base + 17 IaC + 2 DLQ + 29 hold) — QA re-corrió suite ×2 por ronda |
| Gate CI | ci.ps1 7/7 exit 0 consecutivas en retest (ronda 1: 3/7 rojas — F-V2-01) |
| Cobertura | umbral ≥70% Core+.Api+Provision en run estándar (Provision 86,4%) |
| IaC | from-scratch 225 s → 15 recursos → verify 15/15 1:1 · re-apply ×2 idempotente · guard pilla manifiesto editado |
| Hold TTL | pago t−0,8 s → TICKETS_ISSUED · t+2,5 s → 409 sin doble liberación · crash 4 servicios → EXPIRED tras restart · replay delta 0 · mini-stress 6 holds: disponibilidad exacta |
| Estrés (regresión) | N=20/X=5 PASS, 0 oversell, cierre 0+5==5, 15,3 s |

## QA adversarial V2

| Métrica | Valor |
|---|---|
| Rondas | 2 (NO-SHIP → fix → SHIP) |
| Hallazgos | Mayor F-V2-01 (gate flaky: writers saga compartidos entre collections paralelas — 3/7 corridas) cerrado con serialización del assembly + -m:1, verificado hammer 7/7 y mecanismo en grep propio · Menores F-V2-02/03/04 + Info F-V2-05 todos cerrados en el mismo loop |
| Hostiles | 18 contra endpoints nuevos (POST /purchases/{id}/payment, hold mal tipado) → 400/409/404 accionables, 0×5xx |
| Regresión V1/MVP | suite ×2 186/186 · idempotencia 4×key = 1 compra · breaker+DLQ 15/15 con DLQ sucia (23 entries) · traza 4/4 · p95 24 ms/850 ms · demos 4/4 exit 0 |

## Bloqueos y aprendizajes

0 bloqueos sin resolver. 0 rondas extra de anti-bloqueo (el loop QA→dev→QA estándar de 1 ronda alcanzó). Sin learnings nuevos formales — el hallazgo F-V2-01 re-confirma L-008 (aislamiento de estado compartido en tests contra infra real) ahora a nivel de collection.

## Riesgo residual

- Ventana zombi >2×TTL+30 s por diseño (D-0017): saga con pago aprobado y Pagos muerto mucho tiempo → EXPIRED con advertencia en el reason ("verificar en Pagos antes de reintentar"); consistencia verificada (sin ticket, sin oversell, disponibilidad exacta).
- Gate descarta stdout de dotnet test (culpable = etapa, no test); trx persistente propuesto si un flake reaparece.
- Heredados: /admin sin auth (localhost-only, documentado), techo escritura emulador.
