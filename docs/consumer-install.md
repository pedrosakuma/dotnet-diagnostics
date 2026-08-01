# Consumer installation guide

This page covers installing **dotnet-diagnostics-mcp** as an end user — no source clone, no .NET SDK on PATH (unless you pick the global-tool path), and no manual restart on crash / reboot.

> Looking for the contributor walkthrough (clone, build from source, share a single dev instance across multiple terminals)? See [README → Contributor setup](../README.md#contributor-setup) and `scripts/local-mcp.sh`.

---

## 1. Pick a distribution

| Distribution            | When to use it                                                                                        | Requires             |
|-------------------------|-------------------------------------------------------------------------------------------------------|----------------------|
| **.NET global tool**    | You already have .NET 10 SDK installed and want a managed install + upgrade path (`dotnet tool update`). | .NET 10 SDK          |
| **Container image**     | You want everything (sidecar parity with K8s, predictable filesystem, `--restart unless-stopped`).    | Docker / Podman      |
| **Single-file binary**  | You want zero runtime dependencies — drop one file on PATH and go.                                    | Nothing              |

All three publish the same MCP surface (Streamable HTTP, bearer-token authenticated, `/health` allow-listed).

> **Instrumentation boundary.** Standard EventPipe and ClrMD diagnostics need no target code
> changes or prior instrumentation. `collect_sample(kind="method-params")` is the explicit
> exception: it performs a privileged dynamic attach of vendored dotnet-monitor profilers and
> a startup hook to the running process, then ReJIT-instruments only the requested method
> allowlist. It is disabled
> by default and requires `Diagnostics:AllowMethodParameterCapture=true`, the literal
> `sensitive-parameter-read` scope, and `includeSensitiveValues=true`. The standalone CLI and
> BenchmarkDotNet diagnoser do not expose this capability.

> 🐧 **Linux heads-up — live memory readers need kernel ptrace permission.**
> `collect_thread_snapshot`, `capture_method_bytes`, `inspect_heap(source="live")`, and
> `get_bytes(kind="module")` against a live PID will fail with `PermissionDenied` /
> `Could not PTRACE_ATTACH to any thread of the process N.` unless the diagnostics host may
> attach. Matching the target's UID is **not** enough on Debian/Ubuntu/WSL (default
> `kernel.yama.ptrace_scope=1`). See
> [§ 1.5 Linux: enabling live memory readers](#15-linux-enabling-live-memory-readers-kernel-ptrace)
> before wiring the server into a client. EventPipe-only tools work out of the box unless
> `collect_sample(kind="cpu", resolveMethodInstantiations=true)` explicitly enables its
> post-sample ClrMD enrichment.

### 1a. .NET global tool

```bash
dotnet tool install -g dotnet-diagnostics-mcp
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

Upgrade: `dotnet tool update -g dotnet-diagnostics-mcp`. Uninstall: `dotnet tool uninstall -g dotnet-diagnostics-mcp`.

<details>
<summary><strong>Migrating from the legacy package id (v0.2.2)</strong></summary>

The NuGet package id was `DotnetDiagnostics.Mcp` for 0.1.0–0.2.1 and is now
`dotnet-diagnostics-mcp` (matches the tool command and the sibling `dotnet-assembly-mcp`).
If you have the old id installed, run `dotnet tool uninstall -g DotnetDiagnostics.Mcp` first,
then install the new one. The legacy id has been unlisted on NuGet.org.

</details>

### 1b. Container

> **Local dev only — internal cleartext.** The container image sets `ASPNETCORE_URLS=http://0.0.0.0:8080`,
> which is a non-loopback cleartext binding. `MCP_ALLOW_INSECURE_HTTP=true` is required to start;
> `-p 127.0.0.1:8787:8080` restricts host-side access to loopback. Do not use this recipe for
> production — configure TLS or a trusted proxy instead (see [§ 1.6](#16-transport-security-for-non-loopback-listeners)).

```bash
docker run -d \
  --name dotnet-diagnostics-mcp \
  --restart unless-stopped \
  -p 127.0.0.1:8787:8080 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  -e MCP_ALLOW_INSECURE_HTTP=true \
  ghcr.io/pedrosakuma/dotnet-diagnostics:latest
```

Attaching to a **live local process** from inside the container requires UID parity + a shared `/tmp` mount — see [docs/local-docker-sidecar.md](./local-docker-sidecar.md) for the canonical walkthrough.

### 1c. Single-file binary

Grab the per-OS archive from the [GitHub Releases](https://github.com/pedrosakuma/dotnet-diagnostics/releases) page (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`), extract, and place `dotnet-diagnostics-mcp` on PATH.

```bash
tar -xzf dotnet-diagnostics-mcp-*-linux-x64.tar.gz -C ~/.local/bin
~/.local/bin/dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

### 1.5. Linux: enabling live memory readers (kernel ptrace)

Four live-memory operations attach to the target via `ptrace(PTRACE_ATTACH, …)`:

- `collect_thread_snapshot`
- `capture_method_bytes` against a live PID
- `inspect_heap(source="live")`
- `get_bytes(kind="module")` against a live PID
- `collect_sample(kind="cpu", resolveMethodInstantiations=true)` (the optional post-sample enrichment only)

Linux's [Yama LSM](https://www.kernel.org/doc/Documentation/admin-guide/LSM/Yama.rst) defaults `kernel.yama.ptrace_scope=1` on Debian, Ubuntu, WSL, GitHub Codespaces, and most desktop distros — meaning **same-UID peer attach is blocked**. The MCP server reports this as a structured `DiagnosticError`:

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N. Either the process has exited or you don't have permission." } }
```

Pick the recipe that matches your distribution:

| Distribution        | Recipe                                                                                       | Permission reach       |
|---------------------|----------------------------------------------------------------------------------------------|------------------------|
| **Global tool / single-file binary** (running on the host) | `sudo sysctl -w kernel.yama.ptrace_scope=0`<br/>Persist with `echo 'kernel.yama.ptrace_scope = 0' \| sudo tee /etc/sysctl.d/10-ptrace.conf`. | Host-wide (relaxes a security default — see note below). |
| **Container (Docker / Podman)** | Add `--cap-add SYS_PTRACE` to the `docker run` command. | Sidecar container only. |
| **Container in compose** | Add `cap_add: [SYS_PTRACE]` to the service. The shipped [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) already does this. | Service only. |
| **Kubernetes** | `securityContext.capabilities.add: ["SYS_PTRACE"]` on the **sidecar** container. The shipped [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml) already does this. | Sidecar only. |

> **Security note on `ptrace_scope=0`.** This is the historical Linux default and is appropriate for personal dev workstations / Codespaces. It lets any process owned by your UID attach to any other process owned by your UID — which is precisely what the diagnostics server needs. On a shared host or anything close to production, prefer the container/K8s recipes (capability scoped to the sidecar) over relaxing the host setting.

You can verify the current Yama policy with `cat /proc/sys/kernel/yama/ptrace_scope` — `0`
allows the attach, `1` is "scope to children", `2` is "admin-only", and `3` is "no
attach". Anything above `0` blocks these same-UID peer attaches unless the process has the
required kernel capability.

To dodge the requirement entirely, use the dump-based workflow:

```text
collect_process_dump  (runs inside the target process — no ptrace needed)
   ↓
inspect_heap(source="dump")          (offline analysis — no live attach)
```

`collect_process_dump` writes through the diagnostic IPC socket. It needs UID/socket access,
not Linux `CAP_SYS_PTRACE`; the capture happens inside the target runtime. MCP authorization is
a separate boundary: the server still requires the bearer scopes `dump-write` + `ptrace` and
human approval (`confirm=true` or MCP elicitation) before writing a dump.


---

### 1.6. Transport security for non-loopback listeners

The server **refuses to start** when bound to a non-loopback address over cleartext HTTP
without a configured trusted TLS terminator or the explicit unsafe override. For **local
development**, always bind to loopback (`http://127.0.0.1:<port>`) — the examples in this
guide already do this.

For **non-loopback deployments** (sidecar, shared host, Kubernetes), choose one:

| Approach | How | When |
|---|---|---|
| **Direct Kestrel TLS** | Set `MCP_TLS_CERTIFICATE_PEM` + `MCP_TLS_PRIVATE_KEY_PEM` and bind to `https://` | You own the cert lifecycle; no proxy in front |
| **TLS-terminating proxy** | Set `MCP_TRUSTED_PROXY_CIDRS` to the proxy IPs/CIDRs; proxy forwards `X-Forwarded-Proto: https` | nginx, Envoy, service mesh, or cloud load-balancer already handles TLS |
| **Loopback only** | Bind to `http://127.0.0.1:<port>` | Single-host / sidecar where the MCP client is on the same host |
| **Dev override (⚠️ unsafe)** | Set `MCP_ALLOW_INSECURE_HTTP=true` | Local multi-container stacks where TLS setup is impractical — emits a warning on every start |

```bash
# Direct TLS example (container or bare host):
export MCP_TLS_CERTIFICATE_PEM="$(cat cert.pem)"
export MCP_TLS_PRIVATE_KEY_PEM="$(cat key.pem)"
dotnet-diagnostics-mcp --urls https://0.0.0.0:8787

# Trusted proxy example:
export MCP_TRUSTED_PROXY_CIDRS="10.0.0.0/8"
dotnet-diagnostics-mcp --urls http://0.0.0.0:8787  # proxy sets X-Forwarded-Proto: https
```

`/health` always responds regardless of scheme (needed for readiness probes).
See [`client-setup.md` → Transport security](./client-setup.md#transport-security-non-loopback) for the complete reference.
---

## 2. Run it as a supervised service

The server is stateless and resumable but you don't want to remember to restart it after every reboot or crash. The repo ships supervisor templates under [`deploy/supervisors/`](../deploy/supervisors).

### Linux — systemd `--user`

```bash
mkdir -p ~/.config/systemd/user
curl -sSL https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics-mcp/main/deploy/supervisors/linux/dotnet-diagnostics-mcp.service \
  -o ~/.config/systemd/user/dotnet-diagnostics-mcp.service
# Edit the Environment=MCP_BEARER_TOKEN line before enabling.
$EDITOR ~/.config/systemd/user/dotnet-diagnostics-mcp.service
systemctl --user daemon-reload
systemctl --user enable --now dotnet-diagnostics-mcp.service

# Optional — keep the unit running after logout:
loginctl enable-linger "$USER"
```

Status: `systemctl --user status dotnet-diagnostics-mcp`. Logs: `journalctl --user -u dotnet-diagnostics-mcp -f`.

### Windows — Scheduled Task

```powershell
dotnet tool install -g dotnet-diagnostics-mcp
# Then run the supervisor script (downloaded from the release page or repo):
.\deploy\supervisors\windows\Install-Service.ps1 -Port 8787
```

The script registers a Scheduled Task that starts at logon, restarts on failure 5 times at 30s intervals, and publishes the bearer token as a user-scope environment variable.

> 🔒 **Need off-CPU sampling on Windows?** `collect_sample(kind="off_cpu")` uses the NT Kernel
> Logger's `ContextSwitch` provider, which requires Administrator membership or
> `SeSystemProfilePrivilege` — neither is held by the per-user Scheduled Task. For
> production sidecar deployments that want off-CPU, see
> [`windows-sidecar-service.md`](./windows-sidecar-service.md) (Windows Service install with
> `LocalSystem` or a dedicated least-privilege service account). Every other tool
> (counters, CPU sampling, exceptions, GC, EventSources, ETW NativeAOT CPU sampling) works
> from the Scheduled Task without changes.

Uninstall: `Unregister-ScheduledTask -TaskName 'dotnet-diagnostics-mcp' -Confirm:$false`.

### macOS — launchd `LaunchAgent`

```bash
cp deploy/supervisors/macos/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist \
  ~/Library/LaunchAgents/
sed -i '' "s|{{HOME}}|$HOME|g; s|{{MCP_BEARER_TOKEN}}|$(openssl rand -hex 32)|g" \
  ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist
launchctl bootstrap gui/$UID ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist
launchctl enable gui/$UID/io.github.pedrosakuma.dotnet-diagnostics-mcp
```

### Container (already covered)

The `--restart unless-stopped` flag in the `docker run` recipe above is the resilience story for the container path. The image also defines a `HEALTHCHECK` that invokes `dotnet-diagnostics-mcp --health-check`.

---

## 3. Wire it into your MCP client

Add this to your `mcp-config.json` (Claude Desktop, Claude Code, Copilot CLI, Cursor — same shape, slightly different file location):

```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": {
        "Authorization": "Bearer $MCP_BEARER_TOKEN"
      }
    }
  }
}
```


---

## 4. First diagnostic and safety orientation

### First diagnostic (low-risk)

`inspect_process(view="list")` returns a list of running .NET processes with their PIDs and
capabilities — no EventPipe session, no ptrace, no side effects. It is always the first call
to confirm connectivity and discover what is running:

```jsonc
// MCP call (from your client after connecting)
{ "name": "inspect_process", "arguments": { "view": "list" } }
```

With the CLI:

```bash
dotnet-diagnostics-cli processes
```

If the call returns process rows, the server is working. Move to `inspect_process(view="triage")`
on a target PID for an evidence-backed health snapshot. The response includes:

- `assessment` — overall verdict: `ok`, `warning`, `critical`, or `inconclusive`
- `observedSignals` — individual threshold crossings with evidence items
- `hypotheses` — bounded interpretations with supporting and contradicting evidence and a suggested next step
- `topIndicators` — scored signals ranked by severity

`observe`, `investigate`, and `privileged-response` are **operating profiles** (deployment and
workflow recommendations), not signal categories. See
[`production-safety.md`](./production-safety.md#production-operating-profiles).

### Safety levels and acknowledgement

Operations are classified **low / moderate / high / critical** based on target impact, data
exposure, and side effects. Most observation stays at low or moderate and requires no
acknowledgement:

- **Low** — aggregate signals, process lists, capabilities, counters. Executes immediately.
- **Moderate** — bounded EventPipe collection. Executes with a `safetyWarnings` notice.
- **High** — heap walks, thread snapshots, induced GC. Pauses before side effects and returns
  `safetyApproval.requiredAcknowledgement`; retry with that exact value to proceed.
- **Critical** — process dumps, method-parameter capture. Requires MCP elicitation (or
  `confirm=true` for `collect_process_dump`). CLI callers must pass `--acknowledge-risk critical`.

Use `--explain-risk` with any CLI command to inspect the resolved risk level without executing:

```bash
dotnet-diagnostics-cli inspect-heap --pid 1234 --explain-risk
```

The canonical operation matrix with all risk levels and approvals is
[`production-safety.md`](./production-safety.md).

### Evidence disposal orientation

Diagnostic data can contain PII, credentials, stack frames, heap values, and
application-controlled text. Before collecting anything beyond counters:

1. Confirm who needs access to the evidence and where it will be stored.
2. Set a retention deadline before collection, not after.
3. Delete MCP client chat history, exported summaries, and redirected CLI output when no
   longer needed. Server-side handle expiry does not delete copies the client saved.
4. Treat dumps and raw traces as privileged material — restrict access to the incident team.

Full retention and disposal guidance:
[`production-safety.md` → Retention, access, and disposal](./production-safety.md#retention-access-and-disposal).

---

## 5. Optional — pair with `dotnet-assembly-mcp`

The diagnostics server resolves PDBs locally and stamps `SourceLocation` directly onto every `MethodIdentity` it emits for CPU samples (see [#28](https://github.com/pedrosakuma/dotnet-diagnostics/issues/28)). That means **in a dev environment** where the source tree is open in your editor, `dotnet-diagnostics-mcp` alone is enough to follow a hotspot to its source line.

The partner [`pedrosakuma/dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp) remains the right call for:

- Stripped binaries / NativeAOT (no PDB, no inline source).
- Third-party assemblies you don't have source for.
- Decompilation (`decompile_method`) and call-graph queries (`find_callers`).

When you want it, install side-by-side on a distinct port:

```bash
dotnet tool install -g dotnet-assembly-mcp
dotnet-assembly-mcp --urls http://127.0.0.1:8788
```

And add a second entry to `mcp-config.json`:

```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": { "Authorization": "Bearer $MCP_BEARER_TOKEN" }
    },
    "dotnet-assembly": {
      "type": "http",
      "url": "http://127.0.0.1:8788/mcp",
      "headers": { "Authorization": "Bearer $MCP_BEARER_TOKEN" }
    }
  }
}
```

---

## 6. Verify

The CLI bundles a probe-only mode that exits 0 on a healthy 200 response from `/health` and 1 on any failure:

```bash
dotnet-diagnostics-mcp --health-check --urls http://127.0.0.1:8787
```

That same flag is what the systemd `ExecStartPost`, the Scheduled Task readiness gate, and the container `HEALTHCHECK` invoke under the hood.
