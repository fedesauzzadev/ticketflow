# D-0002: dominio del ejercicio — venta de entradas de alta demanda (TicketFlow)

Date: 2026-11-13
Reversibility: medium

## Context
El Negocio pidió explícitamente que la fábrica proponga el "ejercicio de uso actual" (intake.md). El dominio debe hacer naturales los 4 pilares exigidos: orquestación multi-servicio con estado compartido, idempotencia con efecto observable, cache en un path de lectura intensiva, y servicios AWS contra el emulador local (:9297). Además debe explicarse en 2 minutos y probarse con curl. Es PROPUESTA: se confirma en G2.

## Decision
**TicketFlow: venta de entradas para eventos de alta demanda (flash-sale ticketing).** 4 microservicios .NET 10: Catálogo (disponibilidad + cache), Órdenes (process manager del flujo de compra), Pagos (autorización simulada idempotente), Ticketing (emisión + notificación).

Por qué encaja 1:1: miles de compradores refrescando disponibilidad ⇒ cache natural (GET availability, hit/miss observable); clientes móviles reintentando compras con timeout ⇒ idempotencia natural (Idempotency-Key); la compra atraviesa reserva→pago→emisión con desfase ⇒ saga con compensación natural (pago rechazado libera asiento). Es "uso actual": drops/flash sales de entradas son problema vigente y reconocible (conciertos, deportes).

## Alternatives considered
- Checkout e-commerce clásico (carrito→pago→stock→envío): correcto pero visto mil veces en portfolios; no diferencia al evaluador.
- Ride-hailing/matching de repartidores: exige geolocalización/matching que agrega complejidad sin naturalizar idempotencia/cache y alarga la explicación >2 min.
- Pagos instantáneos tipo Pix: idempotencia natural pero el path de lectura intensiva con cache queda forzado (saldos deben ser frescos) y el dominio financiero invita escrutinio de precisión que no aporta a la demo.
