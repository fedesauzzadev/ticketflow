# D-0023: gate NFR-7 con allowlist opt-in de UN host + bind configurable (servicios en contenedores)

Date: 2026-11-21
Reversibility: easy

## Context
`EmulatorAwsOptions.FromEnvironment` rechaza todo endpoint no-loopback (NFR-7, "cero
llamadas a nube real") y los 4 `Program.cs` bindean `http://localhost:{port}`. Dentro de
un contenedor el emulador no es loopback y `localhost` no es alcanzable desde afuera:
ambos impiden dockerizar (N-4004) y desplegar en Kubernetes después.
El dueño eligió esta opción el 2026-11-21 frente a compartir el network namespace del emulador.

## Decision
1. `AWS_EMULATOR_ALLOWED_HOST` (opcional): autoriza UN host extra, match exacto
   case-insensitive. Sin la variable, el comportamiento es idéntico al histórico.
   Un valor que contenga `amazonaws` o `aws.com` nunca autoriza nada.
   Lógica pura en `EmulatorAwsOptions.IsAllowedHost` (testeable sin tocar env).
2. La garantía dura no cambia: `PinnedCaHttpClientFactory` exige que el root de la cadena
   TLS sea la CA del emulador de la corrida. AWS real no puede presentar esa cadena.
3. `SERVICE_HOST` (opcional, default `localhost`): host de bind de Kestrel. Las imágenes
   lo fijan en `0.0.0.0`; fuera de contenedores nada cambia.
4. En compose el emulador se alcanza por `host.docker.internal:9297`: funciona igual si
   el emulador lo levantó compose o `docker run` (emulator-env.ps1).

## Alternatives considered
- **Compartir el netns del emulador** (`network_mode: container:aws-emulator`): no toca el
  gate, pero exige recrear el emulador para publicar 5080-5083 y en k8s fuerza un único
  Pod con todo. Rechazado por el dueño.
- **Quitar el gate de loopback** y confiar solo en el pin de CA: pierde una capa de defensa
  y el fail-fast con mensaje claro. Rechazado.
- **Lista de N hosts / wildcard**: nadie lo necesita. Rechazado (YAGNI).
