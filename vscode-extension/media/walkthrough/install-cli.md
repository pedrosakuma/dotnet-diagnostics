# Install the CLI

Install `dotnet-diagnostics-cli` **0.33.0 or later** for all extension features. CLI 0.29.0
supports the base JSONL protocol-v2 workflows; 0.31.0 adds allocation exploration and
sensitive-value/array-range reads; 0.32.1 adds request Activities; and 0.33.0 adds guided CPU
comparison, bounded investigation evidence, heap type-instance navigation, and observed
fragmentation projection. The extension checks advertised capabilities before exposing optional
workflows. It never installs or updates tools itself, so you run the command yourself.

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
