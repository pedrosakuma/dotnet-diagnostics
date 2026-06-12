# AWS ECS / EC2 (Linux) recipe

On-demand diagnostics for .NET applications running on **AWS ECS with the
EC2 launch type** (Linux container instances). The recipe deploys
`dotnet-diagnostics-mcp` as a **sidecar container** alongside your application
in the same ECS task. The sidecar attaches to the app via the .NET runtime's
diagnostic IPC socket (created in `/tmp`), so the target app needs **no code
changes**.

This is the EC2 counterpart to the
[Fargate recipe](../ecs-fargate/). The key difference: because you control the
EC2 host kernel, you can set `kernel.perf_event_paranoid=-1` so host-side
**off-CPU sampling** (`collect_sample(kind="off_cpu")`) works — something
Fargate's managed host cannot offer. (ECS itself does not accept `CAP_PERFMON`
on the container; the host sysctl is the gate.)

| Artifact | Purpose |
|---|---|
| [`main.yaml`](main.yaml) | CloudFormation template: log group, IAM roles, task definition (`app` + `diag`), ECS service on the EC2 launch type. |
| [`parameters.example.json`](parameters.example.json) | Placeholder values; copy + edit, then pass via `--parameters file://...`. |

For Kubernetes on EKS (or any other cluster), use the generic recipes under
[`../../k8s/`](../../k8s/) instead.

> **This template does not provision EC2 capacity.** Point `ClusterArn` at a
> cluster that already has registered ECS container instances (an Auto Scaling
> group of ECS-optimized AMIs) or a default capacity-provider strategy. A
> Fargate-only cluster will not schedule this task.
>
> **AWS Lambda is out of scope** — Lambda's freeze-between-invocations model
> breaks long-running diagnostic sessions.

---

## Why EC2 over Fargate

| | Fargate | EC2 (this recipe) |
|---|---|---|
| EventPipe tools (counters, CPU sample, GC, exceptions) | ✅ | ✅ |
| `ptrace` tools (thread snapshot, live heap, dump) | ✅ `SYS_PTRACE` | ✅ `SYS_PTRACE` |
| Off-CPU sampling (`collect_sample(kind="off_cpu")`) | ❌ no host kernel control | ✅ host `perf_event_paranoid=-1` (you own the AMI) |
| Host kernel control | ❌ managed | ✅ you own the AMI |

Pick this recipe when you need off-CPU / blocking analysis, or when you already
run an EC2-backed cluster and want diagnostics inside it. Otherwise the Fargate
recipe is operationally simpler.

> **Note on off-CPU and capabilities.** ECS does **not** accept `CAP_PERFMON`
> in `LinuxParameters.Capabilities.Add` (unlike Docker/Kubernetes). On ECS/EC2
> off-CPU sampling is therefore enabled purely by setting the **host** kernel's
> `kernel.perf_event_paranoid = -1` — no container capability is involved. That
> host knob is exactly what Fargate does not expose, which is why off-CPU works
> here and not there.

## Prerequisites

1. **AWS CLI v2** authenticated against the target account.
2. **An ECS cluster with EC2 capacity.** Either:
   - **container instances already registered** to the cluster (an Auto Scaling
     group of ECS-optimized instances) — use the default `LaunchType: EC2`; or
   - a **capacity provider** (ASG capacity provider with managed scaling) —
     pass its name as `CapacityProviderName` so the service launches capacity
     on demand. (A bare `LaunchType: EC2` ignores any cluster *default*
     capacity-provider strategy, so set `CapacityProviderName` explicitly to
     use one.)

   The instances must run a Linux AMI and live in the `Subnets` you pass.
3. **Subnets and a security group** for the task ENI (the template uses
   `awsvpc` network mode, same as Fargate):
   - The subnets must overlap where your EC2 instances run.
   - The security group must allow **inbound TCP 18887** (default `DiagPort`)
     **only from your operator network or an internal ALB**. Do not expose
     the MCP endpoint to the public internet.
