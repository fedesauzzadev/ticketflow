# Evidencia — Verificación real del emulador AWS local (:9297)

**Fecha:** 2026-11-13 · **Autor:** Tech Lead (Fase 2) · **Resultado: EMULADOR VERIFICADO Y USABLE** (19/20 checks OK; el único FAIL es un gap de fidelidad conocido de SNS con fallback definido en spec.md).

## 1. Entorno

- Docker Desktop 29.9.2 (Windows). Imagen buildeada desde `aws-emulator/Dockerfile` (`docker build -t aws-emulator:verified`) — build OK.
- Contenedor: `docker run -d --name aws-emulator -p 9297:9297 aws-emulator:verified` — Up. (Reemplazó un contenedor previo `aws-emulator:latest` que ya ocupaba 9297; mismo entrypoint `aws-emulator -b 0.0.0.0`.)
- Probe ejecutado con **AWS SDK for .NET v4.1.0** (`AWSSDK.Core/DynamoDBv2/SQS/S3` 4.1.0 + `AWSSDK.SimpleNotificationService` 4.1.0) sobre `net10.0` (SDK instalado: dotnet 10.0.403). Código: `2026-11-13-emulator-probe-Program.cs`. Salida: `2026-11-13-emulator-probe-output.txt`.

## 2. Mecanismo del emulador (descubierto, NO documentado en el intake)

1. **HTTPS con CA propia self-signed** (`CN=rcgen self signed cert`). NO hay modo HTTP.
2. **Auth SigV4 OBLIGATORIA**: al arrancar genera credenciales NUEVAS por corrida y las escribe en `/home/emulator/.aws/emulator/9297/emulator_credentials` (más `config`, `ca.pem`, `truststore.p12` para Java). NO valen credenciales dummy tipo localstack.
3. Health check: `curl --cacert ca.pem https://localhost:9297/__admin/health` → `ok` (verificado).
4. **Estado EN MEMORIA**: sobrevive reinicios de los microservicios (lo que exige H-01 AC4) pero se BORRA si se reinicia el contenedor del emulador.
5. Docs oficiales: docs.aws.amazon.com/emulator/latest/userguide (repo github.com/aws/aws-emulator).

### Credenciales de esta corrida (efímeras, mueren con el contenedor; NO son secretos de producción)

```
[default]
aws_access_key_id = EMULATOR71030A222FA2
aws_secret_access_key = AWS_SECRET_ACCESS_KEY_REDACTED
```

> Redacción F-V1-03 (2026-11-15): el valor literal del `aws_secret_access_key` fue
> reemplazado por un placeholder. Era una credencial efímera del emulador local (muerta:
> solo válida contra `localhost:9297` con la CA pineada de esa corrida, y el contenedor ya
> la regeneró), pero disparaba secret-scanners y violaba NFR-2 literal.
CA de esta corrida: `2026-11-13-emulator-ca.pem` (thumbprint `DA5942E22972F2DFB12FD734222BD11A9B6F91EF`).

## 3. Tabla de verificación por servicio (salida del probe)

| Servicio | Operación | Resultado | Uso en MVP |
|---|---|---|---|
| dynamodb | ListTables / CreateTable / wait ACTIVE | OK | ✅ estado de negocio |
| dynamodb | PutItem / GetItem | OK | ✅ |
| dynamodb | UpdateItem + ConditionExpression (decremento atómico) | OK | ✅ primitiva anti-oversell de la reserva |
| dynamodb | ConditionExpression NO cumple → `ConditionalCheckFailedException` | OK | ✅ rechazo explícito de reserva/agotado |
| sqs | ListQueues / CreateQueue **FIFO + ContentBasedDeduplication** | OK | ✅ cola de comandos de la saga |
| sqs | SendMessage con MessageGroupId + MessageDeduplicationId / Receive / Delete | OK | ✅ mensajería entre servicios |
| sqs | Deduplicación FIFO observada (re-envío con mismo dedup id no redelivers) | OK (observación) | ✅ capa extra de idempotencia de comandos |
| sns | ListTopics / CreateTopic FIFO | OK | — |
| sns | Subscribe (protocolo sqs, **endpoint = queue ARN**; con URL falla `InvalidParameterException`) | OK | — |
| sns | Publish FIFO | OK (acepta) | — |
| sns | **Fanout SNS→SQS: entrega real del mensaje** | **FAIL** (FIFO y estándar, recursos frescos, polling 10×1s) | ❌ NO usable en MVP → fallback spec: notificación = evento interno + log; SNS pasa a V1 |
| s3 | ListBuckets / PutBucket / PutObject / GetObject | OK | ✅ artefacto de entrada (payload QR referenciable) |
| cache | ElastiCache/Redis data plane | N/A — el emulador solo expone 9297 HTTPS; no hay protocolo Redis | ❌ → cache in-app con invalidación (AC exige X-Cache HIT/MISS observable, no un servicio) |

