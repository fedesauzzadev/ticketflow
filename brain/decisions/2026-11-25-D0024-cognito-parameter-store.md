# D-0024: Cognito para la compra y Parameter Store para la configuración

Date: 2026-11-25
Reversibility: easy

## Context
El dueño pidió sumar Parameter Store y Cognito con fin didáctico (N-4007). La API de
compras era anónima y la configuración venía solo por variables de entorno.

## Decision
1. JWT bearer solo en las rutas de compra de Órdenes. Se valida firma (JWKS con CA
   pineada, host reescrito al endpoint configurado), issuer, expiración,
   `token_use=access`, `client_id` y `sub`. Sin `aud`: el access token no lo trae.
2. Dueño = `sub`; ajeno -> 404; Idempotency-Key con alcance `{sub}:{key}`.
3. Parameter Store bajo `/ticketflow/`, leído al arrancar. Env > parámetro > default.
4. Pool, client y parámetros en el manifiesto; Provision no sobrescribe valores existentes.
5. Cambio incompatible: comprar exige token.

## Alternatives considered
- Auth en los 4 servicios: Pagos y Ticketing se alcanzan por la saga; más superficie sin pedido. Diferido.
- Secrets Manager: no hay secretos que rotar.
- Recarga en caliente de parámetros: YAGNI.
