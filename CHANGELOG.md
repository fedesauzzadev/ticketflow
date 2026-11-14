# Changelog

Formato [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/),
versionado [SemVer](https://semver.org/lang/es/).

## [0.1.0] - 2026-11-14

Primera versión entregable del MVP (historias H-01..H-04, veredicto QA: SHIP).

### Agregado

- **Catálogo de eventos con disponibilidad y cache observable** (H-01): `GET /events`,
  `GET /events/{id}/availability` con header `X-Cache: HIT|MISS` (cache-aside in-app,
  TTL 30 s + invalidación en cada reserva), estado en DynamoDB con seed condicional.
- **Saga de compra multi-servicio con compensación** (H-02): `POST /purchases` recorre
  reserva → pago → emisión orquestada por process manager en Órdenes sobre SQS FIFO;
  el rechazo de pago compensa restaurando la disponibilidad. Estados observables por
  `GET /purchases/{id}`; entradas con payload QR en S3 (`GET /tickets/{id}`).
- **Idempotencia observable** (H-03): `Idempotency-Key` con claim condicional — replay
  de la misma key+body devuelve la misma compra (200) sin efectos nuevos; mismatch de
  body 422 `idempotency-key-reused`; dedup por correlación en cada worker de la saga.
- **Documentación de portfolio** (H-04): README con quickstart de un comando, mapa de
  arquitectura y patrones; scripts de demo reproducibles (`demo-cache.ps1`,
  `demo-idempotency.ps1`); ADRs en `brain/decisions/`.
- **Todo sobre el emulador oficial de AWS en local** (HTTPS + SigV4 en `:9297`): CA
  pineada por thumbprint, endpoint loopback-only verificado al arranque — cero nube
  real, cero secrets en el repo, cero costos.
- Suite de tests: 96/96 (unit + integración contra el emulador real) con umbral de
  cobertura ≥70% de líneas en los 4 proyectos Core (NFR-6).

### Seguridad

- Validación de entrada en todas las fronteras HTTP y del worker de saga (26 payloads
  hostiles auditados por QA: todos 400/404/405 accionables, cero 5xx, sin fuga de
  internals en los ProblemDetails).
- Timeouts efectivos de 10 s en toda la IO al emulador (NFR-3, fix post-QA F-01).

### Corregido (ronda post-QA, sin cambio funcional)

- F-01: timeout de 10 s efectivo contra dependencia colgada (el HttpClient del
  `PinnedCaHttpClientFactory` ahora lleva el `ClientConfig.Timeout`; antes quedaba
  suspendido 150-240 s con el emulador pausado).
- F-02: LICENSE (MIT), CHANGELOG y versión 0.1.0.
- F-03: umbral de cobertura NFR-6 cableado en la suite (falla el build si un Core
  baja de 70% de líneas).
