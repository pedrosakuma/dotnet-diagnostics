# Install the CLI

Install `dotnet-diagnostics-cli` **0.31.0 or later** for all extension features. CLI 0.29.0
supports the base JSONL protocol-v2 workflows; 0.30.0 adds capture/navigation/export requests,
while 0.31.0 adds allocation exploration and sensitive-value/array-range reads. The extension never
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
