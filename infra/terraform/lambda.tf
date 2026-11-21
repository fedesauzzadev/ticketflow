# Lambda ticket-notifier (N-4002): S3 ObjectCreated (QR) -> Lambda -> CloudWatch Logs.
# Cadena completa de un trigger: zip -> rol IAM -> función -> permiso -> notificación.

locals {
  notifier_name = "ticketflow-ticket-notifier"
  qr_bucket     = aws_s3_bucket.this["ticketflow-tickets"]
}

data "archive_file" "notifier" {
  type        = "zip"
  source_file = "${path.module}/../lambda/ticket_notifier/handler.py"
  output_path = "${path.module}/.terraform/notifier.zip" # .terraform/ está gitignored
}

# Trust policy: QUIÉN puede asumir el rol (el servicio Lambda).
resource "aws_iam_role" "notifier" {
  name = "${local.notifier_name}-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_cloudwatch_log_group" "notifier" {
  name              = "/aws/lambda/${local.notifier_name}"
  retention_in_days = 7
}

# Permission policy: QUÉ puede hacer la función. Mínimo privilegio: solo sus logs.
resource "aws_iam_role_policy" "notifier_logs" {
  name = "logs"
  role = aws_iam_role.notifier.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
      Resource = "${aws_cloudwatch_log_group.notifier.arn}:*"
    }]
  })
}

resource "aws_lambda_function" "notifier" {
  function_name    = local.notifier_name
  role             = aws_iam_role.notifier.arn
  runtime          = "python3.12"
  handler          = "handler.handler"
  filename         = data.archive_file.notifier.output_path
  source_code_hash = data.archive_file.notifier.output_base64sha256 # cambia el código => redeploy
  timeout          = 5
  depends_on       = [aws_cloudwatch_log_group.notifier, aws_iam_role_policy.notifier_logs]
}

# Resource policy de la función: S3 (y solo ESTE bucket) puede invocarla.
resource "aws_lambda_permission" "s3_invoke" {
  statement_id  = "AllowS3Invoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.notifier.function_name
  principal     = "s3.amazonaws.com"
  source_arn    = local.qr_bucket.arn
}

resource "aws_s3_bucket_notification" "qr_created" {
  bucket = local.qr_bucket.id
  lambda_function {
    lambda_function_arn = aws_lambda_function.notifier.arn
    events              = ["s3:ObjectCreated:*"]
    filter_prefix       = "qr/"
    filter_suffix       = ".json"
  }
  depends_on = [aws_lambda_permission.s3_invoke]
}

output "notifier_log_group" {
  value = aws_cloudwatch_log_group.notifier.name
}
