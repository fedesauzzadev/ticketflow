# Learnings — fábrica TicketFlow

Registro de bloqueos resueltos (causa → fix → lección). Anti-bloqueo: 3 intentos con enfoques distintos → escalar a TL y decidir workaround que preserve AC.

## L-001 — TLS "roto" contra el emulador desde AWS SDK .NET v4 (2026-11-13, TL Fase 2)

**Síntoma:** todos los calls del SDK fallaban con `The SSL connection could not be established` aunque `curl --cacert` y un `HttpClient` crudo con la CA pineada funcionaban perfecto.

**3 intentos:**
1. `ClientConfig.HttpClientFactory` + `X509Chain CustomRootTrust` → `PartialChain` solo dentro del SDK.
2. + `RevocationMode.NoCheck` → idéntico.
3. Pin por thumbprint del root → "remote certificate was rejected" (callback SÍ corría) → habilitó el diagnóstico.

**Causa raíz:** en SDK v4, si se setean `RegionEndpoint` Y `ServiceURL` a la vez, **gana RegionEndpoint**: el cliente apuntaba al endpoint REAL de AWS (`CN=dynamodb.us-east-1.amazonaws.com`, cadena Amazon/Starfield) y el pin de CA correctamente lo rechazaba. El error de TLS era un síntoma; jamás estábamos hablando con el emulador.

**Fix verificado:** `ServiceURL = "https://127.0.0.1:9297"` + `AuthenticationRegion = "us-east-1"`, SIN `RegionEndpoint`. Resultado: 19/20 operaciones OK (el 20° es el gap de SNS fanout, ver L-002).

**Lección:** (a) un pin de CA del emulador no es solo cosmético: convierte una mala config silenciosa (llamar a AWS real) en un error ruidoso — se adopta como patrón del proyecto; (b) cuando "el mismo código pasa afuera y falla adentro del SDK", dumpear lo que el callback VE (sujeto/issuer/thumbprints) antes de tocar una línea más de validación.

## L-002 — SNS→SQS fanout no entrega en el emulador (2026-11-13, TL Fase 2)

**Síntoma:** `Subscribe` y `Publish` devuelven OK pero el mensaje nunca llega a la cola suscrita.

**Verificado:** 2 corridas con recursos únicos (topic/cola FIFO y también estándar), polling 10×1s, dedup ids distintos → 0 mensajes entregados. Es un gap de fidelidad del emulador (control plane sí, data plane de entrega no).

**Decisión:** fallback ya definido en spec.md — notificación del MVP = evento interno + log visible; SNS pasa a V1. La mensajería de la saga usa SQS FIFO directo (verificado 4/4). Ver ADR D-0006.

**Lección:** "la API acepta el request" ≠ "el efecto ocurrió": verificar los efectos (mensaje entregado), no los status 200.

## L-003 — `pwsh` no existe en la máquina; SDK 10 crea `.slnx` por defecto (2026-11-13, Dev S01)

**Síntoma:** `pwsh -File scripts/run-local.ps1` → CommandNotFoundException. Además `dotnet new sln` genera `TicketFlow.slnx`, pero config.yaml/comandos estampados usan `TicketFlow.sln`.

**Fix verificado:** `run-local.ps1` escrito compatible PS 5.1+ (sin sintaxis PS7) y corrido con `powershell -NoProfile -ExecutionPolicy Bypass -File`. Solución recreada con `dotnet new sln --format sln`. PS 5.1 no interactivo: `Invoke-WebRequest` exige `-UseBasicParsing`; para evidencia HTTP se usa `curl.exe` directamente.

**Pendiente TL:** o instalar PowerShell 7, o actualizar `commands.run` de brain/config.yaml a `powershell` — mientras tanto documentar el equivalente (hecho en N-1001.verify).

## L-004 — Emulador DynamoDB: `GetItem` devuelve `Item = null` (no dict vacío) si la key no existe (2026-11-13, Dev S01)

**Síntoma:** NullReferenceException en el repositorio al buscar evento inexistente; en la API se manifestaba como 500 (la excepción no era la esperada `EventNotFoundException`).

**Causa raíz:** el emulador (a diferencia de DynamoDB real/SDK esperado) serializa "sin item" como atributo ausente → `response.Item` llega null, no `{}`.

**Fix verificado:** guard `response.Item is not { Count: > 0 }` antes de tocar el diccionario. 34/34 tests verdes (incluye 404 por evento inexistente vía HTTP).

**Lección:** asumir colecciones vacías vs null en la frontera de un emulador es un bug latente — validar la forma real de la respuesta en la primera corrida de integración, no fiarse del comportamiento del servicio real.

