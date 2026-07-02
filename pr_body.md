## Summary

Adds `--native-aot-map <path-to-map.xml>` to the CLI's threshold-gated CPU capture path (`collect --kind counters --capture-when ... --capture cpu-sample`), giving NativeAOT targets name-based `MethodIdentity` on sampled frames instead of raw addresses.

The Core CPU sampler already accepts `NativeAotSymbolResolutionOptions` and the ILC `map.xml` was introduced in #416. The MCP's `collect_sample` tool already exposes `nativeAotMapFile`. This PR closes the gap for the CLI front-end.

## Changes

| Layer | Change |
|---|---|
| `GatedCaptureUseCases.WatchAndCapture` (Core) | New optional `NativeAotSymbolResolutionOptions? nativeAotSymbols = null` parameter; forwarded to `cpuSampler.SampleAsync` in the `CpuSample` branch |
| `CollectEventsTool.cs` (MCP) | Updated call site to pass `nativeAotSymbols: null` explicitly (no behavior change) |
| `CliOptions` | New `NativeAotMapFile` (string?) property parsed from `--native-aot-map <path>` |
| `CliCommands.TryValidateCollect` | Rejects if map file does not exist; rejects if `--capture` is not `cpu-sample` or is absent |
| `CliCommands.CollectAsync` | Builds `NativeAotSymbolResolutionOptions` and passes it to `WatchAndCapture` |
| `CliHelp` | Documents the option under `collect`; notes Linux/perf-path only |
| `CliCompletionScripts` | Adds `--native-aot-map` to `collect` options and file-path completion in bash/zsh/pwsh |

## Tests

4 new tests in `CliGatedCaptureValidationTests`:
- Option round-trip parse
- Missing file → friendly error
- Wrong capture kind → error
- No `--capture` → error

All 313 CLI tests pass. Build is zero-warning for source projects.

Closes #489
