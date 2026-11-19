# D-0021: G4 aprobado + v0.3.0 + cierre de corrida

Date: 2026-11-19
Reversibility: easy

## Context
Review G4 con el Negocio tras QA F6 SHIP (ronda 2: hammer del gate 7/7, F-V2-01..05 cerrados).

## Decision
1. Entrega V2 (H-08 CI+IaC + H-09 hold TTL) APROBADA — bump v0.3.0 (CHANGELOG [0.3.0], Directory.Build.props, tag v0.3.0).
2. CIERRE DE CORRIDA: roadmap V0/V1/V2 completo — no queda valor planificado. La fábrica cierra con el portfolio final (repo local con tags v0.1.0/v0.2.0/v0.3.0, 186/186 tests, QA adversarial SHIP en las 3 entregas).
3. Riesgos residuales aceptados documentados (scorecard G4): ventana zombi por diseño con advertencia en reason, gate sin stdout de tests (trx propuesto si flake reaparece), /admin localhost-only, techo de escritura del emulador.

## Alternatives considered
- Proponer un V3 (p.ej. multi-región, despliegue real): fuera del alcance pedido por el Negocio (todo local, emulador); se menciona como posible continuación futura sin comprometer nada.
