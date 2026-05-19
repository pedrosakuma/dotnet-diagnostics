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

### 1a. .NET global tool

```bash
dotnet tool install -g DotnetDiagnosticsMcp.Server
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

Upgrade: `dotnet tool update -g DotnetDiagnosticsMcp.Server`. Uninstall: `dotnet tool uninstall -g DotnetDiagnosticsMcp.Server`.

### 1b. Container

```bash
docker run -d \
  --name dotnet-diagnostics-mcp \
  --restart unless-stopped \
  -p 127.0.0.1:8787:8787 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest
```

Attaching to a **live local process** from inside the container requires UID parity + a shared `/tmp` mount — see [docs/local-docker-sidecar.md](./local-docker-sidecar.md) for the canonical walkthrough.

### 1c. Single-file binary

Grab the per-OS archive from the [GitHub Releases](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/releases) page (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`), extract, and place `dotnet-diagnostics-mcp` on PATH.

```bash
tar -xzf dotnet-diagnostics-mcp-*-linux-x64.tar.gz -C ~/.local/bin
~/.local/bin/dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

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
dotnet tool install -g DotnetDiagnosticsMcp.Server
# Then run the supervisor script (downloaded from the release page or repo):
.\deploy\supervisors\windows\Install-Service.ps1 -Port 8787
```

The script registers a Scheduled Task that starts at logon, restarts on failure 5 times at 30s intervals, and publishes the bearer token as a user-scope environment variable.

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

## 4. Optional — pair with `dotnet-assembly-mcp`

After [#28](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/28) shipped, the diagnostics server resolves PDBs locally and stamps `SourceLocation` directly onto every `MethodIdentity`. That means **in a dev environment** where the source tree is open in your editor, `dotnet-diagnostics-mcp` alone is enough to follow a hotspot to its source line.

The partner [`pedrosakuma/dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp) remains the right call for:

- Stripped binaries / NativeAOT (no PDB, no inline source).
- Third-party assemblies you don't have source for.
- Decompilation (`decompile_method`) and call-graph queries (`find_callers`).

When you want it, install side-by-side on a distinct port:

```bash
dotnet tool install -g DotnetAssemblyMcp.Server
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

## 5. Verify

The CLI bundles a probe-only mode that exits 0 on a healthy 200 response from `/health` and 1 on any failure:

```bash
dotnet-diagnostics-mcp --health-check --urls http://127.0.0.1:8787
```

That same flag is what the systemd `ExecStartPost`, the Scheduled Task readiness gate, and the container `HEALTHCHECK` invoke under the hood.
