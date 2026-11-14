# D-0007: cache in-app (cache-aside + TTL + invalidación explícita) con X-Cache observable

Date: 2026-11-13
Reversibility: easy

## Context
H-01 exige: 1ª llamada `X-Cache: MISS`, 2ª `X-Cache: HIT`, misma disponibilidad correcta; e invalidación tras compra (AC2). El intake contemplaba ElastiCache/Redis "si el emulador lo soporta". Verificación: el emulador solo expone 9297 HTTPS — **no existe data plane Redis/memcached** (ElastiCache sería solo control plane vía API, inútil como cache real).

## Decision
**Cache a nivel aplicación en el servicio Catálogo** (`Microsoft.Extensions.Caching.Hybrid` de .NET, cero infra extra):
- **Cache-aside** sobre `GET /events/{id}/availability`: MISS → lee DynamoDB → pobla cache con **TTL 30s** → responde `X-Cache: MISS`; HIT → responde desde cache con `X-Cache: HIT` y el mismo valor.
- **Invalidación explícita**: el worker de Catálogo que procesa `reserve-seats`/`release-seats` muta disponibilidad con decremento condicional (D-0005) y **remueve la key del cache en la misma operación** ⇒ la próxima lectura refleja el valor nuevo (AC2). La invalidación vive en el mismo proceso que el cache (ventaja de la decisión).
- Header `X-Cache: HIT|MISS` en la respuesta: requisito observable del AC, no negociable.
- Timeout de TTL corto (30s) acota cualquier drift residual entre compra e invalidación (defensa en profundidad, documentada).

## Alternatives considered
- Redis en contenedor aparte: rompería "todo lo AWS en el emulador" y agrega una pieza de infra que el AC no pide.
- Cache "servida" por DynamoDB: no es cache (sigue siendo IO al almacén) y no da HIT/MISS observable.
- Sin cache + header falso: viola el AC y la honestidad técnica. Descartado.
