# .NET Diagnostics for VS Code

Investigate .NET performance problems without leaving VS Code: watch runtime counters and GC
pauses, find CPU hotspots, inspect heap and thread snapshots, and analyze existing dumps.

## What you can do

- **Watch a running app:** live counters and GC pauses in one panel.
- **Capture a problem:** CPU, heap, threads, exceptions, allocations, contention and more.
- **Keep useful evidence:** save supported snapshots and reopen them after the app has stopped.
- **Share and compare:** export redacted measurements, save raw CPU traces locally, and compare
  compatible saved captures against a baseline.

## Get started

Requires VS Code **1.96+** and the **.NET 10 runtime**, or a self-contained CLI. Install
`dotnet-diagnostics-cli` **0.29.0+** in the environment where VS Code runs your workspace:

```sh
dotnet tool install -g dotnet-diagnostics-cli
```

Already installed? Run `dotnet tool update -g dotnet-diagnostics-cli`. The extension never
installs or updates the CLI automatically. Newer capture and export features may require a
more recent CLI build; unsupported features show update guidance.

1. Open **.NET Diagnostics** in the Activity Bar.
2. Run **Check Environment**, then select your app in **.NET Processes**.
3. Choose **Start Live Counters**, or capture a snapshot while reproducing the problem.

For a guided introduction, run **.NET Diagnostics: Open Getting Started** from the Command Palette.

## Screenshots

*Illustrative panels using fixture data, not live captures.*

**Live counters and GC pauses**

![Live runtime counters and GC pauses (illustrative)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/live-counters.png)

**Heap snapshot and drilldowns**

![Heap snapshot and GC roots drilldown (illustrative)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/heap-drilldown.png)

**Thread snapshot**

![Thread snapshot and inferred deadlock candidates (illustrative)](https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/thread-snapshot.png)

## Permissions and sensitive data

Run the CLI as the same OS user as your app. Some captures can briefly pause the target or
trigger a GC; live heap/thread inspection also needs OS tracing permissions. Higher-impact
actions ask for confirmation before running.

Captures can contain sensitive application data. JSON/CSV sharing exports intentionally exclude
application text and identifiers; raw traces are **not redacted**, require explicit confirmation,
and are only saved locally. No automatic upload occurs.

Windows, macOS and Linux desktop/workspace hosts are supported, including Remote SSH, WSL and
Dev Containers. Install the CLI in that same host environment; browser-only VS Code is not supported.

## Learn more

- [Detailed usage and troubleshooting](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/vscode-extension-reference.md)
- [CLI installation and reference](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/cli-reference.md)
- [Configuration and host support](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/vscode-extension-reference.md#prerequisites)
- [Development and release guide](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/vscode-extension-reference.md#development)
- [Report a problem](https://github.com/pedrosakuma/dotnet-diagnostics/issues)
