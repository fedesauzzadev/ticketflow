# Intake — Fase 0

**Fecha:** 2026-11-13
**Negocio:** dueño del portfolio (usuario único del requerimiento).

## Requerimiento literal (sin interpretar)

> Necesito que generes un proyecto para un portfolio profesional personal. Tiene que estar montado en .NET10, con microservicios orquestados. Obviamente con clean architecture, patrones y buen codigo. Necesito que me propongas que ejercicio de uso actual podemos montar para que sean microservicios orquestados, con operaciones idempotentes, cache, y todo montado en AWS con el simualdor aws local que está instalado. Es un ejercicio practico para portfolio. Necesito que además este documentado

## Objetivo

Proyecto de portfolio profesional personal que demuestre, de forma verificable y ejecutable, microservicios orquestados en .NET 10 con clean architecture, operaciones idempotentes, cache, y backend 100% sobre servicios AWS corriendo contra el emulador AWS local ya instalado. Todo documentado.

## Usuarios

- Reclutadores / evaluadores técnicos que clonan el repo, lo corren y lo leen.
- El propio dueño (demo, evolución futura).

## Definición de éxito

1. Un tercero clona el repo, levanta el emulador + los servicios con comandos documentados, y ve el flujo de negocio completo funcionando.
2. El proyecto demuestra con evidencia: orquestación entre microservicios, idempotencia (reintentos no duplican efectos), cache (hit/miss observable), clean architecture y patrones nombrados en el código/docs.
3. Documentación: README con arquitectura, decisión de dominio, cómo correr, cómo probar idempotencia y cache, y mapa de patrones aplicados.

## Stack

| Ítem | Estatus |
|---|---|
| .NET 10 | **Obligatorio** (impuesto por Negocio) |
| Microservicios orquestados | **Obligatorio** (impuesto por Negocio) |
| Clean architecture + patrones + buen código | **Obligatorio** (impuesto por Negocio) |
| Operaciones idempotentes | **Obligatorio** (impuesto por Negocio) |
| Cache | **Obligatorio** (impuesto por Negocio) |
| AWS vía emulador local instalado | **Obligatorio** (impuesto por Negocio) |
| Ejercicio/dominio de uso actual | **A proponer por la fábrica** (el Negocio pidió propuesta explícitamente) |
| UI/frontend | No pedido → fuera de scope salvo que el PO lo justifique mínimo |

## Constraints

- Emulador AWS local YA instalado en `aws-emulator/` (Docker image oficial `aws-emulator`, entrypoint `aws-emulator -b 0.0.0.0`, **puerto 9297**). Todo lo que sea "AWS" debe correr contra este emulador, no contra nube real.
- El dominio del ejercicio lo propone la fábrica (el Negocio pidió "ejercicio de uso actual"). Se decide con el PO y se confirma en G2.
- Es ejercicio práctico de portfolio: prima demostrable y documentado sobre feature-completo.

## Pendientes de decisión (se resuelven dentro de la fábrica, G1)

- Dominio/ejercicio concreto (propuesta del PO).
- Servicios AWS del emulador a usar (SQS/SNS/DynamoDB/etc. — verificar soporte del emulador).
- Mecanismo de orquestación (saga/choreography vs orchestrator process manager).
- Estrategia de cache (DynamoDB DAX no existe local → likely cache app-level con invalidación explícita, o ElastiCache-Redis si el emulador lo soporta).
