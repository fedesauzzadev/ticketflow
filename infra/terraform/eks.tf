# EKS (N-4003): red + IAM + cluster + managed node group, contra el emulador.
# SOLO control plane: el emulador registra cluster y node group (ACTIVE) pero no corre
# nodos ni expone la API de Kubernetes al host. Los pods van a un Kubernetes local.

locals {
  cluster_name = "ticketflow"
  # EKS exige subnets en al menos 2 AZs distintas.
  eks_subnets = {
    "us-east-1a" = "10.20.1.0/24"
    "us-east-1b" = "10.20.2.0/24"
  }
}

resource "aws_vpc" "eks" {
  cidr_block           = "10.20.0.0/16"
  enable_dns_hostnames = true # los nodos se registran por nombre DNS
  tags                 = { Name = "${local.cluster_name}-vpc" }
}

resource "aws_subnet" "eks" {
  for_each          = local.eks_subnets
  vpc_id            = aws_vpc.eks.id
  availability_zone = each.key
  cidr_block        = each.value
  tags = {
    Name                                          = "${local.cluster_name}-${each.key}"
    "kubernetes.io/cluster/${local.cluster_name}" = "shared" # discovery de subnets por EKS/LB
  }
}

# --- IAM: dos roles distintos, dos principals distintos --------------------------------
# Rol del CLUSTER: lo asume el servicio EKS para manejar el control plane.
resource "aws_iam_role" "eks_cluster" {
  name = "${local.cluster_name}-eks-cluster"
  assume_role_policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Principal = { Service = "eks.amazonaws.com" }, Action = "sts:AssumeRole" }]
  })
}

resource "aws_iam_role_policy_attachment" "eks_cluster" {
  role       = aws_iam_role.eks_cluster.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonEKSClusterPolicy"
}

# Rol de los NODOS: lo asumen las instancias EC2 (kubelet, CNI, pull de imágenes de ECR).
resource "aws_iam_role" "eks_node" {
  name = "${local.cluster_name}-eks-node"
  assume_role_policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Principal = { Service = "ec2.amazonaws.com" }, Action = "sts:AssumeRole" }]
  })
}

resource "aws_iam_role_policy_attachment" "eks_node" {
  for_each = toset([
    "AmazonEKSWorkerNodePolicy",
    "AmazonEKS_CNI_Policy",
    "AmazonEC2ContainerRegistryReadOnly",
  ])
  role       = aws_iam_role.eks_node.name
  policy_arn = "arn:aws:iam::aws:policy/${each.key}"
}

# --- Cluster + node group ---------------------------------------------------------------
resource "aws_eks_cluster" "this" {
  name     = local.cluster_name
  role_arn = aws_iam_role.eks_cluster.arn

  vpc_config {
    subnet_ids = [for s in aws_subnet.eks : s.id]
  }

  # Sin la policy adjunta, EKS real falla al crear/borrar recursos del cluster.
  depends_on = [aws_iam_role_policy_attachment.eks_cluster]
}

resource "aws_eks_node_group" "default" {
  cluster_name    = aws_eks_cluster.this.name
  node_group_name = "default"
  node_role_arn   = aws_iam_role.eks_node.arn
  subnet_ids      = [for s in aws_subnet.eks : s.id]
  instance_types  = ["t3.medium"]

  scaling_config {
    desired_size = 2
    min_size     = 1
    max_size     = 3
  }

  depends_on = [aws_iam_role_policy_attachment.eks_node]
}

output "eks" {
  value = {
    cluster  = aws_eks_cluster.this.name
    version  = aws_eks_cluster.this.version
    endpoint = aws_eks_cluster.this.endpoint # interno al contenedor del emulador
    status   = aws_eks_cluster.this.status
  }
}