## L-005 — Emulador SQS: `ReceiveMessage` devuelve `Messages = null` (no lista vacía) si la cola está vacía (2026-11-13, Dev S02)

**Síntoma:** `NullReferenceException` en el worker de saga al evaluar `response.Messages.Count` con la cola vacía; en los hosts se manifestaba como error de receive en loop (y por reflections, 500 en tests de API que frenaban workers).

**Causa raíz:** mismo gap de fidelidad que L-004 pero en SQS: el emulador serializa "sin mensajes" como atributo ausente → `Messages` llega null, no `[]`.

**Fix verificado:** guard `response.Messages is not { Count: > 0 }` antes de tocar la colección (SqsSagaWorker + drains de tests). 88/88 verdes.

**Lección:** asumir la misma regla de L-004 para TODA colección de respuesta del emulador; normalizar el guard en el shared (SqsSagaWorker ya lo hace por diseño para los 3 servicios).

## L-006 — PS 5.1: `Start-Process -ArgumentList` no cita paths con espacios; `curl -o $null` rompe (2026-11-13, Dev S02)

**Síntoma:** `dotnet run --project C:\repos\MichaelJackson AWS\src\...` lanzado vía Start-Process con array → "La ruta de acceso al archivo proporcionada no existe: C:\repos\MichaelJackson." (el path se corta en el espacio). Además `curl.exe -o $null` pasa un argumento vacío → `curl: (2) no URL specified`.

**Fix verificado:** (a) ArgumentList como STRING único con comillas explícitas: `-ArgumentList ('run --project "{0}" --no-build' -f $path)`; (b) `-o NUL` (dispositivo nulo de Windows). run-local.ps1 levanta los 4 servicios y pasea readiness probes OK.

**Lección:** en PS 5.1, todo path con espacio que cruce un borde de proceso va citado a mano, y `$null` nunca como argumento de un exe (se convierte en string vacío). El repo tiene espacio en el nombre por diseño — patrón obligatorio para scripts futuros.

## L-007 — Tests concurrentes contra el emulador: carreras en create de tablas/colas → 500 crudos; y mensajes in-flight bloquean un drain caído ~30 s (2026-11-13, Dev S02)

**Síntoma:** primera corrida de la suite completa: fallos intermitentes con `HttpErrorResponseException` (500 crudo del emulador) en creates concurrentes de las mismas tablas/colas desde clases de test paralelas (y en runtime: los 4 servicios arrancan a la vez).

**Fix verificado:** ensure TOLERANTE A CARRERAS por diseño, no solo en tests: `DynamoDbTables.EnsureAsync` (catch ResourceInUse/AmazonServiceException + re-describe hasta ACTIVE, deadline 30 s), `SqsSagaTransport.EnsureQueueAsync` (reintentos Get/Create con catch amplio, 5 s), `S3QrPayloadStore.InitializeAsync` (idem bucket), y workers que reintentan el ensure en vez de morir. Suite 88/88 en 2 corridas consecutivas.

**Residuo conocido:** un mensaje recibido y no borrado (drain que crasheó) queda in-visible ~30 s (visibility timeout FIFO); si un consumidor muere a mitad, esa compra puede tardar ese window en avanzar. Aceptable para el MVP (los handlers son idempotentes); backoff/DLQ son H-06 (V1).

**Lección:** contra un emulador (o nube real), TODO ensure de recurso compartido se escribe como si dos instancias lo corrieran simultáneamente — porque en run-local y en tests así es.

## L-008 — Tests de integración contra estado persistente: keys fijas de idempotencia y colas compartidas rompen la re-corrida (2026-11-14, Dev S03)

**Síntoma:** la suite completa fallaba de forma DISTINTA en cada corrida (Orders 46/48, o Catalog flaky): `Expected: Created / Actual: OK` en `OrdersApiTests`, `Expected: Created / Actual: UnprocessableEntity` en el heal-test, y `CatalogSagaDedupTests` intermitente.

**Causas (3 bugs de tests, no de producción):**
1. `OrdersApiTests` usaba `Idempotency-Key` FIJA (`api-test-key-1`): el claim persiste en el DynamoDB del emulador (el contenedor lleva horas arriba) ⇒ la 2ª corrida recibe replay 200, no 201.
2. El test de heal computaba el hash del claim con un holder distinto al del body POSTeado ⇒ mismatch determinista 422 (`idempotency-key-reused`).
3. `CatalogSagaDedupTests` enviaba a la cola compartida `ticketflow-catalog-commands.fifo`: el `CatalogSagaWorker` del host de `CatalogApiTests` (clase paralela del mismo ensamblado, y colas compartidas con otros procesos de test) recibía y borraba sus mensajes ⇒ asserts de conteo flaky.

