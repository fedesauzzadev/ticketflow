# TicketFlow

[![CI](https://github.com/fedesauzzadev/ticketflow/actions/workflows/ci.yml/badge.svg)](https://github.com/fedesauzzadev/ticketflow/actions/workflows/ci.yml)

> **Caso de estudio (portfolio):** **https://fedesauzzadev.github.io/ticketflow/** ([`docs/portfolio.html`](docs/portfolio.html)) — arquitectura, demo guiada con una corrida real (Terraform → Kubernetes → compra → Lambda), decisiones, bugs encontrados, calidad y límites. Se abre en cualquier navegador; salida cruda en [`docs/portfolio-run.txt`](docs/portfolio-run.txt).

**Backend de venta de entradas para eventos de alta demanda** (flash-sale ticketing): 4 microservicios .NET 10 orquestados con saga y compensación, cache observable, idempotencia observable — **100% sobre servicios AWS corriendo en un emulador oficial en local** (`https://127.0.0.1:9297`). Cero nube real, cero costos, cero secrets en el repo.

> Un evaluador clona, corre un comando, y ve con curl: catálogo con cache (`X-Cache: HIT|MISS`), una compra que atraviesa **reserva → pago → emisión**, el rechazo que compensa, y la repetición que no duplica efectos.

**Decisión de dominio:** venta de entradas de alta demanda — razonada en el ADR [D-0002](brain/decisions/2026-11-13-D0002-dominio-ticketflow.md) (el resto de las decisiones: `brain/decisions/`).

## Arquitectura

```mermaid
flowchart LR
    E[Evaluador<br/>curl / scripts] -->|GET /events| CAT[Catálogo :5080<br/>cache-aside + X-Cache]
    E -.->|1. login| COG[Cognito]
    E -->|POST /purchases<br/>Bearer token + Idempotency-Key| ORD[Órdenes :5081<br/>process manager]
    E -->|GET /purchases/{id}| ORD
    E -->|GET /tickets/{id}| TIC[Ticketing :5083]

    ORD -->|reserve-seats / release-seats| Q1[catalog-commands.fifo]
    ORD -->|authorize-payment| Q2[payments-commands.fifo]
    ORD -->|issue-tickets| Q3[ticketing-commands.fifo]
    CAT -->|seats-reserved| QE[orders-events.fifo]
    PAY -->|payment-approved / declined| QE
    TIC -->|tickets-issued| QE
    QE --> ORD

    subgraph EMU[Emulador AWS :9297 — HTTPS + SigV4, CA pineada]
      DDB[(DynamoDB)]
      COG
      SSM[Parameter Store]
      SQS[SQS FIFO]
      S3[(S3)]
    end

    CAT -.-> DDB
    ORD -.-> DDB
    PAY[Pagos :5082] -.-> DDB
    TIC -.-> DDB
    Q1 & Q2 & Q3 & QE -.-> SQS
    TIC -.ticket QR.-> S3
    ORD -.config al arrancar.-> SSM
    ORD -.claves públicas.-> COG
```

Detalle completo (servicios, tablas, saga paso a paso, ADRs): [docs/architecture.md](docs/architecture.md).

## Quickstart

**Prerrequisitos:** Docker Desktop · SDK de **.NET 10** · Windows con PowerShell 5.1+ (o PS 7).

**Un comando** (desde la raíz del repo):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
```

Alternativa con compose para el emulador: `docker compose up -d` primero (equivalente al paso 1, con `restart: unless-stopped`); `run-local.ps1` detecta el contenedor por su nombre y sigue desde el paso 2. Mapa visual de la integración AWS: `docs/aws-architecture.html` · corridas de demo guardadas: `docs/demo-runs.md` · guía visual del producto: `docs/demo-walkthrough.html`.

Eso: (1) levanta el contenedor `aws-emulator` si está caído (lo buildea si no existe), (2) extrae las credenciales y la CA **de esa corrida** del emulador (se regeneran en cada arranque — nada hardcodeado; caen en `.secrets/`, que está gitignored), (3) buildea la solución (0 warnings) y (4) levanta los 4 servicios:

| Servicio | URL | Prueba |
|---|---|---|
| Catálogo | http://localhost:5080 | `GET /events` |
| Órdenes | http://localhost:5081 | `GET /health` |
| Pagos | http://localhost:5082 | `GET /health` |
| Ticketing | http://localhost:5083 | `GET /health` |

**Alternativa: los 4 servicios en contenedores** (mismos puertos y rutas; no correr ambos a la vez):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-docker.ps1         # build de las 4 imágenes + up
docker compose logs -f orders                                                      # logs de un servicio
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-docker.ps1 -Down   # baja los 4 (el emulador queda)
```

Un solo [`Dockerfile`](Dockerfile) multi-stage sirve a los 4 (`--build-arg SERVICE=Orders`): el SDK compila y la imagen final lleva solo el runtime, corre como usuario no root y no contiene secretos — las credenciales del emulador entran por variables de entorno y la CA se monta como volumen de solo lectura. Como dentro de un contenedor el emulador ya no es `localhost`, compose activa `AWS_EMULATOR_ALLOWED_HOST` (opt-in de un único host; el pin de la CA sigue impidiendo hablar con AWS real, D-0023).

**Alternativa: los 4 servicios en Kubernetes** (cluster local [kind](https://kind.sigs.k8s.io/); mismos puertos, tampoco a la vez que los otros dos):

```powershell
winget install Kubernetes.kind Kubernetes.kubectl                                  # una vez
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-k8s.ps1            # cluster + imágenes + deploy
kubectl --context kind-ticketflow -n ticketflow get pods                           # 4 pods Running
kubectl --context kind-ticketflow -n ticketflow delete pod -l app=payments         # self-healing: el Deployment lo recrea
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-k8s.ps1 -Down      # borra el cluster
```

[`infra/k8s/ticketflow.yaml`](infra/k8s/ticketflow.yaml) tiene, por servicio, un **Deployment** (imagen, probes de readiness y liveness, requests/limits) y un **Service** (nombre DNS estable: Órdenes llama a `http://catalog:5080`). Las credenciales del emulador van en un **Secret** y la CA en un **ConfigMap** que el script crea desde el entorno; el YAML versionado no contiene ningún valor. Son manifiestos estándar: en EKS real cambiarían las imágenes (a ECR) y el tipo de Service. Los pods corren en kind y no en el EKS del emulador porque ese cluster no ejecuta contenedores (ver sección EKS).

Cuando veas `==> Todo listo:` el flujo completo está operativo. `Ctrl+C` detiene los 4 servicios (el emulador queda; su estado persiste mientras no reinicies el contenedor).

## Recursos AWS del emulador: dónde se declaran (IaC)

Los recursos que los servicios consumen — **6 tablas DynamoDB** (key schema `pk` HASH), **8 colas SQS FIFO** (4 de la saga + sus 4 DLQs, con `ContentBasedDeduplication`) y el **bucket S3 `ticketflow-tickets`** de los QR — están declarados como datos en un manifiesto versionado: [`infra/aws-local.json`](infra/aws-local.json). Un guard test (`tools/TicketFlow.Provision.Tests`) clava el manifiesto contra las constantes de código: tocar uno sin el otro rompe la suite.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\provision.ps1          # --apply: crea lo que falta (idempotente)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\provision.ps1 -Verify  # lista los recursos reales y los compara 1:1 con el manifiesto
```

- **From scratch** (emulador recién recreado, cero recursos): `docker rm -f aws-emulator` → `scripts\provision.ps1` (recrea el contenedor y crea los 15 recursos de datos, el user pool, su cliente y los parámetros) → `-Verify` coincide 1:1. Aplicar 2 veces más deja el mismo conjunto (exit 0, sin duplicados).
- `-Verify` sale **exit 1** con el diff si falta algún recurso o difiere en su forma (key schema, FIFO/CDB, billing); los recursos **extra** (p. ej. colas `it-*` de corridas de tests) son solo WARNING.
- Los servicios **conservan su ensure defensivo de arranque** (decisión D-0019): `run-local.ps1` funciona sin provision previo — el manifiesto es la fuente de verdad de la lista, el ensure es resiliencia de runtime.
- La tool (`tools/TicketFlow.Provision`) reusa la plomería verificada de `Shared.Infrastructure` (CA pineada, SigV4, ensure tolerante a carreras).

### Lo mismo con Terraform

[`infra/terraform/main.tf`](infra/terraform/main.tf) crea esos mismos 15 recursos (y, en [`auth.tf`](infra/terraform/auth.tf), Cognito y Parameter Store) con el provider oficial `hashicorp/aws` apuntado al emulador. No repite la lista: hace `for_each` sobre `jsondecode(file("../aws-local.json"))`, así que el manifiesto sigue siendo la única fuente de verdad (D-0022).

```powershell
winget install Hashicorp.Terraform                                                              # una vez
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 apply                  # crea todos los recursos
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 plan -detailed-exitcode # exit 0 = sin drift
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\provision.ps1 -Verify                # verificador independiente: todo OK
```

Qué mirar en el HCL: `endpoints {}` (cada servicio redirigido al emulador), los `skip_*` (sin ellos el provider consulta STS/IMDS reales al arrancar), `s3_use_path_style` y credenciales/CA por variables de entorno estándar (`AWS_ACCESS_KEY_ID`, `AWS_CA_BUNDLE`), nunca en el repo.

### Lambda: notificación al emitirse una entrada

Cuando Ticketing guarda el QR en `s3://ticketflow-tickets/qr/{compra}/{entrada}.json`, S3 dispara la Lambda [`ticketflow-ticket-notifier`](infra/lambda/ticket_notifier/handler.py) (Python 3.12), que deja la notificación como una línea JSON en CloudWatch Logs. Ningún servicio .NET la invoca: es un trigger por evento. Todo está en [`infra/terraform/lambda.tf`](infra/terraform/lambda.tf), que el mismo `terraform.ps1 apply` crea.

```powershell
python -m unittest discover infra/lambda/ticket_notifier                                # tests del handler
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lambda-logs.ps1             # todas las notificaciones (requiere aws-cli)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lambda-logs.ps1 pur-...     # las de una compra
```

Qué mirar en `lambda.tf`, en orden: el zip (`archive_file` + `source_code_hash`: cambiar el código redespliega), el rol con su *trust policy* (quién lo asume) y su *permission policy* (qué puede hacer: solo escribir sus logs), el `aws_lambda_permission` (quién puede invocar: S3, y solo ese bucket) y la notificación del bucket con filtro `qr/*.json`. El emulador valida IAM: un trigger SQS→Lambda con un rol sin `sqs:ReceiveMessage` es rechazado igual que en AWS real.

### EKS: el cluster como código (solo control plane)

[`infra/terraform/eks.tf`](infra/terraform/eks.tf) declara lo que un cluster EKS necesita antes de correr un solo pod: una VPC con 2 subnets en zonas distintas (EKS lo exige), el rol IAM del **cluster** (lo asume `eks.amazonaws.com`) y el de los **nodos** (lo asume `ec2.amazonaws.com`, con las 3 managed policies de worker, CNI y lectura de ECR), el cluster `ticketflow` y un managed node group con autoscaling 1–3.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 apply
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\terraform.ps1 output eks   # cluster, versión, endpoint, estado
```

Alcance real: el emulador deja cluster y node group en `ACTIVE` y hasta sirve una API de Kubernetes emulada con los nodos registrados, pero en un puerto interno del contenedor y **sin ejecutar contenedores** (un pod de prueba termina en `Failed`). Sirve para aprender y mostrar la parte AWS de EKS (red, IAM, node groups, Terraform); los servicios siguen corriendo como procesos locales.

Límite: Terraform solo maneja lo que está en su estado. Si los servicios ya crearon los recursos con su ensure de arranque, `apply` falla por "ya existe" — usalo sobre un emulador recién recreado, **antes** de `run-local.ps1`. Con Cognito, además, ese `apply` fallido puede dejar un segundo user pool con el mismo nombre: recreá el emulador (`docker rm -f aws-emulator`) y volvé a aplicar. El estado (`terraform.tfstate`) es local y está gitignored.

## Parameter Store y Cognito (D-0024)

**DynamoDB** ya era la base de datos del proyecto (las 6 tablas de arriba). Se suman dos servicios:

| Servicio | Qué es | Para qué lo usa TicketFlow |
|---|---|---|
| **Systems Manager Parameter Store** | Un diccionario `nombre → valor` administrado, con jerarquía de nombres, versiones y permisos | Órdenes lee al arrancar `/ticketflow/auth/user-pool-id`, `/ticketflow/auth/client-id` y `/ticketflow/orders/hold-ttl-seconds` |
| **Cognito (user pools)** | Un servicio de usuarios: registra, guarda contraseñas, inicia sesión y entrega tokens firmados (JWT) | Comprar, consultar y pagar una compra exige un *access token*; cada usuario ve solo lo suyo |

```
1. cliente ──usuario+contraseña──▶ Cognito ──access token (JWT, 1 h)──▶ cliente
2. cliente ──Authorization: Bearer <token>──▶ Órdenes
3. Órdenes verifica la firma con las claves PÚBLICAS del pool (JWKS); no le pregunta nada por cada pedido
```

| Pedido a Órdenes | Respuesta |
|---|---|
| Sin token, token alterado, vencido, o un *ID token* | `401` |
| Token válido, compra de otro usuario | `404` (igual que si no existiera) |
| La misma `Idempotency-Key` usada por dos usuarios | Dos compras distintas |

Catálogo (`GET /events`), Pagos, Ticketing y `/admin/*` **siguen sin autenticación** (ver límites).

**Probarlo a mano**, con los servicios arriba, en una ventana de PowerShell en la raíz del repo:

```powershell
Set-ExecutionPolicy -Scope Process Bypass                    # permite dot-sourcear scripts en esta ventana
. .\scripts\auth.ps1                                          # define Get-DemoToken y carga las credenciales del emulador
$env:AWS_ACCESS_KEY_ID = $env:AWS_EMULATOR_ACCESS_KEY_ID; $env:AWS_SECRET_ACCESS_KEY = $env:AWS_EMULATOR_SECRET_ACCESS_KEY; $env:AWS_CA_BUNDLE = $env:AWS_EMULATOR_CA_PATH; $env:AWS_PAGER = ''  # sin paginador
$aws = '--endpoint-url', $env:AWS_EMULATOR_ENDPOINT, '--region', 'us-east-1'

aws @aws ssm get-parameters-by-path --path /ticketflow --recursive --query "Parameters[].[Name,Value]" --output table
aws @aws cognito-idp list-user-pools --max-results 10
$auth = @{ Authorization = "Bearer $(Get-DemoToken 'ana')" }  # crea a ana si falta e inicia sesión

# Cambiar un parámetro: Órdenes lo lee al ARRANCAR, así que hay que reiniciar los servicios.
aws @aws ssm put-parameter --name /ticketflow/orders/hold-ttl-seconds --value 30 --type String --overwrite
```

`Get-DemoToken` usa `TicketFlow.Provision --token`: crea el usuario con una contraseña aleatoria que no se guarda y llama a `InitiateAuth`, como haría un cliente real. Es solo para el emulador (usa APIs de administrador). La variable `HOLD_TTL_SECONDS`, si está definida, gana sobre el parámetro.

Código para leer, en orden: [`auth.tf`](infra/terraform/auth.tf) → [`CognitoSetup.cs`](src/Shared/TicketFlow.Shared.Infrastructure/CognitoSetup.cs) → [`ParameterStore.cs`](src/Shared/TicketFlow.Shared.Infrastructure/ParameterStore.cs) → [`CognitoJwtAuth.cs`](src/Services/Orders/TicketFlow.Orders.Api/CognitoJwtAuth.cs) → [`AuthApiTests.cs`](tests/TicketFlow.Orders.Tests/AuthApiTests.cs).

## CI local: todo el gate en un comando (H-08)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ci.ps1          # preflight -> build 0 warnings -> suite completa -> cobertura >=70% Core -> infra-verify -> workflow-lint
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ci.ps1 -Demos  # ademas corre las 4 demos (cache, idempotency, resilience y ttl — levanta los servicios si no estan y cierra SOLO lo que levanto)
```

La **primera etapa que falla es la culpable** del `exit != 0`: el pipeline se corta ahí y el
resumen final lista cada etapa con veredicto y duración. `preflight` deja el emulador arriba
(lo arranca si hace falta, misma plomería del quickstart) e inyecta creds/CA al env, con lo
que la suite corre completa (integración incluida) y el **umbral de cobertura ≥70% de los
Core** queda activo (cableado a los csproj): fallar el umbral falla la etapa `tests`.
**Corré el CI con los servicios abajo**: los e2e de saga compiten por las colas compartidas
si hay servicios vivos en :5080..5083 (el propio `-Demos` los levanta recién después de la
etapa `tests` y los cierra al terminar — solo lo que él levantó).

### El workflow de GitHub Actions (CI real, cero deriva)

`.github/workflows/ci.yml` corre el gate **completo** en `ubuntu-latest` en cada push a
`master`: invoca **el mismo `scripts/ci.ps1`** que corre el dueño localmente (cero
deriva). El `preflight` buildea el emulador AWS con el Docker del propio job, extrae
creds/CA de esa corrida, y la suite completa (213 tests + umbral de cobertura ≥70%)
corre contra ese emulador — build 0 warnings → tests → infra-verify → workflow-lint,
primera etapa que falla = culpable. La validación local del YAML es **estructural**
(`scripts\validate-workflow.ps1`, etapa `workflow-lint`): PS 5.1 no
tiene parser YAML — se chequean las invariantes (`on:`, `jobs:`, `runs-on:`, checkout,
`setup-dotnet` 10.x, invocación de `ci.ps1`, indentación sin tabs).

## Probar el flujo a mano

Copia y pega en otra ventana de PowerShell. Cada bloque es independiente salvo los ids que generan los anteriores. Para comprar hace falta un token (ver [Parameter Store y Cognito](#parameter-store-y-cognito-d-0024)); primero, en la raíz del repo:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
. .\scripts\auth.ps1
$auth = @{ Authorization = "Bearer $(Get-DemoToken)" }       # dura 1 hora
```

**1) Catálogo y cache observable** — dos lecturas seguidas: la primera `MISS`, la segunda `HIT` con el mismo valor:

```powershell
Invoke-RestMethod http://localhost:5080/events   # lista de eventos (seed: evt-mj-tribute-night, evt-legacy-tour)

$a = Invoke-WebRequest -UseBasicParsing http://localhost:5080/events/evt-mj-tribute-night/availability
"$($a.Headers['X-Cache']) $($a.Content)"          # MISS {"event_id":...,"available":500}
$a = Invoke-WebRequest -UseBasicParsing http://localhost:5080/events/evt-mj-tribute-night/availability
"$($a.Headers['X-Cache']) $($a.Content)"          # HIT  (mismo valor)
```

**2) Compra aprobada** (PAN que NO termina en `0002` ⇒ aprobada). La saga corre async: `PENDING → SEATS_RESERVED → PAYMENT_APPROVED → TICKETS_ISSUED`:

```powershell
$key  = 'demo-' + [guid]::NewGuid().ToString('N')
$body = @{ event_id = 'evt-mj-tribute-night'; seats = 1
           card = @{ pan = '4111111111111111'; holder = 'Evaluador'; expiry = '12/29'; cvv = '123' } } | ConvertTo-Json -Depth 4
$r = Invoke-WebRequest -UseBasicParsing http://localhost:5081/purchases -Method Post `
     -ContentType 'application/json' -Headers ($auth + @{ 'Idempotency-Key' = $key }) -Body $body
$r.StatusCode; $r.Content                                 # 201 {"purchase_id":"pur-...","status":"PENDING",...}
$compra = ($r.Content | ConvertFrom-Json).purchase_id

do { $p = Invoke-RestMethod -Headers $auth http://localhost:5081/purchases/$compra; Start-Sleep -Milliseconds 300 }
while ($p.status -notin 'TICKETS_ISSUED','FAILED')
$p                                                        # TICKETS_ISSUED + ticket_ids
Invoke-RestMethod http://localhost:5082/payments/$compra     # evidencia del pago: APPROVED, amount
Invoke-RestMethod http://localhost:5083/tickets/$($p.ticket_ids[0])   # entrada + qr_s3_key (S3)
```

**3) Compra rechazada ⇒ compensación** (PAN terminada en `0002` ⇒ `declined`; el asiento se libera y la disponibilidad vuelve a su valor previo):

```powershell
$body = @{ event_id = 'evt-legacy-tour'; seats = 1
           card = @{ pan = '4000000000000002'; holder = 'Evaluador'; expiry = '12/29'; cvv = '123' } } | ConvertTo-Json -Depth 4
$antes = (Invoke-RestMethod http://localhost:5080/events/evt-legacy-tour/availability).available
$r = Invoke-WebRequest -UseBasicParsing http://localhost:5081/purchases -Method Post `
     -ContentType 'application/json' -Headers ($auth + @{ 'Idempotency-Key' = 'demo-' + [guid]::NewGuid().ToString('N') }) -Body $body
$compra2 = ($r.Content | ConvertFrom-Json).purchase_id
do { $p2 = Invoke-RestMethod -Headers $auth http://localhost:5081/purchases/$compra2; Start-Sleep -Milliseconds 300 }
while ($p2.status -notin 'TICKETS_ISSUED','FAILED')
$p2                                                       # FAILED + reason "pago rechazado por el emisor..."
(Invoke-RestMethod http://localhost:5080/events/evt-legacy-tour/availability).available -eq $antes   # True (restaurada)
```

## Scripts de demo (evidencia imprimible)

> Guía visual con la corrida real anotada paso a paso: **docs/demo-walkthrough.html** (se abre en cualquier navegador).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-cache.ps1          # MISS -> HIT -> compra -> MISS con valores
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-idempotency.ps1    # misma key => misma compra; 422s
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-resilience.ps1     # AC1..AC5: backoff, DLQ, breaker, sin drift
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\demo-ttl.ps1            # hold: retencion -> vencimiento -> liberacion -> 409 -> re-compra
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stress-test.ps1 -N 20  # N compradores simultaneos, invariantes anti-oversell
```

Los 5 scripts terminan con exit 0 si la demo pasa (distinto de 0 si falla cualquier check). `demo-idempotency` demuestra también el caso **agotado** cuando existe un evento con ≤ 7 asientos: para verlo, levanta el entorno con un evento chico antes de correr el script:

```powershell
$env:SEED_EVENTS = 'evt-mj-tribute-night|Michael Jackson Tribute Night|2027-03-12T21:00:00Z|120.50|500;evt-demo-soldout|Demo Sold Out|2027-06-01T20:00:00Z|10.00|1'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
```

(formato `id|nombre|fecha|precio|disponibilidad`; el seed es condicional — no pisa eventos existentes)

## Resiliencia: inyectá fallos vos (H-06)

`demo-resilience.ps1` corre la secuencia completa (fallo transitorio con backoff visible,
fallo persistente con DLQ + compra terminal, consistencia post-recuperación, `docker pause`
del emulador con circuit breaker fail-fast y cierre, invariante de disponibilidad). Para
inyectar a mano, cada servicio (:5080 Catálogo · :5081 Órdenes · :5082 Pagos · :5083
Ticketing) expone:

```powershell
# activar fallo por paso de saga (transient: falla N intentos; persistent: hasta borrar; step "*" = todos)
curl -X POST http://localhost:5082/admin/failure -H "Content-Type: application/json" -d '{\"step\":\"authorize-payment\",\"mode\":\"transient\",\"count\":2}'
curl http://localhost:5082/admin/failure                    # reglas activas
curl -X DELETE http://localhost:5082/admin/failure/authorize-payment   # recuperar

# cola de mensajes muertos del worker del servicio (peek sin borrar)
curl http://localhost:5082/admin/dlq                        # [{\"type\":\"authorize-payment\",\"purchase_id\":\"pur-...\",...}]
```

Pasos inyectables: `reserve-seats` / `release-seats` (Catálogo) · `authorize-payment`
(Pagos) · `issue-tickets` (Ticketing) · cualquier evento (Órdenes). Reintentos: 3 intentos
por entrega con backoff 0,5/1 s; agotados → `<cola>-dlq.fifo` + compra `FAILED` con motivo
(compensada si había reserva; si la compensación misma se agota, la compra igual es
terminal `FAILED` con el **asiento huérfano declarado en el motivo** y la entrega esperando
en la DLQ del Catálogo). El "downstream colgado" del breaker es `docker pause
aws-emulator` (3 fallos consecutivos → fail-fast 5 s → probe de recuperación).

## Hold de asientos con TTL (H-09): el carrito abandonado

Una compra puede **retener asientos sin pagar de inmediato** y completar el pago después.
Sin `"hold": true` el comportamiento es exactamente el de V1 (regresión prohibida).

```powershell
# 1) Crear la compra en hold: reserva 1 asiento y NO emite el pago automáticamente.
#    GET /purchases/{id} muestra SEATS_RESERVED con "expires_at" (ISO-8601) y la
#    disponibilidad queda decrementada hasta que el hold venga o se pague.
$key = 'mi-key-' + [Guid]::NewGuid().ToString('N')
$body = '{"event_id":"evt-mj-tribute-night","seats":1,"hold":true,"card":{"pan":"4111111111111111","holder":"Michael Fan","expiry":"12/29","cvv":"123"}}'
curl.exe -X POST http://localhost:5081/purchases -H "Authorization: $($auth.Authorization)" -H "Idempotency-Key: $key" -H "Content-Type: application/json" -d $body

# 2) Completar el pago ANTES del TTL: 202 y la saga sigue hasta TICKETS_ISSUED como en V1.
curl.exe -X POST http://localhost:5081/purchases/<purchase_id>/payment -H "Authorization: $($auth.Authorization)"      # 202 {purchase_id, status, expires_at}

# 3) Si el TTL vence sin pago: un sweeper de Ordenes (cada 5 s) expira la compra a
#    EXPIRED (terminal, con motivo "reserva expirada sin pago (TTL Ns)") y libera el
#    asiento: la disponibilidad vuelve al valor previo y el asiento es re-comprable.
#    El pago tardio recibe 409 hold-expired; el replay del POST original con la misma
#    key devuelve la MISMA compra EXPIRED sin efectos.
```

- **`POST /purchases/{id}/payment`** (sin body): `202` claim a tiempo (la saga sigue sola)
  · `200` replay del endpoint · `409 hold-expired` tardío · `409 invalid-state` (no-hold o
  estado avanzado) · `404` no existe · `400` id inválido.
- **Carrera pago-vs-vencimiento**: resuelta por CAS en DynamoDB (D-0017 §4) — el endpoint
  reclama `payment_initiated_at` con `expires_at > now`; el sweeper exige que nadie haya
  reclamado. Gana quien haga CAS primero, sin relojes distribuidos.
- **`HOLD_TTL_SECONDS`** (env de Órdenes, default **90 s**, válido 5..3600, fail-fast al
  arrancar): configura el TTL. La expiración vive en DynamoDB del emulador (sweeper
  stateless): **sobrevive el reinicio de los microservicios**.
- **Sin doble liberación**: expiración y compensación pueden emitir `release-seats`, pero la
  liberación idempotente del Catálogo (`decrement_pids`, D-0013/D-0017 §3) incrementa la
  disponibilidad UNA sola vez.
- Demo completa con valores (`demo-ttl.ps1`, arriba) y trazabilidad: cada expiración loguea
  el paso `hold-expired` con su `purchase_id` (visible en `trace-purchase.ps1`).

## Estrés concurrente sin oversell (H-07)

`stress-test.ps1` dispara N compradores **simultáneos** (POST con `Idempotency-Key` única, 1 asiento, PAN aprobada) sobre un evento con X asientos y verifica las invariantes con salida imprimible:

```powershell
# N y X por parámetro. X por defecto = disponibilidad del evento al iniciar; con -X se
# alinea pre-comprando (A-X) asientos (X > disponibilidad actual => exit 2, no se fabrican asientos).
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stress-test.ps1 -N 20 -X 5 -EventId evt-mj-tribute-night
```

- **Qué verifica (exit 0 = PASS)**: exactamente `min(N,X)` `TICKETS_ISSUED` y `N-min(N,X)` rechazos explícitos (422 `seats-unavailable` o `FAILED` con motivo) — cero oversell; disponibilidad **nunca negativa** (muestreo durante toda la corrida) y final = `X - emitidas`; 100% de las compras en estado terminal dentro del plazo (**`-DeadlineSeconds`, default 120 s** — la saga p95 es ~0,8 s); e invariante de cierre `disponibilidad_final + emitidos == X`.
- **Concurrencia real**: N `Task.Run` sobre un `HttpClient` compartido (`Add-Type`), sin el límite por defecto de 2 conexiones por host de .NET Framework.
- Evidencia de corridas (N=20/X=5, N=8/X=7, N<X): [brain/evidence](brain/evidence/2026-11-14-S07-n2003-verification.md).

## Traza distribuida de una compra (correlation-id)

Cada compra es su propio correlation-id (`purchase_id`, D-0011): nace en `POST /purchases`, viaja en el envelope de la saga por SQS y aparece en el log JSON de los 4 servicios. `run-local.ps1` redirige los logs a `.artifacts\logs\<svc>.log` (cada arranque los trunca), así que la traza de punta a punta se filtra post-hoc:

```powershell
# compra aprobada o compensada (PAN termina 0002): pasos en orden cronológico + guardián anti-PAN/CVV
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\trace-purchase.ps1 -PurchaseId pur-<32hex>

# comando crudo equivalente (sin script):
Select-String ".artifacts\logs\*.log" -Pattern 'pur-<32hex>' -SimpleMatch

# seguir los logs de un servicio en vivo mientras comprás:
Get-Content .artifacts\logs\orders.log -Wait
```

`trace-purchase.ps1` imprime la tabla `timestamp | servicio | nivel | mensaje` ordenada cronológicamente, un resumen por servicio y sale con código distinto de 0 si la traza contiene un PAN (13-19 dígitos) o un CVV.

## Mapa de patrones aplicados

| Patrón | Dónde vive |
|---|---|
| **Repository** (por agregado, puertos en Application) | Interfaces `src/Services/*/Core/Application/` (p.ej. `IEventRepository.cs`, `Orders` → `Ports.cs`) · impl DynamoDB en `src/Services/*/Core/Infrastructure/DynamoDb*.cs` |
| **Cache-aside + invalidación explícita** | `src/Services/Catalog/TicketFlow.Catalog.Core/Application/AvailabilityService.cs` (HybridCache, TTL 30 s) · header `X-Cache` y endpoint interno de invalidación en `Catalog.Api/Program.cs` · invalida el worker `CatalogSagaHandler.cs` al reservar/liberar |
| **Transactional outbox** | `src/Services/Orders/TicketFlow.Orders.Core/Infrastructure/DynamoDbOutbox.cs` (tabla `orders_outbox`) + relay publicador a SQS en `src/Services/Orders/TicketFlow.Orders.Api/Workers.cs` (`OutboxRelayWorker`) |
| **Process manager / saga orchestrator** | `src/Services/Orders/TicketFlow.Orders.Core/Application/SagaProcessManager.cs` + máquina de estados en `Domain/Purchase.cs` |
| **Idempotency-key store** (put condicional + hash de body) | `src/Services/Orders/TicketFlow.Orders.Core/Application/Idempotency.cs` + `Infrastructure/DynamoDbIdempotencyStore.cs` (tabla `orders_idempotency`) · claims por servicio en las tablas `*_idempotency` de cada uno |
| **Dedup handler** (comandos duplicados, dos capas) | Capa transporte: `src/Shared/TicketFlow.Shared.Infrastructure/Saga/SqsSagaTransport.cs` (SQS FIFO `ContentBasedDeduplication`, dedup id `{type}:{purchase_id}`) · capa aplicación: `CatalogSagaHandler.cs`, `PaymentsSagaHandler.cs`, `TicketingSagaHandler.cs` (log "duplicado detectado: efecto descartado") |
| **Adapter de infraestructura** (aísla el emulador) | `src/Shared/TicketFlow.Shared.Infrastructure/EmulatorAwsClients.cs` + `EmulatorAwsOptions.cs` (endpoint + CA pineada + timeouts; una llamada a AWS real es imposible por diseño) |
| **Mediator-equivalente** (dispatcher in-process) | Workers base de `Shared.Infrastructure/Saga/` → handlers registrados por DI en cada `*.Api/Program.cs` |

## Verificación

- **Tests:** `dotnet test TicketFlow.sln` → **213/213** (unit + integración contra el emulador real; requiere el emulador arriba y `AWS_EMULATOR_*` en el env — `run-local.ps1` muestra cómo se extraen; el run reporta cobertura de los 4 `Core` y los 4 `.Api`, umbral ≥70% — la tool Provision con umbral propio 86%).
- **Evidencias por slice** (comando + salida archivada): [S01 catálogo/cache](brain/evidence/2026-11-13-S01-n1001-verification.md) · [S02 saga+compensación](brain/evidence/2026-11-13-S02-n1002-verification.md) · [S03 idempotencia](brain/evidence/2026-11-14-S03-n1003-verification.md) · [S04 quickstart/demos/mediciones (este slice)](brain/evidence/2026-11-14-S04-n1004-verification.md) · [verificación del emulador](brain/evidence/2026-11-13-emulator-verification.md).

### Performance medida (NFR-5, local, 50 iteraciones tras warmup)

| Operación | p50 | p95 | Objetivo | Veredicto |
|---|---|---|---|---|
| `GET /events/{id}/availability` (cache HIT) | 0,6 ms | **1,4 ms** | < 50 ms | ✅ |
| `POST /purchases` (201 inmediato) | 9,3 ms | **20,7 ms** | < 200 ms | ✅ |
| Saga completa `POST → TICKETS_ISSUED` | 667 ms | **790 ms** | < 2 s | ✅ |

Números crudos y método: [evidencia S04](brain/evidence/2026-11-14-S04-n1004-verification.md).

## Limitaciones conocidas (fidelidad del emulador)

- **Autenticación solo en Órdenes** (D-0024): `GET /payments/{id}`, `GET /tickets/{id}`, `/admin/*` y los endpoints de Catálogo siguen abiertos para quien alcance el puerto. Los servicios escuchan en `localhost` salvo en contenedores.
- **Sin pantalla de registro ni MFA**: los usuarios de demo los crea la herramienta con APIs de administrador.
- **Parámetros leídos al arranque**: cambiar uno requiere reiniciar Órdenes.
- **El estado del emulador vive en memoria del contenedor**: reiniciar los microservicios NO pierde datos; reiniciar el **contenedor** sí (todo se re-seedea al arrancar). No reinicies el contenedor en medio de una demo.
- **SNS sin entrega** (control plane OK, data plane no): la notificación del ticket es un **evento interno + log visible** en Ticketing (gap verificado: [evidencia](brain/evidence/2026-11-13-emulator-verification.md)); SNS queda para V1.
- **Sin Redis real** (el emulador no expone protocolo Redis): la cache es **in-app** (`HybridCache`) con TTL 30 s + invalidación explícita — el header `X-Cache: HIT|MISS` es observable igual que en producción.
- Un mensaje SQS recibido y no borrado demora ~30 s (visibility timeout FIFO) en re-procesarse; los handlers son idempotentes y lo toleran. Backoff/DLQ: V1.
- **No correr la suite de tests con los servicios levantados**: comparten colas SQS y se roban mensajes (ver [L-008](factory/learnings.md)).
