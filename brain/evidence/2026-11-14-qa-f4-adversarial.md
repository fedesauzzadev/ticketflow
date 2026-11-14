# Evidencia QA F4 - auditoria adversarial del MVP (H-01..H-04)

Fecha: 2026-11-14 - Auditor: QA (Fase 4) - Entorno: Windows PS 5.1, Docker Desktop, .NET 10 SDK
(10.0.403), emulador aws-emulator :9297 (imagen aws-emulator:verified). Comandos desde la raiz del
repo salvo indicacion. Log crudo de payloads de la bateria A: %TEMP%\opencode\qa-f4\battery-a.log.

## 1) Build + suite desde cero (servicios abajo, L-008)

    Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
    dotnet build TicketFlow.sln --nologo
      -> Compilacion correcta. 0 Advertencia(s) 0 Errores  [13 proyectos, 18,8 s]
    dotnet test TicketFlow.sln --nologo   [env AWS_EMULATOR_* extraida del contenedor vivo]
      -> Orders 48/48 - Catalog 39/39 - Payments 6/6 - Ticketing 3/3 - EXIT=0
      -> 96/96 (claim del dev CONFIRMADO; 2 corridas, la 2da con coleccion de cobertura)

## 2) Cobertura (coverlet.collector XPlat; umbral NO cableado - hallazgo F-03)

    Catalog.Core 81,2% - Orders.Core 85,5% - Payments.Core 92,8% - Ticketing.Core 87,3%  (todos >=70 OK)
    Shared.Infrastructure 67,8% (fuera del NFR-6, informativo)
    Payments.Api / Ticketing.Api: 0% en la suite (solo e2e/manual) - hallazgo F-07

## 3) Dependencias / secrets

    dotnet list TicketFlow.sln package --vulnerable --include-transitive -> 13 proyectos, 0 vulnerables
    grep repo (AKIA|ASIA|secret=|password=|BEGIN PRIVATE KEY) -> 0 hits en codigo/scripts/docs
    logs de AMBOS runs (380+ lineas JSON) -> 0 hits de PAN, 0 aws_secret, 0 AKIA
    GET /purchases/{id} sobre compra real -> no expone pan
    .gitignore: bin/ obj/ TestResults/ .secrets/ *.user -> .secrets/ca.pem fuera de VCS

## 4) Bateria A - payloads maliciosos/invalidos (26 requests; bodies completos en battery-a.log)

    a01 json roto -> 400 invalid-body        a11 body array [1,2,3] -> 400 invalid-body
    a02 body vacio -> 400 invalid-body       a12 body null -> 400 invalid-body
    a03 seats -3 -> 400 invalid-purchase     a13 sin card -> 400 card es requerido
    a04 seats 9 -> 400 invalid-purchase      a14 anidamiento profundo -> 400 invalid-body
    a05 seats 1e20 -> 400 invalid-body       a15 sin Idempotency-Key -> 400 invalid-idempotency-key
    a06 seats string -> 400 invalid-body     a16 key 201 chars -> 400 invalid-idempotency-key
    a07 pan con letras -> 400                a17 key 10.000 chars -> 400 invalid-idempotency-key
    a08 holder 100.000 chars -> 400          a18 Content-Type text/plain + JSON -> 201 (lenient, F-04)
    a09 event_id unicode/nulo -> 400         a20 id invalido -> 400 invalid-purchase-id
    a10 cvv 4 digitos -> 400                 a21 id valido inexistente -> 404 purchase-not-found
                                                a22 PUT /purchases -> 405 - a23 DELETE evento -> 404
                                                a24/a25 path desconocido / traversal -> 404
                                                a26 %00 en path -> 400 - a27/a28 ids invalidos -> 404/400
    5xx: 0 - stack traces/internals: 0 - procesos vivos toda la bateria.

## 5) Bateria B - idempotencia bajo concurrencia (4 POST simultaneos, MISMA key)

    201 pur-a8af1830904b4fc0a2df920c155bdea3 (1 sola creacion) + 3x 200 misma compra TICKETS_ISSUED
    201s: 1 - purchase_ids distintos: 1 - delta disponibilidad: -1 exacto - tickets: 1
    log: "replay con key qa-b1-529119f8: misma compra ... sin efectos nuevos" (x3)

