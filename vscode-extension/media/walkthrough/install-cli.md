# Install the CLI

The extension drives `dotnet-diagnostics-cli` **0.29.0 or later** (JSONL protocol v2). It never
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