**Fix verificado:** (1) key única por corrida (`Guid`); (2) el objeto que se hashea debe espejar EXACTAMENTE el body que se envía (un solo origen de verdad); (3) cola FIFO dedicada por corrida (`it-...-<guid>.fifo`, misma creación/atributos vía `SqsSagaTransport`). Suite 96/96 en DOS corridas consecutivas contra el mismo emulador.

**Lección:** contra estado que PERSISTE entre corridas (emulador sin reset), todo identificador que la app reclama (idempotency keys) es dato por corrida en tests, no constante; y todo test que asserta CUÁNTOS mensajes entrega una cola debe ser el único consumidor de esa cola — si no, assertea el scheduling de otros procesos.

## L-009 — PS 5.1: `$ErrorActionPreference='Stop'` + stderr de nativos mata scripts que "manejan" exit codes (2026-11-14, Dev S04)

**Síntoma:** `run-local.ps1` corrido con el emulador DETENIDO (quickstart desde cero, H-04 AC1) moría al primer intento del loop de retry de `docker cp` con `NativeCommandError` ("Could not find the file …/ca.pem"), aunque el loop chequeaba `$LASTEXITCODE` y tenía 20 reintentos.

**2 intentos:** (1) ensanchar la ventana de retry 20→60 — falló idéntico (descartó lentitud del emulador en frío); (2) causa raíz: en PS 5.1, con EAP `Stop`, la PRIMERA línea de stderr de un comando nativo se convierte en error terminante ANTES de que el script evalúe nada — el loop de retry jamás reintentó. El camino frío nunca se había ejercitado (S01..S03 siempre tuvieron el emulador ya arriba y `docker cp` exitoso al primer tiro).

**Fix verificado:** zona `$ErrorActionPreference='Continue'` rodeando TODA la sección de nativos docker (ps/start/build/run/cp/exec), restaurada antes del `dotnet build`; ventana del `docker cp` a 60×1 s (el emulador regenera CA+creds al arrancar en frío). Quickstart en frío verde desde entonces (evidencia S04 §0-1).

**Lección:** en PS 5.1, "chequear `$LASTEXITCODE` con reintentos" NO basta para tolerar fallos de un nativo si EAP es `Stop`: el stderr del nativo es un error terminante. Todo bloque de script que hable con nativos que pueden fallar transitoriamente (docker hasta que arranque, curl hasta que responda) va dentro de una zona EAP `Continue`. Nota: `curl.exe -s` no emite stderr (por eso los probes de readiness siempre funcionaron).

## L-010 — Medir p95 con `Invoke-WebRequest` de PS 5.1 mide el CLIENTE, no el servidor (2026-11-14, Dev S04)

**Síntoma:** NFR-5 medido con `Invoke-WebRequest` en loop: GET availability (cache HIT) p95 = 331,9 ms y POST p95 = 1082,7 ms — FALLA contra objetivos de 50/200 ms, con spikes de 700+ ms en lecturas de cache.

**Causa raíz:** cada call de `Invoke-WebRequest` en PS 5.1 paga auto-detección de proxy y NO reutiliza la conexión HTTP — el overhead (y su varianza) domina a un endpoint que resuelve en <2 ms. Además, la primera versión del bench medía la saga con un stopwatch ya detenido (tiempos de saga == tiempos de POST: imposibles).

**Fix verificado:** cliente .NET `HttpClient` compartido (Add-Type System.Net.Http, conexión reutilizada) + warmup sin medir (10 GET + 1 saga completa) + timelines separados (Stopwatch propio para POST, otro para saga). Resultados reales (n=50): GET HIT p95 1,4 ms · POST p95 20,7 ms · saga p95 790,1 ms — los tres NFR-5 OK (evidencia S04 §4).

**Lección:** antes de declarar FALLA un NFR de performance, validar la fidelidad del cliente de medición: conexión reutilizada, sin proxy dance, warmup separado de la medición, y un sanity check (¿p50 coherente con el trabajo que hace el endpoint?). Un cliente ruidoso convierte un servidor 300× más rápido que el objetivo en un "fallo".

## L-011 — SDK .NET v4 con HttpClientFactory custom: ClientConfig.Timeout NO se impone, y el retry lo multiplica (2026-11-14, Dev fix F-01)