## 6) Bateria B - oversell (compra concurrente sobre ultima disponibilidad)

    b2: it-00b1437d8d71 (avail=1), 4 compradores x1 -> 201,422,422,422; 1 TICKETS_ISSUED; final 0.
    b3: it-5150fd2e2604 (avail=3), 6 compradores x1, TODOS 201 (pasaron el precheck en la carrera):
        3 TICKETS_ISSUED + 3 FAILED (seats-unavailable por decremento condicional); final 0.
    b4: it-57296193bc7d (avail=5), 3 compradores x2 asientos: 2 emitidas + 1 FAILED; final 1.
    Invariantes en los 3: avail>=0 - emitidas<=floor(avail0/seats) - delta==emitidas*seats. CERO oversell.

## 7) Bateria C - replay tras FAILED + mismatch

    POST PAN 0002 key qa-c1-* -> 201 pur-02ab1c1e4fcf4395a90d8a24e27571a4 -> FAILED (pago rechazado)
    disponibilidad 248 -> FAILED -> 248 (compensacion restaurada); payments: DECLINED
    REPLAY misma key+body -> 200 misma compra FAILED, disponibilidad 248 (sin efectos nuevos)
    REPLAY misma key+otro body -> 422 idempotency-key-reused

## 8) Gates de division - demos corridas por QA (exit code real)

    powershell -File scripts\demo-cache.ps1 -> EXIT=0
      RESUMEN: MISS(499) -> HIT(499) -> compra -> MISS(498) -> HIT(498) [invalidacion demostrada]
    powershell -File scripts\demo-idempotency.ps1 -> EXIT=0
      misma key => misma compra pur-2ed605...; 499 -> 498 -> 498 (delta 0); mismatch => 422;
      agotado + key nueva => 422 seats-unavailable

## 9) Gate NFR-7 en runtime - endpoint AWS real rechazado al arranque

    AWS_EMULATOR_ENDPOINT=https://dynamodb.us-east-1.amazonaws.com + creds dummy + dotnet run Catalog.Api:
      Unhandled exception. System.InvalidOperationException:
      AWS_EMULATOR_ENDPOINT debe ser loopback ... cero llamadas a nube real (NFR-7).  EXIT=-532462766
    Estatico ademas: 0 refs a RegionEndpoint/amazonaws.com en src; ServiceURL+AuthenticationRegion;
    pin de CA por thumbprint en PinnedCaHttpClientFactory (DynamoDB/SQS/S3); AWS_EC2_METADATA_DISABLED=true.

## 10) NFR-5 - p95 medido POR QA (n=50, HttpClient compartido, warmup; script qa-bench.cs en %TEMP%)

    GET avail (HIT)  p50=2,6ms   p95= 5,8ms  max= 6,5ms   (objetivo <50)    OK
    POST /purchases  p50=7,8ms   p95=22,7ms  max=41,8ms   (objetivo <200)   OK
    SAGA -> terminal p50=658,8ms p95=768,3ms max=773,3ms  (objetivo <2000)  OK
    Numeros del dev (1,4 / 20,7 / 790 ms) plausibles - misma magnitud.

## 11) Quickstart frio (H-04 AC1) - maquina virgen simulada por QA

    Estado inicial: 4 servicios muertos + contenedor aws-emulator EXITED + .secrets/ BORRADO.
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
      ==> Arrancando contenedor ... Emulador OK (health) ... build 0 warnings ... Todo listo (~2,5 min)
    GET /events -> evt-mj-tribute-night=500, evt-legacy-tour=250 (re-seed limpio tras perder estado)
    POST PAN aprobada -> 201 -> TICKETS_ISSUED, ticket tkt-54ea... con qr_s3_key referenciable (S3).

## 12) Inyeccion de falla - emulador congelado (docker pause) [hallazgo F-01]

    1a tanda: GET availability sin respuesta en 30s; POST sin respuesta en 60s (curl corta por max-time).
              unpause -> 4/4 servicios vivos; saga completa posterior OK (201 -> TICKETS_ISSUED).
    2a tanda: GET availability: HTTP 000 a los 150,1s. POST: HTTP 000 a los 240,0s. unpause -> catalog vivo.
    Logs DURANTE las pausas: 0 entradas LogLevel=Error en ambos runs (ni timeouts ni reintentos de
    workers) => el Timeout=10s del ClientConfig NO acota la latencia visible contra un peer colgado
    (el HttpClient custom usa Timeout.InfiniteTimeSpan y la peticion queda suspendida).
    Recuperacion: limpia, sin crash, sin corrupcion.

## 13) Salud del repo

    TODO/FIXME en src: 0 (matches son "todo" en espanol, docs). Basura: 0 (TestResults de QA limpiados).
    %TEMP%: transcripts del dev en %TEMP%\opencode\s04-verify\ (fuera del repo, sin secrets).
    LICENSE / CHANGELOG / version: AUSENTES (hallazgo F-02). Repo NO es git todavia.