Salida textual completa: `2026-11-13-emulator-probe-output.txt` (19/20 OK, 1 FAIL = fanout SNS, re-verificado aparte 2 veces con recursos únicos: FIFO y estándar).

## 4. Hallazgos críticos para .NET (SDK v4) — afectan el plan

1. **RegionEndpoint + ServiceURL juntos = desastre silencioso**: en SDK v4 gana RegionEndpoint y el cliente resuelve el endpoint REAL de AWS (`dynamodb.us-east-1.amazonaws.com`, cadena Starfield). La conexión al emulador falla por TLS… y sin pin de CA habría firmado contra AWS real. **Fix verificado**: `ServiceURL = https://127.0.0.1:9297` + `AuthenticationRegion = "us-east-1"` (sin RegionEndpoint).
2. **Confianza TLS desde .NET en Windows**: `SSL_CERT_FILE` (lo que sugieren las docs del emulador para .NET) NO funciona en Windows/.NET 10; instalar la CA en el store del usuario requiere diálogo interactivo (bloqueado en sesión no interactiva). **Fix verificado**: `HttpClientFactory` custom heredando `Amazon.Runtime.HttpClientFactory` con pin de thumbprint del root servido. Beneficio extra: **el pin hace imposible llamar a AWS real por accidente** (gate de división cumplido por diseño).
3. Renombres de paquetes/APIs en v4: `AWSSDK.SNS` no existe → `AWSSDK.SimpleNotificationService`; `ListQueuesAsync`/`ListTopicsAsync` piden request; `FindTopicAsync` devuelve `Topic`; `GetQueueAttributesResponse.Attributes["QueueArn"]`.
4. SNS `Subscribe` exige **ARN de cola** como endpoint (la URL da `Invalid parameter: Endpoint`).
5. Fidelidad: DynamoDB respeta la ventana DELETING (hay que pollear `ResourceNotFoundException` antes de recrear tabla homónima).

## 5. Anti-bloqueo: los 3 intentos registrados (bloqueo TLS desde SDK)

1. `ClientConfig.HttpClientFactory` + `chain.Build` con `CustomRootTrust` → dentro del SDK daba `PartialChain` (fuera del SDK, el mismo código pasaba).
2. Variante `RevocationMode.NoCheck` → idéntico `PartialChain`.
3. Pin directo por thumbprint → "remote certificate was rejected" → llevó al diagnóstico definitivo: el callback veía la **cadena REAL de AWS** (el SDK nunca hablaba con el emulador).
   - Resolución (fuera de la familia de intentos, con diagnóstico nuevo): quitar `RegionEndpoint`, usar `AuthenticationRegion` → **20/20 operaciones de servicio OK**.
   - Registrado en `factory/learnings.md` (L-001).

## 6. Reproducibilidad

```powershell
docker build -t aws-emulator:verified .\aws-emulator
docker run -d --name aws-emulator -p 9297:9297 aws-emulator:verified
docker exec aws-emulator cat /home/emulator/.aws/emulator/9297/emulator_credentials   # credenciales de esta corrida
docker cp aws-emulator:/home/emulator/.aws/emulator/9297/ca.pem <path-local>
curl.exe -s --cacert <path-local>\ca.pem https://localhost:9297/__admin/health        # -> ok
# probe .NET: ver 2026-11-13-emulator-probe-Program.cs (env: EMU_AWS_ACCESS_KEY_ID, EMU_AWS_SECRET_ACCESS_KEY, EMU_CA_PATH)
```
