# Consumer installation guide

This page covers installing **dotnet-diagnostics-mcp** as an end user — no source clone, no .NET SDK on PATH (unless you pick the global-tool path), and no manual restart on crash / reboot.

> Looking for the contributor walkthrough (clone, build from source, share a single dev instance across multiple terminals)? See [README → Contributor setup](../README.md#contributor-setup) and `scripts/local-mcp.sh`.
>
> New to the MCP server itself? Start with [`client-setup.md`](./client-setup.md) for the top-level `--stdio` vs. HTTP onboarding choice, then return here for packaging and supervisor details.

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
> `inspect_process(view="runtime-config")`, `collect_thread_snapshot`,
> `capture_method_bytes`, `inspect_heap(source="live")`, and `get_bytes(kind="module")`
> against a live PID all require the `ptrace` bearer scope and OS permission. Most fail
> with `PermissionDenied` / `Could not PTRACE_ATTACH to any thread of the process N.`;
> `runtime-config` instead returns its non-ClrMD fields with an attach-failure note.
> Matching the target's UID is **not** enough on Debian/Ubuntu/WSL (default
> `kernel.yama.ptrace_scope=1`). See
> [§ 1.5 Linux: enabling live memory readers](#15-linux-enabling-live-memory-readers-kernel-ptrace)
> before wiring the server into a client. EventPipe-only tools work out of the box unless
> `collect_sample(kind="cpu", resolveMethodInstantiations=true)` explicitly enables its
> post-sample ClrMD enrichment.

### 1a. .NET global tool

```bash
dotnet tool install -g dotnet-diagnostics-mcp
export MCP_BEARER_TOKEN="$(openssl rand -hex 32)"  # or omit and copy the ephemeral token from the startup warning
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

If you intentionally omit `-e MCP_BEARER_TOKEN=...`, read the generated ephemeral token from `docker logs` before configuring the client.

Attaching to a **live local process** from inside the container requires UID parity + a shared `/tmp` mount — see [docs/local-docker-sidecar.md](./local-docker-sidecar.md) for the canonical walkthrough and the consolidated [Linux sidecar checklist](#14-linux-sidecar-checklist).

### 1c. Single-file binary

Grab the per-OS archive from the [GitHub Releases](https://github.com/pedrosakuma/dotnet-diagnostics/releases) page (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`), extract, and place `dotnet-diagnostics-mcp` on PATH.

