# MCP 2026 Draft Migration Assessment

**Issue**: #546 (Phase 16 P1) · **Branch**: `docs/mcp-2026-draft-migration-assessment` · **Date**: 2026-07-06
**Status**: Research spike — no production migration. Output is this findings doc only.

## Executive summary

**Recommendation: WAIT for spec finalization and a more complete/stable SDK 2.x before doing the real migration.**

The repo is in a **mixed position** against the 2026 draft:

- **Good news:** the main diagnostic drilldown model is already close to SEP-2567. `IDiagnosticHandleStore` already mints opaque server handles, and follow-up calls already pass those handles as ordinary tool arguments.
- **Medium risk:** the core MCP registration surface is likely to stay mostly source-compatible. In a throwaway SDK `2.0.0-preview.1` spike, repo-style `AddMcpServer(...).WithHttpTransport().WithTools<T>().MapMcp(...)`, `[McpServerTool]`, `RequestContext<CallToolRequestParams>`, and `McpServer.ElicitAsync(...)` all still compiled.
- **High risk:** the repo has a **second**, separate state model in the orchestrator attach/proxy path that is explicitly tied to `Mcp-Session-Id`. That state is outside `IDiagnosticHandleStore`, and SEP-2567 breaks it directly.
- **Highest uncertainty:** dump approval currently depends on native MCP elicitation (`server.ElicitAsync(...)`). SEP-2322 replaces that wire pattern with MRTR (`input_required` + retry). The preview SDK still exposes `ElicitAsync`, but its observed wire output was not fully draft-conformant yet, so it is too early to trust as a migration target.

The preview spike changed the urgency recommendation in one important way: **do not start a production SDK upgrade now**. The preview already auto-implements `server/discover`, but in local testing it still emitted `server/discover`, `tools/list`, and `tools/call` results **without the draft-required `resultType` field**, which is a strong sign that the preview is not yet a safe endpoint for a real migration PR.

## Draft changes reviewed

Primary sources reviewed for this spike:

- Draft changelog: <https://modelcontextprotocol.io/specification/draft/changelog>
- SEP-2567: <https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2567>
- SEP-2575: <https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2575>
- SEP-2322: <https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2322>
- SEP-2663 (out of scope for migration work here, but read for context): <https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2663>
- Draft MRTR page: <https://modelcontextprotocol.io/specification/draft/basic/patterns/mrtr>
- Draft Streamable HTTP page: <https://modelcontextprotocol.io/specification/draft/basic/transports/streamable-http>
- Draft versioning / discovery / subscriptions pages.

## Repo impact map

## SEP-2567 — remove protocol-level sessions and `Mcp-Session-Id`

### What already fits

### 1. The main drilldown handle store is already compatible

This part of the issue summary is **confirmed true**.

`IDiagnosticHandleStore` is already a server-minted opaque-handle registry (`src/DotnetDiagnostics.Core/Drilldown/IDiagnosticHandleStore.cs:8-46`). The in-memory implementation generates a fresh random handle ID, stores the artifact behind it, and later resolves by that string alone (`src/DotnetDiagnostics.Core/Drilldown/MemoryDiagnosticHandleStore.cs:28-49,69-85,181-217`).

The public MCP surface already threads these handles through ordinary tool parameters:

- `inspect_heap` returns a handle and documents later drilldown by handle (`src/DotnetDiagnostics.Mcp/Tools/InspectHeapTool.cs:74-78,241-259`).
- `query_snapshot` takes `string handle` as a normal argument, resolves it with `handles.TryGetWithKind(handle)`, and dispatches from the stored artifact kind (`src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.cs:19-24,125-166`).
- other collectors register artifacts with `handles.Register(...)` and return the resulting `handle.Id` in the normal result envelope (`src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:2451-2510`; plus the many `handles.Register(...)` call sites across `src/DotnetDiagnostics.Core/UseCases/*` and `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs`).

There is **no hidden MCP-session affinity** in this store. Handle lifetime is TTL / process-exit based, not protocol-session based. This is exactly the explicit-handle model SEP-2567 wants.

### 2. Tool / prompt / resource registration is not per-session today

