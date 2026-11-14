# Vision

## What this project is
Portfolio profesional personal: backend de **venta de entradas para eventos de alta demanda (TicketFlow)** en .NET 10 — microservicios orquestados mediante saga/process manager (reserva → pago → emisión, con compensación), operaciones idempotentes con efecto observable (replay no duplica), cache con hit/miss observable en el path de disponibilidad, y persistencia/mensajería sobre servicios AWS corriendo contra el emulador local (:9297). Todo documentado para que un evaluador lo corra y lo compruebe con curl/scripts en ~10 minutos. Dominio propuesto por la fábrica (el Negocio lo pidió así), aprobación en G2 — ver ADR D-0002.

## What this project is NOT
- No es un e-commerce completo: sin carrito, reventa, promociones ni cuentas de usuario.
- No usa nube AWS real ni paga nada: TODO apunta al emulador local localhost:9297.
- No tiene UI/frontend (no pedida): los clientes son curl/scripts.
- No procesa pagos reales: el servicio de pagos es un simulador determinístico (PAN `...0002` rechaza).
- No depende de servicios AWS exóticos (Kinesis/EventBridge/Step Functions/Lambda fuera del MVP).

## Constraints
- Stack obligatorio impuesto por el Negocio: .NET 10, microservicios orquestados, clean architecture + patrones, idempotencia, cache, AWS vía emulador local instalado en `aws-emulator/` (puerto 9297).
- Soporte de servicios del emulador sin verificar: el TL lo verifica en Fase 2 antes de planificar (fallbacks en factory/spec.md).
- Idioma: documentación de negocio en español (brain language: es).
- Prima demostrable y documentado sobre feature-completo (es un ejercicio de portfolio).
