# AWS deployment recipes

On-demand diagnostics for .NET applications running on AWS-managed container
hosts. Recipes here deploy `dotnet-diagnostics-mcp` as a **sidecar container**
alongside your application; the sidecar attaches to the app via the .NET
runtime's diagnostic IPC socket (created in `/tmp`), so the target app needs
**no code changes**.

| Recipe | Target host | Multi-container model | External MCP ingress? |
|---|---|---|---|
| [`ecs-fargate/`](ecs-fargate/) | ECS on Fargate (1.4.0+) | One task, two containers, shared task-scoped volume on `/tmp` | No — reach via internal ALB or `aws ssm start-session` port forward |
| [`ecs-ec2/`](ecs-ec2/) | ECS on EC2 (Linux) | One task, two containers, shared task-scoped volume on `/tmp` | No — reach via internal ALB or `aws ssm start-session` port forward |

For Kubernetes on EKS (or any other cluster), use the generic recipes under
[`../k8s/`](../k8s/) instead. For Azure-managed container hosts, see
[`../azure/`](../azure/). For GCP-managed container hosts, see
[`../gcp/`](../gcp/).

> **AWS Lambda** is intentionally not covered. Lambda's freeze-between-
> invocations execution model breaks long-running EventPipe sessions and
> `ptrace` attach paths.

## What lives here

- [`ecs-fargate/`](ecs-fargate/) — single-task CloudFormation recipe
  (`main.yaml` + `parameters.example.json` + README with smoke test).
- [`ecs-ec2/`](ecs-ec2/) — EC2 launch-type CloudFormation recipe. Same
  two-container shape as Fargate, plus **off-CPU sampling**
  (`collect_sample(kind="off_cpu")`) works because you control the EC2 host
  kernel (`kernel.perf_event_paranoid=-1`) — a knob Fargate does not expose.
  Bring your own EC2 cluster capacity.

## Future additions

- **CDK or Terraform alternate dialects** are deliberately deferred; the
  initial reference is CloudFormation YAML for parity with the Azure Bicep
  and Kubernetes YAML recipes already in this repo.
