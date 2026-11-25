# Cognito + Parameter Store (D-0024). Nombres y valores salen del manifiesto (../aws-local.json).

resource "aws_cognito_user_pool" "this" {
  name = local.manifest.userPool.name
}

resource "aws_cognito_user_pool_client" "cli" {
  name         = local.manifest.userPool.clientName
  user_pool_id = aws_cognito_user_pool.this.id
  # Sin generate_secret: cliente público (una CLI o una SPA no pueden esconder un secreto).
  explicit_auth_flows = ["ALLOW_USER_PASSWORD_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"]
}

# Parámetros de configuración declarados en el manifiesto.
resource "aws_ssm_parameter" "config" {
  for_each = { for p in local.manifest.parameters : p.name => p.value }
  name     = each.key
  type     = "String"
  value    = each.value

  # El manifiesto da el valor inicial; un cambio posterior (put-parameter) se respeta, igual que en Provision.
  lifecycle {
    ignore_changes = [value]
  }
}

# Ids que genera Cognito: se publican para que los servicios los descubran.
resource "aws_ssm_parameter" "auth" {
  for_each = {
    "/ticketflow/auth/user-pool-id" = aws_cognito_user_pool.this.id
    "/ticketflow/auth/client-id"    = aws_cognito_user_pool_client.cli.id
  }
  name  = each.key
  type  = "String"
  value = each.value
}
