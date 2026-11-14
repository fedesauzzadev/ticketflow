# División Backend Cloud (.NET + AWS local) · síntesis

## Detección
- Señales del intake: .NET 10, microservicios orquestados, clean architecture, idempotencia, cache, servicios AWS contra emulador local (:9297), documentación.

## Ajustes de equipo
- Roles core tal cual (PO → Tech Lead → Dev → QA). UX/designer/artist no aplican (no hay UI pedida).
- Tech Lead con énfasis: verificación real de qué servicios soporta el emulador AWS antes de planificar.

## Mapa de skills (por rol)
| Rol | Skills a cargar | Cuándo |
|---|---|---|
| PO | — (conocimiento inline) | — |
| Tech Lead | — | — |
| Dev | — | — |
| QA | `security-audit` | checklist de seguridad/resiliencia de la Fase 4 |

## Gates de dominio (QA) — se suman a references/security.md
- Idempotencia demostrada con evidencia: replay/mensaje duplicado NO duplica efectos de negocio.
- Cache demostrada con evidencia: hit/miss observable y invalidación correcta.
- Todo lo "AWS" apunta al emulador local (localhost:9297); cero llamadas a nube real; cero secrets en repo.

## DoD de dominio
- README raíz con: arquitectura (diagrama), mapa de patrones aplicados (dónde está cada uno), quickstart para levantar emulador + servicios y scripts para probar idempotencia y cache.

## Artefactos de dominio
- `docs/architecture.md` + ADRs (Tech Lead) · README raíz con quickstart (Dev, al cerrar el MVP).