`AddDiagnosticMcpServer(...)` registers a fixed tool/prompt/resource surface via `.WithTools<...>()`, `.WithPrompts<...>()`, and `.WithResources<...>()` (`src/DotnetDiagnostics.Mcp/Hosting/DiagnosticServiceRegistration.cs:198-309`).

The list surface can vary by **authorization** because of the scope filter (`BuildScopeListToolsFilter`), but not by connection-local mutable state (`src/DotnetDiagnostics.Mcp/Hosting/DiagnosticServiceRegistration.cs:312-341`). That is acceptable under the draft because auth is per-request input, not protocol session state.

### What breaks

### 3. Orchestrator attach/proxy state is session-bound and must be redesigned

This is the biggest migration blocker not captured by the optimistic “handle store already fits” summary.

The orchestrator path has its own handle/state system that is explicitly bound to MCP sessions:

- `InvestigationHandle` stores `OwnerSessionId` (`src/DotnetDiagnostics.Mcp/Orchestrator/Investigations/InvestigationHandle.cs:43-62`).
- `MemoryInvestigationSessionBinder` maps `sessionId -> handleId` (`src/DotnetDiagnostics.Mcp/Orchestrator/Investigations/MemoryInvestigationSessionBinder.cs:12-72`).
- `attach_to_pod` reflects `McpServer.SessionId`, stamps it onto the handle, and binds the session to the investigation (`src/DotnetDiagnostics.Mcp/Tools/OrchestratorTools.cs:239-250`).
- the in-process proxy filter routes later tool calls by reading the current session id and looking it up in the binder (`src/DotnetDiagnostics.Mcp/Tools/InvestigationProxyCallToolFilter.cs:77-93,130-181`).
- the HTTP reverse proxy requires the caller to present the original `Mcp-Session-Id` header when the handle has an owner (`src/DotnetDiagnostics.Mcp/Hosting/InvestigationProxyEndpoints.cs:37-42,153-180,492-504`).
- distributed-trace and replica-counter fan-out also scope the active investigations by caller session id (`src/DotnetDiagnostics.Mcp/Tools/CollectEventsTool.cs:552-621,685-734`).

SEP-2567 removes exactly this protocol-level concept. Even if the SDK keeps a compatibility `SessionId` property for legacy mode, the draft-compatible path cannot rely on it.

**Conclusion:** the main drilldown handle store is already compatible, but the **orchestrator investigation-handle model is not**. That migration is not optional.

### Migration shape for the orchestrator state

The most likely migration target is:

- make the investigation handle itself the only cross-call state token,
- require it explicitly on every follow-up orchestrator/fan-out call (or have the client/thread above MCP remember and re-thread it),
- remove `OwnerSessionId` / `IInvestigationSessionBinder` as the default routing mechanism,
- keep authorization bound to bearer identity / scopes and (if needed) integrity-protected explicit state, not protocol session headers.

That redesign is conceptually aligned with SEP-2567 and can be designed **before** the final SDK 2.x lands.

## SEP-2575 — remove `initialize`, add per-request `_meta`, require `server/discover`

### What does not look scary

### 1. Core registration / transport wiring looks mostly SDK-managed

The repo's actual HTTP transport wiring is minimal:

- `.WithHttpTransport()` in `Program.cs` (`src/DotnetDiagnostics.Mcp/Program.cs:97-103`)
- `app.MapMcp("/mcp")` in `Program.cs` (`src/DotnetDiagnostics.Mcp/Program.cs:172-179`)
- MCP server metadata set in `DiagnosticServiceRegistration` through `options.ProtocolVersion`, `options.ServerInfo`, and `options.ServerInstructions` (`src/DotnetDiagnostics.Mcp/Hosting/DiagnosticServiceRegistration.cs:221-275`).

There is no custom `initialize` handler in this repo, no custom handshake state machine, and no explicit `server/discover` implementation. That is good: the migration pressure here is likely **mostly on the SDK**, not on handwritten server plumbing.

### 2. Auth does not depend on the handshake

The bearer / OIDC/JWT path is ordinary ASP.NET Core HTTP middleware:

- bearer token validation in `BearerTokenMiddleware` (`src/DotnetDiagnostics.Mcp/Auth/BearerTokenMiddleware.cs:10-186`)
- JWT validation in `OidcJwtAuthExtensions` / `OidcJwtProvider` (`src/DotnetDiagnostics.Mcp/Auth/OidcJwtAuthExtensions.cs`, `src/DotnetDiagnostics.Mcp/Auth/OidcJwtProvider.cs`).

