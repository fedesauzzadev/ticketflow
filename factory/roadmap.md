# Roadmap de entregas — TicketFlow

> Cada entrega es vendible por sí sola. El MVP (V0) responde 1:1 al requerimiento del Negocio; V1/V2 amplían valor de portfolio.

## V0 — MVP: "drop de entradas demostrable" (H-01..H-04)

- **Valor de portfolio concreto:** un evaluador clona, corre un comando, y en ~10 minutos ve: saga con compensación (compra aprobada/rechazada), idempotencia (replay sin duplicar), cache (MISS→HIT→invalidación) y estado persistido en AWS emulado local (:9297) — todo documentado con mapa de patrones.
- **Contenido:** catálogo+disponibilidad con cache observable · saga de compra multi-servicio con compensación · idempotencia HTTP y de comandos · README + scripts de demo.
- **Dependencias:** verificación de servicios del emulador (Tech Lead, Fase 2) — el diseño tiene fallbacks definidos en spec.md; aprobación del dominio en G2.
- **Riesgos:** soporte del emulador distinto al esperado (mitigado: MVP sin servicios exóticos + fallbacks); complejidad de la saga (mitigado: 3 pasos + 1 compensación, scripts imprimen evidencia).

## V1 — "producción creíble": observabilidad + resiliencia (H-05..H-07)

- **Valor de portfolio concreto:** pasa de "funciona en demo" a "sé qué pasa cuando falla": trazas distribuidas del saga, fallos inyectados con reintentos/DLQ/circuit breaker visibles, y estrés concurrente sin sobrevender.
- **Contenido:** trazas (una compra → sus pasos entre servicios) · reintentos con backoff + DLQ + circuit breaker · script de estrés concurrente.
- **Dependencias:** V0 completo; servicios de mensajería del emulador verificados (si la DLQ no es soportada, se simula con cola secundaria documentada).
- **Riesgos:** el emulador puede no exponer métricas nativas → la trazabilidad se construye a nivel aplicación (log estructurado con correlation-id), decisión del TL.

## V2 — "mantenible": CI + IaC + evolución de dominio (H-08..H-09)

- **Valor de portfolio concreto:** el repo se sostiene solo: cada push buildea y testea, el entorno local se declara como código, y el dominio gana una regla de negocio real (expiración de reservas) que muestra evolución limpia sobre la arquitectura existente.
- **Contenido:** pipeline CI (build+tests) · IaC/compose versionado del entorno · hold de asientos con TTL y liberación automática.
- **Dependencias:** V1 (tests estables para gatear el CI); decisión del TL sobre runner de CI.
- **Riesgos:** el TTL de reservas agrega estados intermedios a la saga ⇒ requiere revisión del process manager (diseño del TL, no del PO).

## Hitos de decisión

- **G2 (Negocio):** aprueba/rechaza el dominio propuesto (spec.md) — sin esto no se construye.
- **Fase 2 (Tech Lead):** verifica servicios del emulador y fija mecanismos concretos (mensajería, estado, cache) con sus ADRs.
