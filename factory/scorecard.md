# Scorecard — Review G3 (V1)

**Fecha:** 2026-11-15 · **Corrida:** V1 completa (PO → TL G1 → S05..S08 → QA F5 3 rondas) · **Veredicto QA final V1: SHIP**

## Entrega

| Métrica | Valor |
|---|---|
| Historias entregadas / planificadas | 4/4 (H-05, H-06, H-07, H-10) |
| AC cumplidas | 18/18 (4+5+4+3+2 trazas extra QA) — todas con evidencia corrida dev+QA |
| Nodos brain | N-2001..N-2004 verified (level full) |
| Commits V1 | 5188147 → 03f3031 (9 commits, árbol limpio salvo views orquestador) |

## Tests y performance

| Métrica | Valor |
|---|---|
| Tests | 138/138 (131 + 7 de fixes; QA re-corrió suite completa en cada ronda) |
| Cobertura | Core 80,2–93% + .Api 91,7–94,8% — umbral ≥70% en run estándar |
| Breaker | fail-fast 3–14 ms vs timeout 10.015 ms (2 ciclos docker pause, recovery 40–57 ms) |
| p95 spot QA | GET HIT 0,9 ms · saga 858 ms (en objetivo) |
| Estrés | N=20/X=5 → 5+15, invariante 0+5==5, 20/20 terminales 14,7 s; hostiles exit 1/2 limpios |

## QA adversarial V1

| Métrica | Valor |
|---|---|
| Rondas | 3 (NO-SHIP → fix → NO-SHIP → fix quirúrgico → SHIP) |
| Hallazgos | Mayor F-V1-01 (compensación no toleraba persistent) + Mayor F-V1-R2-01 (dedup tragaba 2º step-failed) — ambos cerrados y verificados con repros propios de QA · Menores F-V1-02/03/04 cerrados · F-V1-R3-01 (flake demo DLQ>10) residual documentado |
| Payloads hostiles | 24 contra /admin/* → 400/415, 0×5xx, procesos vivos |
| Trazas | 4 compras propias: 4/4 servicios, cronológicas, 0 mezcla, 0 PAN/CVV |
| Regresión MVP | demo-cache/demo-idempotency exit 0 · idempotencia concurrente 1 compra · quickstart frío ~1 min |

## Bloqueos y aprendizajes

L-013..L-015 nuevos (encoding PS 5.1 por pipe, connection limit serializaba POSTs, dedup id de mensajes de fallo debe incluir step). 0 bloqueos sin resolver. 1 ronda extra autorizada por anti-bloqueo (causa raíz 1 línea) — disclosure completo en G3.

## Riesgo residual

- F-V1-R3-01: demo-resilience AC2 flakea si DLQ payments >10 entries en la misma sesión del emulador (peek página 10) — quickstart limpio no afectado; fix opcional en V2.
- Heredados: /admin sin auth (localhost-only, documentado), techo escritura emulador (~13 s/20 POSTs), estado en memoria del emulador (no reiniciar en demo).
