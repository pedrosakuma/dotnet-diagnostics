# .NET Diagnostics for VS Code

This desktop/workspace-host extension provides a live runtime-counters and GC-pauses panel, plus a
point-in-time CPU capture, backed by the Core-owned EventPipe/CPU-sampling sessions in
`dotnet-diagnostics-cli`. It does not collect diagnostics in TypeScript and does not use the MCP
server as a data transport.

## Prerequisites

- VS Code 1.96 or newer with a Node.js extension host.
- `dotnet-diagnostics-cli` built from this repository and a compatible .NET 10 runtime, or a
  self-contained CLI executable for the host platform.
- Permission to attach to the selected process's diagnostic IPC endpoint. The CLI must run as the
  same OS user as the target. Linux remote-process privilege requirements remain those documented
  by the CLI/Core.

Install the CLI explicitly; the extension never installs or updates tools:

```sh
dotnet tool install -g dotnet-diagnostics-cli
```

The extension adds a **.NET Diagnostics** icon to the Activity Bar. Open its **Live Diagnostics**
view and select **Start Live Counters**, or use the same command from the Command Palette. Choose a
discovered process, then use the panel's Stop/Start controls. One streaming session now carries
both the live counters chart and a **GC pauses** table (timestamp, generation, reason, type, pause
duration, plus a running pause count and total paused time) — both signals start and stop together.
The extension runs `dotnet-diagnostics-cli processes --json` for discovery and starts
`dotnet-diagnostics-cli stream --protocol jsonl` for live samples. Stdout is parsed only as protocol
frames. The CLI path can be set in the machine-scoped `dotnetDiagnostics.cliPath` setting; the
extension host must be able to execute it. Runtime, missing-tool, and protocol-version errors are
shown with actionable guidance.

Use **Capture CPU Now** (toolbar button, tree-view action, or Command Palette command
`dotnetDiagnostics.captureCpu`) to take a single point-in-time CPU sample of the selected process.
Unlike the counters/GC timeline, a capture is a one-shot snapshot: it reuses the panel's active
streaming connection if one is open, or opens (and then closes) a short-lived CLI connection
otherwise, and renders its own top-hotspot table (module, method, inclusive/exclusive sample
counts) that is replaced by the next capture rather than appended to a history. A capture does not
start or stop the live counters/GC session, and it is not compared against any baseline.

Live streaming requires a CLI build that supports `stream --protocol jsonl` and the multi-kind
`kinds`/`capture` protocol (CLI builds from this repository starting with #1099). A CLI installed
from an older package may still support process discovery but not live streaming; update it or
point `dotnetDiagnostics.cliPath` at a compatible executable. The extension fails with a concise
compatibility message instead of displaying CLI help output in the counters panel.


## Host support

The extension runs in VS Code desktop Node.js hosts on Windows, macOS, and Linux, including
workspace extension hosts in Remote SSH, WSL, and Dev Containers. The CLI must be installed and
runnable in that same workspace host environment. Web extension hosts are not supported because
they cannot spawn the CLI child process. Exact OS/architecture support follows the CLI's published
RID matrix.

## Development

```sh
npm install
npm test
```

Open this folder in VS Code and press F5 to launch an Extension Development Host. The package
manifest is scoped to the workspace/machine; no executable path can be supplied by workspace
settings.