It keys off the `Authorization` header and ASP.NET request context, not `initialize` or session creation. No protocol-handshake lifecycle assumption was found there.

### 3. Request-scoped progress notifications should survive

`CollectionProgressTicker` uses `RequestContext<CallToolRequestParams>`, reads the request's `ProgressToken`, and emits `NotifyProgressAsync(...)` on the server attached to that request (`src/DotnetDiagnostics.Mcp/Diagnostics/CollectionProgressTicker.cs:33-103,114-232`).

This fits the draft Streamable HTTP model, where `notifications/progress` remain **request-scoped** on the response stream of the originating request.

The repo does **not** appear to rely on out-of-band server push for progress. That means SEP-2575's new `subscriptions/listen` model is mostly irrelevant here.

### What changes anyway

### 4. The repo hard-codes the old protocol version

`options.ProtocolVersion = "2025-11-25"` is still set in `DiagnosticServiceRegistration` (`src/DotnetDiagnostics.Mcp/Hosting/DiagnosticServiceRegistration.cs:260`).

That must eventually move to the modern protocol version once the real migration happens.

### 5. HTTP/header assumptions and tests will need a pass

The draft requires per-request `_meta` and the standard request headers (`MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`). In the SDK 2.0 spike, the server enforced the header/body mirror: a bad `Mcp-Name` on `tools/call` returned `HeaderMismatch` `-32020`.

The repo's MCP entrypoint itself relies on the SDK for this, so production code impact should be small. But any custom HTTP tests, orchestrator proxy cases, or documentation that implicitly assume the older header/handshake model will need review.

### 6. Some comments are now stale even if the code path is fine

A few comments still describe HTTP cancellation using `notifications/cancelled`, for example in `CollectionProgressTicker` and collector comments (`src/DotnetDiagnostics.Mcp/Diagnostics/CollectionProgressTicker.cs:19-22`; `src/DotnetDiagnostics.Mcp/Tools/CollectEventsTool.cs:283-285`; `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:877-879`).

Under the draft Streamable HTTP transport, cancellation is modeled as closing the request's response stream; `notifications/cancelled` remains relevant to stdio, not HTTP. The runtime behavior likely remains okay because the code ultimately obeys cancellation tokens, but the comments/docs should be normalized during the real migration.

### 7. `ping`, `logging/setLevel`, and `roots/list_changed`

No direct repo usage was found for:

- `ping`
- `logging/setLevel`
- `notifications/roots/list_changed`
- `notifications/message`-based client logging
- `resources/subscribe` / `resources/unsubscribe`

That means SEP-2575's removals here are **low impact** for this repo.

## SEP-2322 — MRTR replaces server-initiated elicitation/sampling/roots requests

### The directly affected code

The repo's explicit native elicitation flow is concentrated in one place:

- `DumpApprovalElicitation.RequestAsync(...)` checks `server.ClientCapabilities?.Elicitation`, builds `ElicitRequestParams`, then awaits `server.ElicitAsync(...)` (`src/DotnetDiagnostics.Mcp/Diagnostics/DumpApprovalElicitation.cs:33-105`).
- `collect_process_dump` uses that result to gate the destructive write and preserve the `confirm=true` fallback for clients without elicitation (`src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:1882-1974`).

### Why this is still the highest-risk MCP feature

Today the approval UX is logically:

1. `tools/call collect_process_dump`
2. server sends elicitation request to client during the same request
3. client responds
4. tool continues or fails closed

SEP-2322 changes this to:

1. `tools/call collect_process_dump`
2. server returns `resultType: "input_required"` with an `elicitation/create` entry in `inputRequests`
3. client gathers human input
4. client retries the **original** `tools/call` with `inputResponses` (+ possibly `requestState`)
5. server completes

That is a real protocol-contract change, even if the high-level UX is similar.

### Why the current implementation is still a good starting point

The current design already has two properties that map well to MRTR:

- it is **request-bounded**, not backed by a server-side pending-approval queue (`DumpApprovalElicitation.cs:16-21`);
- it already has a conservative fallback/error model: no-capability => `confirm=true` preview path, elicitation failure => fail closed.

