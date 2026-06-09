## Summary

- add `collect_events(kind="db")` for curated EF Core / SqlClient diagnostics
- aggregate by sanitized command hash + sanitized connection string
- expose `query_snapshot(handle, view="summary|byCommand|n+1|connectionPool")`
- add `/db-n+1?count=N` BadCodeSample fixture and live coverage

## What changed

- new `DotnetDiagnostics.Core.Db` collector + snapshot types
- server wiring in `CollectEventsTool`, `DiagnosticTools`, `QuerySnapshotTool`, DI registration, and collection dispatcher
- redaction hardening for quoted connection-string passwords and more SQL numeric literal forms
- BadCodeSample now drives reproducible N+1 query bursts with SQLite + EF Core
- docs/changelog updated for the new DB workflow
- stabilized two pre-existing integration tests that were flaking under the required validation command (`HealthCheckCommandTests`, `ByteFetchToolsTests`)

## Verification

- `dotnet build DotnetDiagnostics.slnx -c Release`
- `dotnet test tests/DotnetDiagnostics.Core.Tests/ -c Release --no-build --filter "FullyQualifiedName~Db"`
- `dotnet test tests/DotnetDiagnostics.Mcp.IntegrationTests/ -c Release --no-build`

Closes #250.