```bash
tar -xzf dotnet-diagnostics-mcp-*-linux-x64.tar.gz -C ~/.local/bin
~/.local/bin/dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

### 1.4. Linux sidecar checklist

Use this table for every Docker / Kubernetes sidecar deployment before chasing attach failures:

| Requirement | Why it matters | Docker / local validation | Kubernetes |
|---|---|---|---|
| **Matching UID/GID (or `fsGroup`)** | The diagnostic socket at `/tmp/dotnet-diagnostic-<pid>` inherits the target app's identity; the sidecar must be able to open it. | Run the sidecar as the same UID as the target (the local sample walkthrough uses `--user 0`). | Set matching `runAsUser` / `runAsGroup`, or a pod-level `fsGroup`, for the app + sidecar. |
| **Shared `/tmp`** | Both containers must see the same diagnostic socket path. | Mount the same Docker volume at `/tmp` in the target and sidecar. | Mount the same `emptyDir` (or equivalent shared volume) at `/tmp` in both containers. |
| **Shared PID visibility** | The sidecar must be able to enumerate the target process and its PID. | Join the same PID namespace (`--pid=container:<anchor>` in the supported recipe). | Set `shareProcessNamespace: true` on the Pod. |
| **`DOTNET_EnableDiagnostics=1` on the target** | The .NET runtime must expose diagnostic IPC/EventPipe in the target process. | Leave the default on, or set `-e DOTNET_EnableDiagnostics=1` explicitly on the target container. | Set `env: { name: DOTNET_EnableDiagnostics, value: "1" }` on the target container. |
| **`CAP_SYS_PTRACE` for live memory readers** | ClrMD live attach (`inspect_heap(source="live")`, `collect_thread_snapshot`, `runtime-config`, live module bytes, optional CPU enrichment) needs ptrace on Linux with `ptrace_scope=1`. | Add `--cap-add SYS_PTRACE` to the **sidecar** when you need live memory readers. | Add `securityContext.capabilities.add: ["SYS_PTRACE"]` to the **sidecar** container when you need those tools. |

If a tool call still fails with `PermissionDenied` or `ServerNotAvailableException: Permission denied`, run `inspect_process(view="preflight", processId=<pid>)` as troubleshooting step 1. It checks the attach-related preconditions it can observe directly (especially UID / ptrace / perf readiness) and returns copy-pasteable remediation before you retry a more expensive collection; use this checklist for the shared `/tmp` and PID-namespace pieces.

### 1.5. Linux: enabling live memory readers (kernel ptrace)

These live-memory operations attach to the target via `ptrace(PTRACE_ATTACH, …)`:

- `inspect_process(view="runtime-config")`
- `collect_thread_snapshot`
- `capture_method_bytes` against a live PID
- `inspect_heap(source="live")`
- `get_bytes(kind="module")` against a live PID
- `collect_sample(kind="cpu", resolveMethodInstantiations=true)` (the optional post-sample enrichment only)

Linux's [Yama LSM](https://www.kernel.org/doc/Documentation/admin-guide/LSM/Yama.rst) defaults `kernel.yama.ptrace_scope=1` on Debian, Ubuntu, WSL, GitHub Codespaces, and most desktop distros — meaning **same-UID peer attach is blocked**. Most live readers report this as a structured `DiagnosticError`:

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N. Either the process has exited or you don't have permission." } }
```

First decide whether the investigation actually needs a live memory reader. EventPipe collectors
such as counters, GC, exceptions, contention, CPU, and allocation use the diagnostic IPC socket
and do **not** need Linux ptrace permission (unless CPU sampling explicitly enables
`resolveMethodInstantiations=true`). Offline dump analysis also avoids live attach.

`inspect_process(view="runtime-config")` is deliberately best-effort after its
`ptrace` authorization check: it returns filtered environment / runtimeconfig data and
records the failed ClrMD attach in `notes[]`.

When live attach is required, choose the narrowest permission boundary available:

| Environment | Recipe | Permission reach |
|---------------------|----------------------------------------------------------------------------------------------|------------------------|
| **Container (Docker / Podman)** | Add `--cap-add SYS_PTRACE` to the `docker run` command. | Sidecar container only. |
| **Container in compose** | Add `cap_add: [SYS_PTRACE]` to the service. The shipped [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) already does this. | Service only. |
| **Kubernetes** | `securityContext.capabilities.add: ["SYS_PTRACE"]` on the **sidecar** container. The shipped [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml) already does this. | Sidecar only. |
| **CLI-launched child on `ptrace_scope=1`** | Use `dotnet-diagnostics-cli … --launch -- <app> [args]`; the CLI is the target's parent. See the [`--launch` CLI guidance](./cli-reference.md#linux-ptrace-note). | That descendant only; no added capability or sysctl change. |
| **Global tool / single-file binary on an isolated personal-development host** | `sudo sysctl -w kernel.yama.ptrace_scope=0`<br/>Persist only on such a machine with `echo 'kernel.yama.ptrace_scope = 0' \| sudo tee /etc/sysctl.d/10-ptrace.conf`. | **Host-wide**; weakens isolation between all same-UID processes. |

