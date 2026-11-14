# D-0001: init-brain no-interactivo — defaults asumidos

Date: 2026-11-13
Reversibility: easy

## Context
init-brain corrió en modo no-interactivo dentro del workflow software-factory (orquestador, Negocio no disponible fuera de gates). Todo supuesto debía quedar registrado.

## Decision
- `language: es` (idioma del Negocio).
- `commands` vacíos: el repo aún no tiene solución .NET; el Tech Lead los fija en Fase 2 con la estructura real del proyecto (dotnet build/test/run).
- Modelos por tier (provider propio `zai-coding-plan`): strong=`glm-5.3` (mayor razonamiento, default del harness), balanced=`glm-5.3-highspeed`, fast=`glm-5.3-flash` (clasificados por tier de capacidad, no por vendor).
- `remote_url` = origin del clone del kit (`C:/Users/hp/Downloads/NSDD/brainloom-remote.git`).
- Proyecto tratado como NUEVO (solo existía `aws-emulator/` docker + factory/): no se corrió `adopt`.

## Alternatives considered
- Esperar al Negocio para confirmar: bloquea la fábrica, contra la regla anti-bloqueo; todo supuesto es reversible editando `brain/config.yaml`.
