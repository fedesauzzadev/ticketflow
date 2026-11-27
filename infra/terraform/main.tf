# TicketFlow — infraestructura del emulador AWS local como Terraform (N-4001, D-0022).
# Los recursos NO se listan acá: salen de ../aws-local.json, el mismo manifiesto que usa
# tools/TicketFlow.Provision. Un manifiesto, dos ejecutores, cero deriva.
#
# Uso (el wrapper inyecta credenciales + CA de la corrida por env):
#   powershell -File scripts\terraform.ps1 apply
#   powershell -File scripts\terraform.ps1 plan -detailed-exitcode   # 0 = sin drift

terraform {
  required_version = ">= 1.7"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    archive = {
      source  = "hashicorp/archive"
      version = "~> 2.0"
    }
  }
}

variable "emulator_endpoint" {
  description = "Endpoint HTTPS del emulador. Único destino: nunca AWS real."
  type        = string
  default     = "https://127.0.0.1:9297"
}

# Credenciales: AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY. CA propia: AWS_CA_BUNDLE.
provider "aws" {
  region = "us-east-1"

  # Sin estos flags el provider consulta STS/IAM/IMDS reales al arrancar.
  skip_credentials_validation = true
  skip_requesting_account_id  = true
  skip_metadata_api_check     = true
  s3_use_path_style           = true # bucket en el path: no hay DNS bucket.localhost

  endpoints {
    dynamodb = var.emulator_endpoint
    sqs      = var.emulator_endpoint
    s3       = var.emulator_endpoint
    sts      = var.emulator_endpoint
    iam      = var.emulator_endpoint
    lambda   = var.emulator_endpoint
    logs     = var.emulator_endpoint
    eks      = var.emulator_endpoint
    ec2      = var.emulator_endpoint
    ssm      = var.emulator_endpoint

    cognitoidp = var.emulator_endpoint
    bedrock    = var.emulator_endpoint
  }
}

locals {
  manifest = jsondecode(file("${path.module}/../aws-local.json"))
  tables   = { for t in local.manifest.tables : t.name => t }
  queues   = { for q in local.manifest.queues : q.name => q }
  buckets  = toset([for b in local.manifest.buckets : b.name])
}

resource "aws_dynamodb_table" "this" {
  for_each     = local.tables
  name         = each.key
  billing_mode = each.value.billingMode
  hash_key     = one([for k in each.value.keySchema : k.attributeName if k.keyType == "HASH"])
  range_key    = one([for k in each.value.keySchema : k.attributeName if k.keyType == "RANGE"])

  dynamic "attribute" {
    for_each = each.value.attributeDefinitions
    content {
      name = attribute.value.attributeName
      type = attribute.value.attributeType
    }
  }
}

resource "aws_sqs_queue" "this" {
  for_each                    = local.queues
  name                        = each.key
  fifo_queue                  = each.value.fifo
  content_based_deduplication = each.value.contentBasedDeduplication
}

resource "aws_s3_bucket" "this" {
  for_each = local.buckets
  bucket   = each.key
}

output "resources" {
  value = {
    tables  = keys(aws_dynamodb_table.this)
    queues  = { for k, q in aws_sqs_queue.this : k => q.arn }
    buckets = keys(aws_s3_bucket.this)
  }
}