> **Canonical security note on `ptrace_scope=0`.** This host-wide setting lets any process
> owned by your UID attach to any other process owned by your UID. Use it only on an isolated
> personal-development workstation or Codespace where you accept that reduced isolation.
> **Do not set or persist it on shared, multi-user, CI-runner, staging, or production hosts.**
> For deployed environments, keep the host policy intact and scope `CAP_SYS_PTRACE` to the
> diagnostics sidecar. If that capability is not acceptable, use EventPipe or offline analysis.

You can verify the current Yama policy with `cat /proc/sys/kernel/yama/ptrace_scope` — `0`
allows the attach, `1` is "scope to children", `2` is "admin-only", and `3` is "no
attach". Anything above `0` blocks these same-UID peer attaches unless the process has the
required kernel capability.

To avoid live ptrace entirely, use the dump-based workflow:

```text
collect_process_dump  (runs inside the target process — no ptrace needed)
   ↓
inspect_heap(source="dump")          (offline analysis — no live attach)
```

`collect_process_dump` writes through the diagnostic IPC socket. It needs UID/socket access,
not Linux `CAP_SYS_PTRACE`; the capture happens inside the target runtime. MCP authorization is
a separate boundary: the server still requires the bearer scopes `dump-write` + `ptrace` and
human approval (`confirm=true` or MCP elicitation) before writing a dump.

For a target you can launch yourself, the standalone CLI provides another no-added-privilege
option under `ptrace_scope=1`:

```bash
dotnet-diagnostics-cli inspect-heap --launch --acknowledge-risk high -- dotnet App.dll
```

This works because the CLI is the target's parent. It does not bypass `ptrace_scope=2` or `3`;
use offline analysis there when additional privilege is unavailable.


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
# Auth (required for non-loopback): scoped bearer tokens preferred; MCP_BEARER_TOKEN accepted but deprecated
export Auth__BearerTokens__0__Name="agent"
export Auth__BearerTokens__0__Token="$(openssl rand -hex 32)"
export Auth__BearerTokens__0__Scopes__0="read-counters"

# Direct TLS example (container or bare host):
export MCP_TLS_CERTIFICATE_PEM="$(cat cert.pem)"
export MCP_TLS_PRIVATE_KEY_PEM="$(cat key.pem)"
dotnet-diagnostics-mcp --urls https://0.0.0.0:8787

# Trusted proxy example (same Auth__BearerTokens__* set above):
export MCP_TRUSTED_PROXY_CIDRS="10.0.0.0/8"
dotnet-diagnostics-mcp --urls http://0.0.0.0:8787  # proxy sets X-Forwarded-Proto: https
```

`read-counters` is sufficient for process discovery, triage, and counters.
Add only the scopes required by the intended diagnostics; see
[`authorization.md`](./authorization.md).

`/health` always responds regardless of scheme (needed for readiness probes).
See [`client-setup.md` → Transport security](./client-setup.md#transport-security-non-loopback) for the complete reference.
---

## 2. Run it as a supervised service

The server is stateless and resumable but you don't want to remember to restart it after every reboot or crash. The repo ships supervisor templates under [`deploy/supervisors/`](../deploy/supervisors).

All three local supervisors create the same least-privilege bearer entry:

| Setting | Default |
|---|---|
| Principal name | `local-observer` |
| Scope | `read-counters` |
| Effective access | Process discovery, `inspect_process(view="triage")`, and numeric counters |

The environment keys use ASP.NET Core's array syntax exactly:
`Auth__BearerTokens__0__Name`, `Auth__BearerTokens__0__Token`, and
`Auth__BearerTokens__0__Scopes__0`. Double underscores map configuration
sections and numeric segments map array indexes. Do not replace them with
single underscores or skip indexes.

### Linux — systemd `--user`

```bash
unit="$HOME/.config/systemd/user/dotnet-diagnostics-mcp.service"
token="$(openssl rand -hex 32)"
mkdir -p ~/.config/systemd/user
curl -fsSL https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/deploy/supervisors/linux/dotnet-diagnostics-mcp.service \
  -o "$unit"
