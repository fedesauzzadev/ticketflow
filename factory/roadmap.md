# Roadmap de entregas — TicketFlow

> Cada entrega es vendible por sí sola. El MVP (V0) responde 1:1 al requerimiento del Negocio; V1/V2 amplían valor de portfolio.

## V0 — MVP: "drop de entradas demostrable" (H-01..H-04)

- **Valor de portfolio concreto:** un evaluador clona, corre un comando, y en ~10 minutos ve: saga con compensación (compra aprobada/rechazada), idempotencia (replay sin duplicar), cache (MISS→HIT→invalidación) y estado persistido en AWS emulado local (:9297) — todo documentado con mapa de patrones.
- **Contenido:** catálogo+disponibilidad con cache observable · saga de compra multi-servicio con compensación · idempotencia HTTP y de comandos · README + scripts de demo.
- **Dependencias:** verificación de servicios del emulador (Tech Lead, Fase 2) — el diseño tiene fallbacks definidos en spec.md; aprobación del dominio en G2.
- **Riesgos:** soporte del emulador distinto al esperado (mitigado: MVP sin servicios exóticos + fallbacks); complejidad de la saga (mitigado: 3 pasos + 1 compensación, scripts imprimen evidencia).

## V1 — "producción creíble": observabilidad + resiliencia (H-05..H-07 + H-10)

- **Valor de portfolio concreto:** pasa de "funciona en demo" a "sé qué pasa cuando falla": trazas distribuidas del saga, fallos inyectados con reintentos/DLQ/circuit breaker visibles, y estrés concurrente sin sobrevender — con la deuda Menor del QA (F-04..F-07) cerrada.
- **Contenido:** trazas (una compra → sus pasos entre servicios) · reintentos con backoff + DLQ + circuit breaker (+ atomicidad de reserva F-05 como AC) · script de estrés concurrente · pulido de deuda QA F-04/F-06/F-07 (H-10).
- **Dependencias:** V0 completo; servicios de mensajería del emulador verificados (si la DLQ no es soportada, se simula con cola secundaria documentada).
- **Riesgos:** el emulador puede no exponer métricas nativas → la trazabilidad se construye a nivel aplicación (log estructurado con correlation-id), decisión del TL.

## V2 — "mantenible": CI + IaC + evolución de dominio (H-08..H-09)

- **Valor de portfolio concreto:** el repo se sostiene solo: un solo comando verifica build+tests+cobertura (y opcionalmente los demos), un pipeline de GitHub Actions versionado queda listo por si el repo gana remote, el entorno local se declara como código (aprovisionamiento from scratch e idempotente), y el dominio gana una regla de negocio real (expiración de reservas con liberación automática) que muestra evolución limpia sobre la arquitectura existente.
- **Contenido (especificado 2026-11-15, ver ACs en backlog):** script de CI local PS 5.1 (build+suite+gate de cobertura, flag de demos) + workflow GH Actions como artefacto (repo sin remote — decisión PO) · manifiesto declarativo + comando idempotente de tablas/colas/bucket ejecutable from scratch (H-08) · hold de asientos con TTL configurable: liberación automática, pago a tiempo prospera, pago tardío rechazado limpio, idempotencia/compensación sin doble liberación, expiración que sobrevive reinicios de servicios, demo imprimible (H-09). F-V1-R3-01 (flake de demo con DLQ >10) absorbido en H-08 AC5.
- **Dependencias:** V1 completa (suite 138/138 estable = gate confiable); decisión del TL sobre mecanismo de TTL (SqsDelaySeconds vs timer/sweeper aplicativo — ADR del TL); decisión del TL sobre cómo el workflow sirve el emulador en un runner real; para el IaC, lógica ensure idempotente existente (EnsureAsync) como base.
- **Riesgos:** el TTL agrega estados intermedios y un terminal nuevo al process manager ⇒ revisar garantías V1 (toda compra termina terminal con motivo; expiración y compensación no duplican liberaciones — AC6); la expiración no puede depender de memoria de proceso (el estado vive en el emulador — AC7); determinismo del gate de CI en sesión local compartida (F-V1-R3-01, mitigado en H-08 AC5).

## Hitos de decisión

- **G2 (Negocio):** aprueba/rechaza el dominio propuesto (spec.md) — sin esto no se construye.
- **Fase 2 (Tech Lead):** verifica servicios del emulador y fija mecanismos concretos (mensajería, estado, cache) con sus ADRs.
