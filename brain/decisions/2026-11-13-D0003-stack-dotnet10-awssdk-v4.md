# D-0003: stack .NET 10 + AWS SDK for .NET v4 contra el emulador oficial

Date: 2026-11-13
Reversibility: hard

## Context
El Negocio IMPUSO .NET 10 y microservicios orquestados (intake.md, no negociable). La verificación real del emulador (brain/evidence/2026-11-13-emulator-verification.md) se hizo con el SDK que usará el proyecto y reveló reglas de juego que condicionan todo el código de infraestructura.

## Decision
- **.NET 10 (`net10.0`) + C# latest, ASP.NET Core Minimal APIs** para los 4 servicios. ASP.NET Core + Minimal APIs + `IResult` (menos boilerplate que controllers; el evaluador lee el flujo, no scaffolding).
- **AWS SDK for .NET v4** (4.1.0: `AWSSDK.DynamoDBv2`, `AWSSDK.SQS`, `AWSSDK.S3`, `AWSSDK.SimpleNotificationService`). Reglas verificadas que TODO cliente del proyecto debe cumplir:
  1. Config de endpoint: `ServiceURL = https://127.0.0.1:9297` + `AuthenticationRegion = "us-east-1"`, **NUNCA setear `RegionEndpoint` junto a `ServiceURL`** (en v4 RegionEndpoint gana y el tráfico sale a AWS real — hallazgo L-001).
  2. TLS: `HttpClientFactory` custom (hereda `Amazon.Runtime.HttpClientFactory`) que pinea el thumbprint de la CA del emulador leída de `AWS_EMULATOR_CA_PATH`. El pin además hace imposible llamadas accidentales a AWS real (gate de división: "cero llamadas a nube real" cumplido por diseño, no por convención).
  3. Timeouts explícitos: `Timeout = 10s` en todo cliente (probe lo demostró).
- Credenciales del emulador y CA se obtienen en runtime (script de dev las extrae del contenedor con `docker exec`/`docker cp`) → inyectadas como env vars; **cero secrets en el repo**.
- Serilog NO: `ILogger` + formatter JSON de consola de .NET 10 (cero deps extra; correlation-id `purchase_id` en scope de log).

> **Enmienda 2026-11-14 (QA F-01):** con `HttpClientFactory` custom, el SDK v4 **NO impone**
> `ClientConfig.Timeout` sobre los requests (verificado: peer congelado → 150-240 s colgados).
> La regla 3 se mantiene (10 s) pero la aplica el `HttpClient` que crea el factory:
> `Timeout = clientConfig.Timeout ?? 10 s` en `PinnedCaHttpClientFactory`. Evidencia:
> `brain/evidence/fix-2026-11-14-qa-f01-f02-f03.md` + test `PinnedCaHttpClientFactoryTests`.

## Alternatives considered
- SDK v3 (`AWSSDK.*` 3.7.x): sospechado obsoleto para .NET 10; v4 es la línea actual de AWS para .NET 10. Descartado.
- gRPC entre servicios: mejor perf pero no pedido; HTTP+SQS alcanza y es más legible para el evaluador.
- LocalStack/DynamoDB Local: PROHIBIDO por el Negocio (el emulador oficial fue el requerimiento); además ya verificado que funciona.
- Controllers MVC: más ceremonia sin valor para un portfolio de dominio.