chmod 600 "$unit"
sed -i "s|{{AUTH_BEARER_TOKEN}}|$token|g" "$unit"
systemctl --user daemon-reload
systemctl --user enable --now dotnet-diagnostics-mcp.service

# Optional — keep the unit running after logout:
loginctl enable-linger "$USER"
```

Status: `systemctl --user status dotnet-diagnostics-mcp`. Logs: `journalctl --user -u dotnet-diagnostics-mcp -f`.
The generated secret remains in `$token` for client setup in the same shell and is not
printed by these commands.

### Windows — Scheduled Task

```powershell
dotnet tool install -g dotnet-diagnostics-mcp
$installer = Join-Path $env:USERPROFILE 'Install-DotnetDiagnosticsMcp.ps1'
Invoke-WebRequest `
  'https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/deploy/supervisors/windows/Install-Service.ps1' `
  -OutFile $installer
& $installer -Port 8787
```

The script registers a Scheduled Task that starts at logon and restarts on failure 5
times at 30-second intervals. It stores the indexed `Auth__BearerTokens__0__*`
configuration in the current user's environment, removes the value left by the legacy
installer, and does not print the generated secret. Non-secret settings (port, task
name, principal name, and scopes) are retained in
`%LOCALAPPDATA%\dotnet-diagnostics-mcp\install-state.json`; the token is not written
there. Load it for client setup without printing it:

```powershell
$token = [Environment]::GetEnvironmentVariable(
    'Auth__BearerTokens__0__Token',
    'User')
$state = Get-Content "$env:LOCALAPPDATA\dotnet-diagnostics-mcp\install-state.json" |
  ConvertFrom-Json
Start-ScheduledTask -TaskName $state.TaskName
```

> 🔒 **Need off-CPU sampling on Windows?** `collect_sample(kind="off_cpu")` uses the NT Kernel
> Logger's `ContextSwitch` provider, which requires Administrator membership or
> `SeSystemProfilePrivilege` — neither is held by the per-user Scheduled Task. For
> production sidecar deployments that want off-CPU, see
> [`windows-sidecar-service.md`](./windows-sidecar-service.md) (Windows Service install with
> `LocalSystem` or a dedicated least-privilege service account). The Scheduled Task's
> Windows principal can run the other collectors without that OS
> privilege, but the default bearer remains limited to `read-counters`; grant the required
> scopes deliberately before invoking them.

### macOS — launchd `LaunchAgent`

```bash
plist="$HOME/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist"
token="$(openssl rand -hex 32)"
mkdir -p ~/Library/LaunchAgents
curl -fsSL https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/deploy/supervisors/macos/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist \
  -o "$plist"
chmod 600 "$plist"
sed -i '' "s|{{HOME}}|$HOME|g; s|{{AUTH_BEARER_TOKEN}}|$token|g" "$plist"
launchctl bootstrap gui/$UID "$plist"
launchctl enable gui/$UID/io.github.pedrosakuma.dotnet-diagnostics-mcp
```

The generated secret remains in `$token` for client setup in the same shell and is not
printed by these commands.

### Scope expansion

The observer default intentionally cannot use `inspect_process(view="runtime-config")`,
start EventPipe samples, read heaps, attach with ptrace, write dumps, or export
investigations. Add scopes only for a concrete workflow after reviewing
[`authorization.md`](./authorization.md):

```ini
# Linux unit: append consecutive scope indexes under [Service], then daemon-reload + restart.
Environment=Auth__BearerTokens__0__Scopes__1=eventpipe
Environment=Auth__BearerTokens__0__Scopes__2=investigation-export
```

```bash
# macOS plist: add consecutive keys, then bootout + bootstrap the LaunchAgent.
/usr/libexec/PlistBuddy -c \
  "Add :EnvironmentVariables:Auth__BearerTokens__0__Scopes__1 string eventpipe" \
  "$plist"