4. **Container images reachable from the task** — your app image plus the
   diagnostics sidecar image (default
   `ghcr.io/pedrosakuma/dotnet-diagnostics:0.14.0`; mirror to ECR if your
   account blocks anonymous GHCR pulls).
5. **A bearer token in AWS Secrets Manager** for the MCP HTTP transport:
   ```bash
   TOKEN=$(openssl rand -hex 32)
   aws secretsmanager create-secret \
     --name diag-mcp-bearer-token \
     --secret-string "$TOKEN"
   ```
6. **(For off-CPU sampling) host kernel `perf` access.** The EC2 **host
   kernel** gates `perf` tracepoints via `kernel.perf_event_paranoid`. ECS does
   not accept `CAP_PERFMON`, so this host sysctl is the only lever — see the
   next section.
7. **cfn-lint** for static validation (optional but recommended):
   ```bash
   python3 -m venv ~/.cfnlint && ~/.cfnlint/bin/pip install cfn-lint
   ~/.cfnlint/bin/cfn-lint deploy/aws/ecs-ec2/main.yaml
   ```

## Enabling off-CPU sampling on the EC2 host

Off-CPU sampling needs the `sched:sched_switch` tracepoint. On ECS/EC2 there is
**no container capability to add** — ECS rejects `CAP_PERFMON` in
`LinuxParameters.Capabilities.Add`. The only requirement is that the EC2
**host kernel** permits the tracepoint via `kernel.perf_event_paranoid`.

Set it on the **EC2 container instances** (e.g. via the Auto Scaling group
launch template user-data), not inside the container:

```bash
# user-data on the ECS-optimized AMI:
echo 'kernel.perf_event_paranoid = -1' > /etc/sysctl.d/99-perf.conf
sysctl --system
```

- `-1` — all events, required for `sched_switch` off-CPU sampling.
- `1` — CPU-event sampling only (on-CPU works, off-CPU may not).
- `2` (common default) — blocks the tracepoints off-CPU sampling needs.

If you do not need off-CPU sampling, leave the host default in place; EventPipe
and `ptrace`-based tools are unaffected.

## UID matching (read this first)

Both containers must agree on the UID that creates and reads
`/tmp/dotnet-diagnostic-<pid>-<unique>-socket`. The default
`dotnet-diagnostics-mcp` image runs as UID **10001**. Most Microsoft
`mcr.microsoft.com/dotnet/aspnet:*` images run as **root**. The template
forces the diag container to **`User: "0"`** because that is the path with the
fewest surprises for the reference recipe.

Two alternatives, if running diag as root is not acceptable:

- **Most secure**: rebuild your app image with `USER 10001` and make `/tmp`
  writable by that UID, then drop the `User: "0"` override on the diag
  container.
- **Easiest, fully controlled**: rebuild the diag image without the non-root
  user (`USER root` in your fork of `deploy/Dockerfile`).

If the UIDs do not match, you'll see `Permission denied` when the diag
container tries to open the diagnostic socket. See `AGENTS.md` → "Diagnostic
socket UID".

## Capability matrix

| MCP tool family | Works on ECS / EC2? | Notes |
|---|---|---|
| EventPipe (`inspect_process`, `collect_sample(kind="cpu")`, `collect_events`, …) | ✅ Yes | Only needs socket access + UID match. |
| ClrMD / `ptrace` (`collect_thread_snapshot`, `inspect_heap(source="live")`, `collect_process_dump`) | ✅ Yes | `LinuxParameters.Capabilities.Add: [SYS_PTRACE]` (template adds it). |
| `perf`-based off-CPU sampling (`collect_sample(kind="off_cpu")`) | ✅ Yes | No container capability (ECS rejects `CAP_PERFMON`); requires host `kernel.perf_event_paranoid = -1`. |

## Deploy

The example parameters file uses the `aws cloudformation create-stack
--parameters file://...` JSON shape, so the commands below use `create-stack`
/ `update-stack`.

