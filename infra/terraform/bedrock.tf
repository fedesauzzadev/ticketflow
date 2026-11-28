# Guardrail de Bedrock (N-4011). Nombre, mensaje y palabras salen del manifiesto (../aws-local.json).

resource "aws_bedrock_guardrail" "this" {
  name                      = local.manifest.guardrail.name
  blocked_input_messaging   = local.manifest.guardrail.blockedMessage
  blocked_outputs_messaging = local.manifest.guardrail.blockedMessage

  word_policy_config {
    dynamic "words_config" {
      for_each = local.manifest.guardrail.blockedWords
      content {
        text = words_config.value
      }
    }
  }
}

# El id lo genera Bedrock: se publica para que el Catálogo lo descubra.
resource "aws_ssm_parameter" "guardrail" {
  name  = "/ticketflow/catalog/guardrail-id"
  type  = "String"
  value = aws_bedrock_guardrail.this.guardrail_id
}
