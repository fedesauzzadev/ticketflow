# Spec — TicketFlow (propuesta de dominio, sujeta a aprobación del Negocio en G2)

**Fecha:** 2026-11-13 · **PO:** Fase 1 fábrica NSDD · **Estado:** PROPUESTA

## Visión

TicketFlow es una plataforma backend de venta de entradas para eventos de alta demanda ("drops"/flash sales), montada como microservicios .NET 10 orquestados sobre servicios AWS emulados localmente (:9297). Un evaluador entiende el dominio en 2 minutos, lo corre con un comando y comprueba con curl/scripts que la compra es una saga con compensación, que los reintentos no duplican efectos y que la disponibilidad se sirve de cache con hit/miss observable.

## Propuesta de dominio y justificación

**Dominio: venta de entradas para eventos con alta demanda (flash-sale ticketing).**

- Por qué es "uso actual": los drops de entradas (conciertos, deportes) son un problema reconocible y vigente — lectura masiva de disponibilidad por miles de clientes que refrescan (cache), reintentos de compra desde móvil con timeouts (idempotencia), y una compra que atraviesa inventario + pago + emisión (saga). Los tres pilares del requerimiento surgen del dominio, no están forzados.
- Alternativa descartada 1 — checkout e-commerce clásico (carrito→pago→stock→envío): técnicamente correcto pero ubiquitous en portfolios; no diferencia al evaluador.
- Alternativa descartada 2 — ride-hailing/matching de repartidores: process manager atractivo pero exige geolocalización/matching que agrega complejidad sin hacer más naturales la idempotencia ni la cache, y alarga la explicación más allá de 2 minutos.

## Problema y usuarios

- **Problema:** demostrar en un portfolio, con evidencia ejecutable, orquestación de microservicios + idempotencia + cache + AWS local en .NET 10 con clean architecture.
- **Actores:**
  - Evaluador/reclutador: clona, corre, prueba y lee la documentación.
  - Dueño del portfolio: presenta y evoluciona el proyecto.
  - Comprador (actor de dominio, simulado por curl/scripts): consulta disponibilidad y compra entradas.

## Alcance MVP

1. Catálogo de eventos con disponibilidad y **path de lectura intensiva servido por cache** con hit/miss observable (header `X-Cache: HIT|MISS`) e invalidación sobre compra.
2. **Compra de entradas como saga orquestada** (process manager) que atraviesa: reserva de asientos → autorización de pago (simulada) → emisión de entradas, con **compensación observable** (pago rechazado ⇒ asiento liberado).
3. **Idempotencia observable**: reintento HTTP con la misma `Idempotency-Key` y replay de comandos entre servicios no duplican efectos (disponibilidad, pagos, tickets).
4. Estado de negocio **persistido en servicios AWS del emulador local** (:9297): sobrevive reinicios de los microservicios.
5. **Documentación de portfolio**: README con arquitectura, decisión de dominio, quickstart copy-paste y scripts que imprimen evidencia de idempotencia y cache; mapa de patrones aplicados.

## Flujo de negocio principal (paso a paso)

1. El evaluador consulta `GET /events` → lista de eventos (seed) con nombre, fecha, precio y disponibilidad.
2. `GET /events/{id}/availability` → primera llamada `X-Cache: MISS`, segunda `X-Cache: HIT` (path intensivo: todos los compradores refrescan disponibilidad).
3. `POST /purchases` con header `Idempotency-Key: K1` y cuerpo `{evento, asiento(s), tarjeta}` → se crea la compra y arranca la saga:
   - **Reserva**: el orquestador ordena reservar asientos al servicio de catálogo (idempotente por `purchase_id`+asiento). Estado: `SEATS_RESERVED`.
   - **Pago**: autorización ante el servicio de pagos (idempotente por key derivada de `purchase_id`; simulador documentado: PAN terminado en `0002` ⇒ rechazada, cualquier otro ⇒ aprobada).
   - **Emisión**: aprobado ⇒ el servicio de ticketing emite la entrada y dispara la notificación; estado final `TICKETS_ISSUED`.
   - **Compensación**: rechazado ⇒ se liberan los asientos y la compra termina `FAILED` con motivo; la disponibilidad vuelve a su valor previo (observable).
4. Replay: repetir el `POST` con la misma key devuelve la **misma** compra sin nuevos efectos.
5. Reiniciar los microservicios (no el emulador): `GET /purchases/{id}` sigue devolviendo la compra ⇒ el estado vive en el emulador AWS, no en memoria de proceso.

## Qué demuestra (mapa al requerimiento)

