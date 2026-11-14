# Scorecard — Review G2 (MVP V0)

**Fecha:** 2026-11-14 · **Corrida:** Fase 0→4 + dev loop 1 ronda · **Veredicto QA final: SHIP**

## Entrega

| Métrica | Valor |
|---|---|
| Historias entregadas / planificadas | 4 / 4 (H-01..H-04) |
| AC cumplidas / totales | 15 / 15 (4+4+3+4, Given/When/Then verificables) |
| Nodos brain | 4/4 `verified` (level: full, evidencia corrida c/u) |
| Slices build | S01..S04 secuenciales, 0 re-trabajos de AC |

## Tests y performance

| Métrica | Valor |
|---|---|
| Tests | 98/98 verdes (48 Orders + 41 Catalog + 6 Payments + 3 Ticketing), en ≥2 corridas independientes (dev + QA) |
| Cobertura (4 Core, umbral cableado ≥70%) | Catalog 81,2% · Orders 85,5% · Payments 92,8% · Ticketing 87,3% |
| Build | 0 warnings / 0 errores (warnings-as-errors), verificado con bin/obj borrados |
| p95 medidos vs objetivo | availability HIT 1,4–5,8 ms (<50) · POST 20,7–22,7 ms (<200) · saga 768–790 ms (<2000) — dev y QA midieron independiente |
| Timeout NFR-3 | 10,1 s efectivo bajo `docker pause` (antes >150 s), recovery limpio |

## QA adversarial

| Métrica | Valor |
|---|---|
| Hallazgos críticos | 0 |
| Mayores | 2 (F-01 timeout, F-02 DoD) — ambos cerrados en 1 ronda de dev loop |
| Menores | 5 (F-04..F-07) — deuda documentada V1 |
| Rounds de rework | 1 de 2 máximos |
| Gates de división | 4/4 (idempotencia ✓ cache ✓ 100% AWS-local ✓ cero secrets ✓) |
| Pruebas propias QA | 26 payloads hostiles (0×5xx), 4 POST simultáneos misma key → 1 compra, 3 carreras de oversell → 0 oversell, quickstart frío, docker pause 7 min |

## Bloqueos y aprendizajes

12 learnings (L-001..L-012): 2 escalados de contexto (SDK→AWS real, SNS sin entrega) resueltos por TL; resto resueltos por dev sin escalar. 0 bloqueos sin resolver.

## Riesgo residual

- F-04..F-07 menores (deuda V1 documentada en qa-report).
- `git init` + commit inicial pendiente (pre-publicación, post-G2).
- SNS entrega y ElastiCache sin data plane en el emulador → V1 con releases nuevos.
- Mensaje SQS en limbo tras crash (~30 s visibility) — tolerado por handlers idempotentes, DLQ formal en V1 (H-06).