launchctl bootout gui/$UID/io.github.pedrosakuma.dotnet-diagnostics-mcp
launchctl bootstrap gui/$UID "$plist"
```

```powershell
# Windows: add scopes while preserving the token, port, task name, and existing scopes.
$installer = Join-Path $env:USERPROFILE 'Install-DotnetDiagnosticsMcp.ps1'
& $installer -AddScopes @('eventpipe', 'investigation-export')
$state = Get-Content "$env:LOCALAPPDATA\dotnet-diagnostics-mcp\install-state.json" |
  ConvertFrom-Json
Start-ScheduledTask -TaskName $state.TaskName
```

Do not grant `ptrace`, `heap-read`, `dump-write`, or modifier scopes merely to make a
failed call pass. Each expands data exposure or target impact; grant the exact
combination documented for the intended tool.

### Rotate

Rotation changes only the `Token` value; keep the principal name and scopes stable,
restart the supervisor, and update every client `Authorization` header before deleting
your old client-side copy.

```bash
# Linux
unit="$HOME/.config/systemd/user/dotnet-diagnostics-mcp.service"
token="$(openssl rand -hex 32)"
sed -i -E "s|^Environment=Auth__BearerTokens__0__Token=.*$|Environment=Auth__BearerTokens__0__Token=$token|" "$unit"
systemctl --user daemon-reload
systemctl --user restart dotnet-diagnostics-mcp.service
```

```bash
# macOS
plist="$HOME/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist"
token="$(openssl rand -hex 32)"
launchctl bootout gui/$UID/io.github.pedrosakuma.dotnet-diagnostics-mcp
/usr/libexec/PlistBuddy -c \
  "Set :EnvironmentVariables:Auth__BearerTokens__0__Token $token" "$plist"
launchctl bootstrap gui/$UID "$plist"
```

```powershell
# Windows: rotate only the token; retained settings remain unchanged.
$installer = Join-Path $env:USERPROFILE 'Install-DotnetDiagnosticsMcp.ps1'
& $installer -RotateToken
$token = [Environment]::GetEnvironmentVariable(
    'Auth__BearerTokens__0__Token',
    'User')
$state = Get-Content "$env:LOCALAPPDATA\dotnet-diagnostics-mcp\install-state.json" |
  ConvertFrom-Json
Start-ScheduledTask -TaskName $state.TaskName
```

### Uninstall

```bash
# Linux
systemctl --user disable --now dotnet-diagnostics-mcp.service
rm ~/.config/systemd/user/dotnet-diagnostics-mcp.service
systemctl --user daemon-reload
```

```bash
# macOS
launchctl bootout gui/$UID/io.github.pedrosakuma.dotnet-diagnostics-mcp
rm ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist
```

```powershell
# Windows: also removes the generated launcher and user-scope auth variables.
$installer = Join-Path $env:USERPROFILE 'Install-DotnetDiagnosticsMcp.ps1'
& $installer -Uninstall
```

Remove the matching bearer from each MCP client configuration after uninstalling.

### Troubleshooting

| Symptom | Check |
|---|---|
| `401 Unauthorized` after install or rotation | The client header must use the current bearer token. Reload the client after replacing the value. |
| Server reports no configured bearer | Confirm all three indexed variables exist and that the token placeholder was replaced. On Linux, `grep -q '{{AUTH_BEARER_TOKEN}}' "$unit"` must fail. On macOS, `/usr/libexec/PlistBuddy -c 'Print :EnvironmentVariables:Auth__BearerTokens__0__Token' "$plist" >/dev/null` must succeed without printing the secret. On Windows, rerun the installer. |
| Counters work but a capture is denied | This is the expected `read-counters` boundary. Follow [Scope expansion](#scope-expansion) instead of switching to a root-like token. |
| Rotation appears stale | Restart the supervisor and the MCP client. The Windows launcher reloads user-scope values from the registry on every task start; use `-RotateToken`, not a fresh install command. |
| Windows update mode says no retained state exists | Run the current installer normally once to migrate the task and create the non-secret state file, then retry `-RotateToken` or `-AddScopes`. |

Existing `MCP_BEARER_TOKEN` deployments remain compatible as documented in
[`authorization.md` → Backward compatibility](./authorization.md#backward-compatibility),
but that variable resolves to the deprecated synthetic `legacy-root` principal. New
supervisor installs must use the named scoped configuration above.

On Windows, running the current installer normally over a legacy task performs a
one-time migration and creates retained non-secret state. A legacy
`MCP_BEARER_TOKEN` is replaced with a new scoped observer token, so update the client
header. If the old task used a custom port or task name, pass those values on this
one-time migration. An already-scoped `Auth__BearerTokens__0__*` entry is preserved.
Later normal invocations preserve the existing token and retained settings unless
their corresponding parameters are explicitly supplied; `-RotateToken` and
`-AddScopes` are the safer purpose-specific workflows.

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

Set the header value to `Bearer ` followed by the `$token` value retained by the
installation command. If that shell is gone, load the secret without printing it:

```bash
# Linux
token="$(sed -n 's/^Environment=Auth__BearerTokens__0__Token=//p' \
  ~/.config/systemd/user/dotnet-diagnostics-mcp.service)"