**Síntoma:** con el emulador pausado (docker pause), los requests colgaban 150-240 s+ pese a `Timeout=10s` en todos los ClientConfig (QA F-01).

**Causa raíz (2 capas):** (1) con `HttpClientFactory` custom, el SDK v4 NO aplica `ClientConfig.Timeout` a los requests — el timeout real es el del `HttpClient` devuelto, que teníamos en `Timeout.InfiniteTimeSpan`; (2) al corregirlo a 10 s, el retry default del SDK reintenta el timeout: 4 intentos × 10 s = **40,2 s medidos** (sin backoff visible).

**Fix verificado:** `HttpClient.Timeout = clientConfig.Timeout ?? 10s` en el factory + `MaxErrorRetry = 0` en los 3 clientes (el retry vive en la app: redelivery por visibility timeout con handlers idempotentes + loops L-007). E2e: 500 ProblemDetails a los **10,05 s** exactos en GET y POST con el emulador pausado; unpause → recovery total (saga completa). Unit: `PinnedCaHttpClientFactoryTests` (TcpListener que acepta y nunca responde + watchdog que convierte una regresión en fallo, no en hang).

**Lección:** "timeout configurado" ≠ "timeout efectivo": con un factory custom el SDK delega el enforcement al HttpClient que VOY a devolver. Verificarlo midiendo contra un peer colgado de verdad, y mirar cuántas veces el retry re-intenta el propio timeout (el presupuesto total = intentos × timeout). Documentado como enmienda en D-0003.

## L-012 — coverlet.msbuild no corre solo con referenciarlo; y umbral vs suite que auto-omite integración (2026-11-14, Dev fix F-03)

**Síntoma:** tras agregar `coverlet.msbuild` + `<Threshold>70</Threshold>`, `dotnet test` seguía sin evaluar el umbral (verde con runs parciales). Después, al activarlo, el run SIN env del emulador rompía el build (los tests de integración se auto-omiten → cobertura de los Core colapsa bajo 70).

**Causa raíz:** (1) coverlet.msbuild exige `/p:CollectCoverage=true` (o la prop en el csproj) para siquiera ejecutarse — el `Threshold` solo aplica cuando colecta; (2) nuestra cobertura de los Core proviene de la integración contra el emulador, así que el umbral solo es honesto en el modo de verificación (con env), no en el modo unit-only.

**Fix verificado:** `<CollectCoverage>true</CollectCoverage>` + umbral condicionado: `Threshold=70` si `$(AWS_EMULATOR_ACCESS_KEY_ID)` está seteada, `0` si no. Probado en ambos sentidos: suite completa con env 98/98 + umbral pasando (81-93% por Core); run filtrado con env → `error : The total line coverage is below the specified 70`, exit 1; sin env → verde.

**Lección:** un umbral de cobertura cableado en csproj se verifica con UN run que DEBA fallarlo — si no se vio romper el build, no está cableado. Y cuando parte de la cobertura depende de tests que se auto-omiten, el umbral se condiciona al modo donde ese conjunto corre (o el gate mide humo, no cobertura).

## L-014 — PS 5.1 stress: DefaultConnectionLimit serializa "N concurrentes" a 2; y foreach sobre List[object] con PSCustomObject rompe el binder DLR (2026-11-14, Dev S07)

**Síntoma (2 bugs del script de estrés, no de servicios):** (1) 20 POSTs "simultáneos" respondidos en 13-14 s — sin concurrencia real del lado del cliente; (2) crash "Los tipos de argumentos no coinciden" apuntando a un `foreach ($row in @($pending))` perfecto a simple vista.

**Causa raíz:**
1. .NET Framework limita por host a `ServicePointManager.DefaultConnectionLimit` = 2 por defecto (apps no-ASP.NET): 20 requests en cola de a 2 — la "carrera" era una fila india.
2. El binder de arrays de PS 5.1 (`PSToObjectArrayBinder` → `PSEnumerableBinder.MaybeDebase` → `Expression.Condition`) lanza `ArgumentException` al enumerar `List[object]` de .NET que contiene `PSCustomObject` — bug conocido del DLR de PS 5.1, no del script.

**Fix verificado:** (1) `ServicePointManager.DefaultConnectionLimit = 256` (+ `UseNagleAlgorithm=false`, `Expect100Continue=false`) en el static del helper `HttpClient` — N=4→57 ms, N=8→1,6 s de carrera; (2) arrays PS nativos (`$rows = @()` + `+=`) en vez de `List[object]` para colecciones que van a iterarse con `foreach`/`@()`. 3 corridas del stress PASS tras el fix.