```bash
cp deploy/aws/ecs-ec2/parameters.example.json /tmp/diag-params.json
# Edit /tmp/diag-params.json with your cluster ARN, subnets, security group,
# app image, and Secrets Manager ARN.

# First deploy:
aws cloudformation create-stack \
  --stack-name dotnet-diagnostics-mcp-ec2 \
  --template-body file://deploy/aws/ecs-ec2/main.yaml \
  --capabilities CAPABILITY_IAM \
  --parameters file:///tmp/diag-params.json

aws cloudformation wait stack-create-complete --stack-name dotnet-diagnostics-mcp-ec2

# Subsequent updates:
aws cloudformation update-stack \
  --stack-name dotnet-diagnostics-mcp-ec2 \
  --template-body file://deploy/aws/ecs-ec2/main.yaml \
  --capabilities CAPABILITY_IAM \
  --parameters file:///tmp/diag-params.json
```

The stack creates:

- a CloudWatch log group `/ecs/dotnet-diagnostics-mcp-ec2` (7-day retention)
- a task execution role (ECR pull + CloudWatch Logs + read the bearer secret)
- a task role (empty unless ECS Exec is enabled; then SSM channel rights)
- one task definition with two containers (`app` + `diag`) sharing `/tmp`,
  `PidMode: task`, and `SYS_PTRACE` on the diag container
- one ECS service on the **EC2** launch type (or a capacity-provider strategy
  when `CapacityProviderName` is set)

## Smoke test

```bash
# 1. Wait for the service to stabilize.
aws ecs wait services-stable --cluster diag-ec2-cluster --services dotnet-diagnostics-mcp

# 2. Find the running task ARN.
TASK_ARN=$(aws ecs list-tasks --cluster diag-ec2-cluster \
  --service-name dotnet-diagnostics-mcp --query 'taskArns[0]' --output text)

# 3. Shell into the diag container (requires EnableEcsExec=true) and confirm
#    the .NET diagnostic socket from the app is visible.
aws ecs execute-command \
  --cluster diag-ec2-cluster \
  --task "$TASK_ARN" \
  --container diag \
  --interactive \
  --command "/bin/sh"
# inside the container:
ls /tmp/dotnet-diagnostic-*    # must list the app process socket
```

If `/tmp/dotnet-diagnostic-*` is missing inside `diag`, the UID is mismatched
or `pidMode: task` is not in effect — re-check the prerequisites above.

## MCP client snippet

Once the service is healthy and reachable from your operator network
(internal ALB, port-forward, VPN, etc.):

```json
{
  "mcpServers": {
    "dotnet-diag-aws-ec2": {
      "url": "http://<service-endpoint>:18887/mcp",
      "headers": {
        "Authorization": "Bearer <value of MCP_BEARER_TOKEN>"
      }
    }
  }
}
```

## Out of scope

- **Provisioning EC2 capacity.** Bring your own Auto Scaling group / capacity
  provider; this template only schedules the task.
- **Public-by-default ingress.** Front the service with an **internal** ALB if
  you need stable DNS.
- **CDK / Terraform variants.** Native CloudFormation is the first reference.

## Reference

- [`AGENTS.md`](../../../AGENTS.md) — diagnostic socket UID, `CAP_SYS_PTRACE`, and WSL2 `perf` invariants
- [Issue #394](https://github.com/pedrosakuma/dotnet-diagnostics/issues/394) — Wave C cloud recipes

## Production: pin to a digest

The defaults use a released version tag
(`ghcr.io/pedrosakuma/dotnet-diagnostics:0.14.0`) rather than `:latest`. For
production go one step further and pin to a **content-addressable digest** so
the exact image bytes are immutable across replicas and rollbacks:

```bash
docker buildx imagetools inspect \
  ghcr.io/pedrosakuma/dotnet-diagnostics:0.14.0 \
  --format '{{json .Manifest}}' | jq -r .digest
# -> sha256:...
# Use ghcr.io/pedrosakuma/dotnet-diagnostics@sha256:<digest> in your parameters.
```

Mirror the digest into your private registry (ECR) for air-gapped or
pull-quota-limited environments.