# macOS
token="$(/usr/libexec/PlistBuddy -c \
  'Print :EnvironmentVariables:Auth__BearerTokens__0__Token' \
  ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist)"
```

```powershell
# Windows
$token = [Environment]::GetEnvironmentVariable(
    'Auth__BearerTokens__0__Token',
    'User')
```

Do not paste the token into issue text, command output, or logs.


---

## 4. First diagnostic and safety orientation

### First diagnostic (low-risk)

Use the same canonical bootstrap sequence described in [`tool-reference.md`](./tool-reference.md#inspect_process):

1. `inspect_process(view="list")` — confirm connectivity and discover candidate PIDs.
2. `inspect_process(view="capabilities", processId=<pid>)` — confirm CoreCLR vs. NativeAOT and runtime gates on the PID you chose.
3. `inspect_process(view="triage", processId=<pid>)` — get an evidence-backed health snapshot before choosing a deeper collector.

Shortcut rules are explicit:

- If you already know the PID, skip straight to `capabilities`.
- If exactly one .NET process is visible, direct tool calls can auto-resolve it.
- If a tool call fails with `PermissionDenied` or `ServerNotAvailableException: Permission denied`, run `inspect_process(view="preflight", processId=<pid>)` first; it diagnoses UID, ptrace, perf, and other sidecar prerequisites before you pay for another failed collect.

```jsonc
// MCP call (from your client after connecting)
{ "name": "inspect_process", "arguments": { "view": "list" } }
```

With the CLI:

```bash
dotnet-diagnostics-cli processes
```

If the call returns process rows, the server is working. Follow the sequence above, then move to `inspect_process(view="triage")`
on a target PID for an evidence-backed health snapshot. The response includes:

- `assessment` — overall verdict: `healthy`, `degraded`, `critical`, or `inconclusive`
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
- **Critical** — includes process dumps, method-parameter capture, raw artifact export, and
  sensitive-value drilldowns; see the safety matrix for the complete conditional set. Uses MCP elicitation when the
  client advertises the `elicitation` capability (preferred). Without elicitation:
  most critical tools return `safetyApproval.requiredAcknowledgement` for retry (same
  protocol as high-risk); `collect_process_dump` keeps its `confirm=true` fallback.
  CLI callers must pass `--acknowledge-risk critical` (and `--confirm` for dumps). See
  [`authorization.md`](./authorization.md#per-call-confirmation).

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

That same flag is what the systemd `ExecStartPost` and the container `HEALTHCHECK`
invoke under the hood. The Windows installer prints the equivalent manual probe.
