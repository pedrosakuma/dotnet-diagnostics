# Install the CLI

Install `dotnet-diagnostics-cli` **0.30.0 or later** for all extension features. CLI 0.29.0
supports the base JSONL protocol-v2 workflows, but lacks the new capture/navigation/export requests. The extension never
installs or updates tools itself, so you run the command yourself.

Install:

```
dotnet tool install -g dotnet-diagnostics-cli
```

Update an older CLI:

```
dotnet tool update -g dotnet-diagnostics-cli
```

The framework-dependent CLI needs a .NET 10 runtime. If the executable lives elsewhere, set the
machine-scoped `dotnetDiagnostics.cliPath` setting.
