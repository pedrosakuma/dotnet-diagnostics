# Manual MCP smoke test (pre-release / post-merge validation)

A fast, real-client checklist for validating a build before cutting a release
or right after merging a change that touches transport, auth, or protocol
negotiation (e.g. an MCP SDK bump). This complements — it does not replace —
the automated test suite (`dotnet test DotnetDiagnostics.slnx`). Automated
tests exercise the same code paths in-process (via `WebApplicationFactory` +
the real `McpClient`); this playbook exercises the **actual HTTP transport,
actual bearer auth, and a real live target process** end to end, the same way
a real MCP client (VS Code Copilot Chat, Claude Desktop, etc.) would.

Budget: ~10 minutes. Run from a clean worktree of the commit you're about to
release.

## 1. Build and start the server

```bash
dotnet build DotnetDiagnostics.slnx -c Release
MCP_BEARER_TOKEN=dev-smoke dotnet run --project src/DotnetDiagnostics.Mcp -c Release --no-build
```

**Caveat — `launchSettings.json` wins over `ASPNETCORE_URLS`.** `dotnet run`
picks up `src/DotnetDiagnostics.Mcp/Properties/launchSettings.json` and binds
to `http://localhost:5130` regardless of an `ASPNETCORE_URLS` env var you set
on the command line. Don't assume your override took effect — check the
"Now listening on" line in the startup log for the real port, or pass
`--no-launch-profile` if you need a custom bind address.

**Caveat — the startup log line about the bearer token is misleading.**
`Using legacy MCP_BEARER_TOKEN (resolves to 'legacy-root' with root scope)`
describes the **internal scope name** the token maps to, not a literal token
substitution. Keep sending the *actual* `MCP_BEARER_TOKEN` value you set
(`dev-smoke` in the example above) as the `Authorization: Bearer …` header —
sending the word `legacy-root` as the token will 401.

## 2. Spawn a live target and find its real PID

```bash
dotnet samples/CoreClrSample/bin/Release/net10.0/CoreClrSample.dll --urls http://127.0.0.1:0 &
```

**Caveat — on a shared dev box, PID discovery by name is ambiguous.** If
multiple worktrees / sessions are doing similar validation concurrently (a
realistic scenario during active development), `pgrep -f CoreClrSample.dll`
or `$!` from a backgrounded shell can return the wrong process (a different
worktree's copy, or a shell wrapper PID rather than the actual `dotnet`
process). Disambiguate with the full binary path:

```bash
ps aux | grep "CoreClrSample.dll --urls http://127.0.0.1:0" | grep <your-worktree-path>
```

## 3. Real client round-trip over HTTP (raw JSON-RPC via curl)

```bash
# initialize — deliberately request an OLDER protocol version to confirm
# backward compatibility (critical after any SDK/protocol bump)
curl -s -i -X POST http://localhost:5130/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Authorization: Bearer dev-smoke" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke-client","version":"1.0.0"}}}'
# -> capture the "Mcp-Session-Id" response header, reuse it below

# required before any further calls — a bare initialize is not enough
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5130/mcp \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -H "Authorization: Bearer dev-smoke" -H "Mcp-Session-Id: <session-id>" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

# discover tools
curl -s -X POST http://localhost:5130/mcp ... -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

# one real live-process call — this is the single highest-value check:
# it proves the diagnostic IPC attach path still works end to end
curl -s -X POST http://localhost:5130/mcp ... \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"inspect_process","arguments":{"processId":<pid>,"view":"capabilities"}}}'
```

**Caveat — the initialize response echoes back the version you asked for.**
A successful response with `"protocolVersion":"2025-06-18"` (not the
newest supported version) is the expected, correct backward-compatible
behavior — the server negotiates down, it doesn't force-upgrade the client.
Confirm this explicitly whenever a protocol-version-gated feature (like the
SEP-2663 Tasks extension) was just added, so you don't accidentally break
older/simpler clients.

**Caveat — don't hand-roll the MCP Tasks JSON-RPC shape in curl.** Task-mode
opt-in (`io.modelcontextprotocol/tasks`) is nontrivial to construct by hand
and is exactly what the SDK's `McpClient.CallToolAsTaskAsync` abstracts away.
Trust the existing `WebApplicationFactory` + real-`McpClient` integration
tests (e.g. `InvestigationProxyTaskIntegrationTests.cs`) for task-flow
correctness; reserve manual curl smoke testing for auth, protocol
negotiation, `tools/list`, and one non-task live-process call.

## 4. Interpret capability gaps correctly

A real `inspect_process(view="capabilities")` response on a typical Linux dev
box / container without extra privileges will report several collectors as
unavailable:

- `collect_sample(kind="off_cpu")` — needs `CAP_PERFMON` and
  `kernel.perf_event_paranoid` other than `2`.
- ClrMD live-attach paths (`collect_thread_snapshot`, `inspect_heap(source="live")`,
  live `capture_method_bytes`, `get_bytes(kind="module")`) — needs
  `CAP_SYS_PTRACE` and `kernel.yama.ptrace_scope=0`.

**These are expected environment gates, not smoke-test failures or
regressions** — see `AGENTS.md` § "🪪 `CAP_SYS_PTRACE` for live memory
readers". Don't misread them as a broken build; a genuine regression would
show up as an error/exception rather than a structured "not available,
here's why + how to grant it" response.

## 5. Clean up

```bash
kill <server-pid> <sample-pid>
git worktree remove --force <temp-worktree-path>
```

## When to run this

- Before cutting a tagged release, especially after a protocol/SDK/transport
  change.
- Right after merging a PR that touches auth, transport, or MCP protocol
  negotiation, in addition to (not instead of) waiting for CI on the merge
  commit to go green.