| Requisito del Negocio | Dónde se observa en el MVP |
|---|---|
| Microservicios orquestados | Saga/process manager del paso 3: 4 servicios (.NET 10), estado compartido de la compra visible en `GET /purchases/{id}` |
| Operaciones idempotentes | Replay con misma key y comandos duplicados entre servicios: disponibilidad y tickets idénticos a una sola ejecución (AC H-03) |
| Cache | `GET /events/{id}/availability` con `X-Cache: HIT\|MISS` e invalidación tras compra (AC H-01) |
| AWS vía emulador local | Persistencia + mensajería + artefactos contra `localhost:9297`; datos sobreviven reinicios de servicios; cero llamadas a nube real |
| Clean architecture + patrones | Estructura por servicio y patrones nombrados en código/docs — el "cómo" lo define el Tech Lead (Fase 2) |

## Servicios AWS candidatos — a verificar por TL en Fase 2

El soporte del emulador **no está verificado**. Candidatos (mainstream, nada exótico):

| Propósito | Candidato | Fallback si el emulador no lo soporta |
|---|---|---|
| Estado de negocio | DynamoDB *(a verificar)* | El almacén clave-valor/documental que soporte el emulador; la interfaz de repositorio aísla el cambio |
| Mensajería entre servicios | SQS *(a verificar)* | El mecanismo de cola soportado; en último caso HTTP + outbox, documentado por TL como ADR |
| Notificación fan-out | SNS *(a verificar)* | Evento interno + log visible; SNS pasa a V1 |
| Artefacto de entrada (PDF/QR) | S3 *(a verificar)* | Ticket como payload firmado en el estado; S3 pasa a V1 |
| Cache | ElastiCache/Redis *(a verificar)* | Cache a nivel aplicación con TTL + invalidación explícita (patrón documentado) |

**Regla de robustez:** el dominio y el flujo de negocio NO dependen del servicio AWS específico; dependen de que exista al menos un almacén de estado y un mecanismo de mensajería/confianza entre servicios. El MVP prohíbe depender de servicios exóticos (Kinesis, EventBridge, Step Functions, Lambda): Step Functions además reemplazaría al orquestador .NET, contradiciendo el requerimiento.

## Fuera de alcance (explícito)

- UI/frontend web o móvil (no pedido; los "clientes" son curl/scripts).
- Pagos reales, integraciones con pasarelas externas: el pago es un simulador determinístico documentado.
- Nube AWS real, facturación, multi-región, Kubernetes.
- Reventa, códigos promocionales, cuentas de usuario/login, seat maps gráficos.
- Concurrencia masiva real / pruebas de carga (V1: script de estrés simple).

## Supuestos asumidos (reversibles)

1. El emulador soporta al menos un servicio de estado y uno de mensajería; si no, el TL documenta el sustituto local manteniendo el contrato AWS SDK (endpoint configurable :9297).
2. 4 microservicios es suficiente para demostrar orquestación sin inflar la demo (catálogo, órdenes/orquestador, pagos, ticketing).
3. El "pago aprobado/rechazado" se controla desde el request (PAN de prueba) para que el evaluador dispare ambos caminos sin infraestructura extra.
4. Idioma de código/docs técnicos: inglés en identificadores, español en documentación de negocio (coherente con brain language: es).

## Riesgos de negocio

| Riesgo | Mitigación |
|---|---|
| El emulador no soporta los servicios esperados | Spec robusto a la incertidumbre: fallbacks definidos; verificación es la primera tarea del TL (Fase 2) |
| Saga demasiado compleja para demostrar en 10 min | 4 servicios, 3 pasos + 1 compensación; scripts de demo imprimen la evidencia |
| Scope creep (features de e-commerce) | Fuera de alcance explícito arriba; el corte MVP son 4 historias |
| Portfolio poco creíble si "AWS" es solo cosmético | AC dura: reiniciar servicios sin reiniciar emulador conserva el estado; todo endpoint apunta a :9297 |

## Métricas de éxito observables (heredadas del intake)

1. Un tercero clona el repo y levanta emulador + servicios con comandos copy-paste del README; el flujo completo (consulta→compra→ticket) funciona.
2. Script de demo de idempotencia imprime: misma key ⇒ misma compra, disponibilidad sin duplicar.
3. Script de demo de cache imprime: `MISS` → `HIT` → tras compra, valor nuevo (invalidación).
4. Compra con pago rechazado termina `FAILED` con asiento liberado (compensación observable vía API).
5. README incluye arquitectura, decisión de dominio, quickstart y mapa de patrones.