So the repo does **not** need to unlearn a big persistent-session approval design.

### What is still unknown

The SDK 2.0 preview still exposes `McpServer.ElicitAsync(...)`, and a repo-style tool compiling against it was straightforward in the throwaway spike. However, that alone does **not** prove the SDK's runtime MRTR path is ready for this repo, because the same preview also emitted draft-version responses without the required `resultType` field in basic `server/discover`, `tools/list`, and `tools/call` testing.

**Practical reading:** do not hand-rewrite the dump-approval flow against raw MRTR objects yet. Wait until the SDK's draft implementation is visibly complete enough to trust its wire behavior, or until the SDK team publishes a clear app-level MRTR authoring pattern for `ElicitAsync`-style flows.

## SDK 2.0.0-preview.1 spike findings

## Throwaway setup

A standalone throwaway ASP.NET Core app was created outside the repo, using:

- `ModelContextProtocol` `2.0.0-preview.1`
- `ModelContextProtocol.AspNetCore` `2.0.0-preview.1`
- repo SDK `10.0.201`

The spike registered two trivial tools:

- `echo`
- `needs_approval` (compile-only proof that `McpServer.ElicitAsync(...)` still exists)

A second throwaway comparison app with the same source but `ModelContextProtocol`/`AspNetCore` `1.4.0` was used to identify at least one concrete source-level API delta.

## What compiled unchanged from repo-style code

The following patterns compiled successfully against `2.0.0-preview.1`:

- `[McpServerToolType]`
- `[McpServerTool]`
- `RequestContext<CallToolRequestParams>` in tool signatures
- `builder.Services.AddMcpServer(options => ...)`
- `options.ProtocolVersion`
- `options.ServerInfo`
- `options.ServerInstructions`
- `.WithHttpTransport(...)`
- `.WithTools<T>()`
- `app.MapMcp("/mcp")`
- `requestContext.Server.SessionId`
- `requestContext.Server.ClientCapabilities`
- `requestContext.Server.ElicitAsync(new ElicitRequestParams { ... })`

That is important: **there is no evidence from the preview spike that this repo needs a broad mechanical rewrite of every tool signature or registration call.**

## Concrete observed API delta vs 1.4.0

One concrete source-level addition was observed:

- `McpServer.IsMrtrSupported` exists in `2.0.0-preview.1`.
- the same source failed against `1.4.0` with:

```text
CS1061: 'McpServer' does not contain a definition for 'IsMrtrSupported'
```

That is the clearest directly observed code-level API difference from the spike.

## `server/discover` appears automatic

No explicit `server/discover` registration API was needed. A simple `AddMcpServer(...).WithHttpTransport(...).MapMcp(...)` app answered `server/discover` successfully and advertised:

- supported protocol versions,
- capabilities,
- `serverInfo`, and
- `instructions`.

That suggests `server/discover` is SDK-managed rather than something this repo will need to implement by hand.

## Preview runtime behavior that argues against migrating now

The strongest finding of the entire spike was **negative**:

- `server/discover`
- `tools/list`
- `tools/call`

all worked against a `2026-07-28` request, **but the returned JSON results omitted the draft-required `resultType` field**.

Examples observed locally:

- `server/discover` returned `supportedVersions`, `capabilities`, `serverInfo`, `instructions`, `ttlMs`, `cacheScope` — but no `resultType`.
- `tools/list` returned `tools`, `ttlMs`, `cacheScope` — but no `resultType`.
- `tools/call` returned `content` / `structuredContent` — but no `resultType`.

So while the preview is useful for shape-checking, it does **not** yet look trustworthy as a production migration target for a repo that cares about spec compliance.

## Other useful preview observations

- In stateless HTTP mode, `SessionId` was observed as `null` inside the tool body.
- The server enforced the request-metadata header/body mirror: a mismatched `Mcp-Name` header on `tools/call` produced `HeaderMismatch` `-32020`.
- The preview answered with SSE-framed responses even for simple calls in the local curl tests, which is fine, but reinforces that transport behavior should be validated end-to-end rather than assumed from older clients.

## What breaks, what does not