**Lección:** en PS 5.1, un cliente de carga debe subir `DefaultConnectionLimit` ANTES de hablar de concurrencia (si no, se mide la cola del cliente); y las colecciones que cruzan `foreach`/`@()` van en arrays PS nativos — `List[T]` de .NET con PSCustomObject dentro es una bomba del binder. Nota aparte: el techo de ESCRITURA concurrente del DynamoDB del emulador (~13 s para 20 POSTs con saga) es del emulador, no del cliente — documentarlo, no "fixearlo".

## L-013 — PS 5.1: .ps1 sin BOM se lee como ANSI, y el pipe de consola corrompe no-ASCII al capturar evidencia (2026-11-14, Dev S05)

**Síntoma:** `trace-purchase.ps1` imprimía "Guardiǭn"/"â†'" (acentos y flechas corruptos) aunque el archivo en disco tenía los codepoints correctos; la evidencia capturada por redirección de stdout quedaba ilegible.

**3 intentos:** (1) `[Console]::OutputEncoding=UTF8` en el powershell hijo + Out-File utf8 — siguió corrupto (el writer de consola de .NET Framework ya estaba creado); (2) capturar el Information stream (`6>&1 | Out-File`) dentro del hijo — seguía cruzando el render del host con codepage OEM; (3) fix estructural: el script escribe su reporte con `[IO.File]::WriteAllText` + `UTF8Encoding($true)` (parámetro `-OutFile`) — inmune por construcción.

**Causa raíz (2 capas):** (a) PS 5.1 parsea .ps1 SIN BOM como ANSI → los literales con acentos se corrompen al PARSEAR; (b) el codepage de consola (OEM 850) no representa á/→ y aplica best-fit '?' al escribir stdout redirigido — cualquier captura por pipe manglea.

**Fix verificado:** BOM UTF-8 en todos los .ps1 (verificado por bytes EF BB BF; el edit de archivos EXISTENTES lo había dejado sin BOM — re-chequear tras editar) + reportes a archivo por .NET UTF-8 BOM. Evidencia S05 §6 legible byte a byte.

**Lección:** en PS 5.1, texto no-ASCII que deba sobrevivir (evidencia, reportes) se escribe con .NET (`WriteAllText` + UTF8 BOM), nunca por pipe/redirect de consola; y todo .ps1 nuevo del repo lleva BOM. Los mensajes que SÍ cruzan consola van en ASCII puro.

## L-015 — El dedup id de un mensaje de FALLO debe incluir el paso/attempt: {type}:{purchase} traga el segundo fallo de la misma compra (2026-11-15, Dev fix F-V1-R2-01)

**Síntoma:** con DOS reglas persistent (issue-tickets + release-seats, o authorize-payment + release-seats, PAN aprobado) la compra quedaba eterna en PAYMENT_APPROVED/SEATS_RESERVED sin `failure_reason` — el mismo síntoma de F-V1-01 pero por el transporte: el `step-failed` de la compensación jamás llegaba a Órdenes aunque el publicador registraba el send OK.

**Causa raíz:** `SqsSagaTransport` mandaba TODO mensaje con `MessageDeduplicationId={type}:{purchase_id}`. El evento de fallo es siempre `type=step-failed`, así que el segundo paso fallado de la MISMA compra colisionaba en la ventana de dedup de SQS FIFO y el mensaje se descartaba EN SILENCIO (el `SendMessage` no falla). El fix de aplicación de F-V1-01 era correcto pero inalcanzable por esta vía: dos capas, mismo síntoma.

**Fix verificado (1 línea + helper):** el `step-failed` añade `StepFailedEvent.Step` a su dedup id (guid si el payload no lo da); el resto de los mensajes conserva `{type}:{purchase_id}` (dedup natural de reenvíos intacto; duplicados que pasen la ventana ~5 min caen en el guard CAS del process manager, como siempre declaró D-0006). Test de transporte (pasos distintos → ambos entregados; idéntico → 1) + 2 e2e de doble fallo. Control rojo: con el fix revertido los 3 tests fallan exactamente con el síntoma del QA.

**Lección:** en una cola FIFO con dedup por id explícito, un mismo {tipo de mensaje} que puede ocurrir DOS veces por clave de agrupación (grupo=purchase) necesita en el dedup id TODO lo que distingue ocurrencias legítimas (paso, attempt, emission) — el dedup del transporte deduplica REENVÍOS, no ocurrencias. Y: "el publicador no falló" ≠ "el mensaje llegó"; cuando un handler aguanta un evento que nunca llega, mirar la capa de dedup ANTES de sospechar del consumidor.