| Area | Assessment | Notes |
|---|---|---|
| `IDiagnosticHandleStore` drilldown model | **Mostly safe** | Already explicit opaque handles passed as tool args. |
| Tool registration / `RequestContext` signatures | **Mostly safe** | Preview build suggests source compatibility is good. |
| Auth middleware | **Mostly safe** | No dependency on `initialize` or protocol sessions. |
| Request-scoped progress notifications | **Mostly safe** | Still valid in draft; comments need cleanup. |
| Dump approval elicitation | **Needs focused migration** | Highest-risk tool-level MCP change because SEP-2322 rewrites the wire contract. |
| Orchestrator attach/proxy session binding | **Breaks conceptually** | Explicitly depends on `Mcp-Session-Id` / `SessionId`; must be redesigned. |
| `ping` / `logging/setLevel` / `roots/list_changed` | **Low/no impact found** | No direct repo usage found. |
| `subscriptions/listen` | **Low impact today** | Repo does not depend on out-of-band list/resource change streams. |
| SDK 2.0 preview as migration base | **Not ready** | Useful for exploration, not yet trustworthy for production migration. |

## Recommended migration order

1. **Do not upgrade the main repo to SDK 2.0 preview yet.**
   - The preview is good enough for research, not for production alignment.
   - The missing `resultType` in live testing is the clearest blocker.

2. **Design the orchestrator state refactor independently of the SDK upgrade.**
   - Remove reliance on session-bound attach state.
   - Move toward explicit investigation-handle threading.
   - This is real migration work, but it is protocol-shape work, not package-version churn.

3. **When the spec is final and the SDK is stable enough, do a narrow transport/protocol migration spike first.**
   - bump protocol version,
   - validate `server/discover`, per-request `_meta`, standard HTTP headers,
   - confirm `resultType` on all result shapes,
   - confirm `ElicitAsync` really surfaces MRTR correctly.

4. **Only then migrate the dump-approval flow if needed.**
   - If stable SDK 2.x preserves `ElicitAsync` and translates it cleanly to MRTR, keep the current high-level code shape.
   - If not, rewrite only this focused area against the stable MRTR surface.

5. **After transport + elicitation are proven, clean up residual docs/comments/tests.**
   - cancellation wording,
   - header expectations,
   - legacy handshake assumptions in integration tests and docs.

## Timeline recommendation

**Recommendation: wait on the production migration, but do not wait on the design work.**

More concretely:

- **Wait** on changing `Directory.Packages.props`, the main solution, and the live MCP server behavior until the draft is finalized and the SDK's wire behavior is clearly complete.
- **Start early** only on migration work that is obviously required regardless of SDK polish — chiefly the orchestrator's session-bound state model.

That is the best balance between urgency and avoiding throwaway work.

## Suggested follow-up issue list (do not file yet)

1. **Protocol 2026 finalization bump + dual-era validation**
   - Upgrade to stable SDK 2.x.
   - Validate `server/discover`, per-request `_meta`, headers, `resultType`, and legacy-client fallback behavior.

2. **Explicit investigation-handle routing redesign**
   - Remove default dependence on `Mcp-Session-Id` / `IInvestigationSessionBinder`.
   - Thread investigation handles explicitly through follow-up orchestrator operations.

3. **Dump-approval MRTR validation / rewrite**
   - Confirm whether stable SDK `ElicitAsync` is sufficient.
   - If not, implement the `input_required` / retry pattern explicitly for `collect_process_dump`.

4. **HTTP/proxy/test modernization for standardized headers**
   - Ensure custom HTTP paths/tests/proxy code remain correct under `MCP-Protocol-Version`, `Mcp-Method`, and `Mcp-Name` requirements.

5. **Comment/doc cleanup for modern cancellation + subscriptions semantics**
   - Normalize the remaining `notifications/cancelled` / session-era wording to the final draft model.

## Bottom line

The repo's **diagnostic artifact handle model is already in the right architectural direction**.

The two things that matter most are:

1. **the orchestrator's session-bound investigation state is not draft-compatible and needs redesign**, and
2. **the SDK preview is not mature enough yet to justify a production migration PR**, even though it is already useful for source-shape validation.

That combination supports a clear plan: **document the impact now, design around explicit handles next, and wait for stable SDK/spec convergence before touching the main MCP server implementation.**
