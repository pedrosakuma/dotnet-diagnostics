# MCP 2026 Draft Migration Assessment

**Issue**: #546 (Phase 16 P1) · **Original assessment**: 2026-07-06 · **Last revalidated**: 2026-07-27
**Status**: Pre-finalization revalidation complete. SDK `2.0.0-rc.1` released 2026-07-25; stable 2.0.0 confirmed for on or before 2026-07-28.

> **Current status (2026-08-01):** Spike is complete; issue #546 is **closed**. The production
> migration to SDK 2.0.0 has **not yet happened** — the repo remains on **SDK 1.4.0**
> (`Directory.Packages.props`). Issue #548 (SEP-2663 Tasks watch item) remains open.
> The orchestrator session-binding blocker identified here was resolved by #554 / PR #559
> (v0.17.0); see the annotation in
> [`docs/central-orchestrator-design.md`](../central-orchestrator-design.md) §3.8.
>
> **What remains reusable without re-reading the spec:** the repo impact map, the per-SEP
> technical inventory ("What already fits" / "What breaks" sections), and the migration order
> below still describe the correct starting shape for a real SDK bump. Before planning the
> migration, perform a fresh revalidation against the then-current SDK package to confirm no
> further breaking changes have been introduced.
>
> **What is historical pre-finalization guidance only:** the executive summary below
> (timing language, "measured in hours", "migration gate is now imminent") reflects the
> pre-finalization state as of 2026-07-27. That timing is no longer meaningful. The `2025-11-25`
> protocol version string in `DiagnosticServiceRegistration` is one of the known stale items
> that must be updated during the real migration.

## Executive summary

> **⚠️ Historical pre-finalization guidance.** This section reflects the pre-finalization state
> as of 2026-07-27. Timing language ("gate is now imminent", "hours not weeks", "immediately
> after the stable release") no longer applies. The technical inventory in the bullet points
> remains useful as a starting point, but should be re-validated against the then-current SDK
> before planning the production migration.

**Recommendation (2026-07-27): The production migration gate is now imminent. Stable SDK 2.0.0 is confirmed for on or before 2026-07-28. All protocol and wire blockers are resolved. Plan the production bump for immediately after the stable release; begin MRTR dump-approval prep in parallel.**

The repo is in a substantially better position than at the original assessment:

- **Good news:** the main diagnostic drilldown model is already close to SEP-2567. `IDiagnosticHandleStore` already mints opaque server handles, and follow-up calls already pass those handles as ordinary tool arguments.
- **Resolved architectural blocker:** #554 / PR #559 made explicit investigation handles the primary orchestrator routing token. Session binding remains only as a compatibility fallback, so SEP-2567 no longer requires an orchestrator redesign before migration.
- **Core SDK shape remains compatible:** repo-style registration, tools, `RequestContext<CallToolRequestParams>`, and Streamable HTTP still compile against `2.0.0-preview.3` and the RC.1 breaking changes do not affect this repo (see 2026-07-27 revalidation below).
- **The original wire-compliance blocker is fixed:** raw HTTP validation against `2.0.0-preview.3` confirmed `resultType: "complete"` on `server/discover`, `tools/list`, and `tools/call`.
- **Dump approval now has a concrete migration requirement:** `McpServer.ElicitAsync(...)` throws `InvalidOperationException("Elicitation is not supported in stateless mode.")` on `2026-07-28` Streamable HTTP. The tool must use explicit MRTR via `InputRequiredException`, then consume `RequestParams.InputResponses` and `RequestState` on retry.
- **Tasks has a concrete package shape but remains gated:** `2.0.0-preview.3` moved Tasks into `ModelContextProtocol.Extensions.Tasks`; `rc.1` removed the unstable `CreateMcpTaskScope` helper; this repo does not use either API, so #548 is unaffected.
- **RC.1 breaking changes are not applicable to this repo:** all four breaking changes in `2.0.0-rc.1` target either the Tasks extension API or the SDK's client-side OAuth flows. This repo's server surface uses its own custom bearer/OIDC middleware, and the internal `PodLocalInvestigationProxyClient` MCP client (used for pod-to-pod proxying) injects its bearer token directly via `AdditionalHeaders` rather than the SDK's OAuth machinery (`ClientOAuthProvider`, `AuthorizationRedirectDelegate`); none of the OAuth changes apply.

The wait is now measured in hours, not weeks. The remaining work can be split into narrow,
testable migrations immediately after stable 2.0.0 ships. *(Pre-finalization assessment —
see the current-status note above.)*

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

> **2026-07-16 status:** this blocker was addressed by #554 / PR #559. Explicit
> `investigationHandleId` / `investigationHandleIds` routing is now primary;
> session lookup is a compatibility fallback. The analysis below records the
> pre-fix state that motivated that work.

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

### What the `preview.3` revalidation established

The SDK now documents and implements a clear split:

- stateful/legacy sessions may continue using `McpServer.ElicitAsync(...)`;
- stateless `2026-07-28` Streamable HTTP tools must throw `InputRequiredException`;
- retries carry `InputResponses` and `RequestState` on `CallToolRequestParams`.

This was validated with a raw two-round HTTP spike:

1. the first `tools/call` returned `resultType: "input_required"` with an
   `elicitation/create` input request and opaque `requestState`;
2. the retry echoed `requestState`, supplied an accepted Boolean response in
   `inputResponses`, and returned `resultType: "complete"`.

Calling the repo's current high-level pattern (`server.ElicitAsync(...)`) on
the same stateless server returned a complete tool error and logged
`InvalidOperationException("Elicitation is not supported in stateless mode.")`.
The existing broad catch in `DumpApprovalElicitation` would fail closed, which
is safe but would make native approval unusable after the protocol migration.

**Practical reading:** the dump-approval change is no longer an API discovery
problem. It is a focused dual-era implementation task that must preserve the
current fail-closed behavior while adding explicit MRTR retry handling.

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

The strongest finding of the original `preview.1` spike was **negative**:

- `server/discover`
- `tools/list`
- `tools/call`

all worked against a `2026-07-28` request, **but the returned JSON results omitted the draft-required `resultType` field**.

Examples observed locally:

- `server/discover` returned `supportedVersions`, `capabilities`, `serverInfo`, `instructions`, `ttlMs`, `cacheScope` — but no `resultType`.
- `tools/list` returned `tools`, `ttlMs`, `cacheScope` — but no `resultType`.
- `tools/call` returned `content` / `structuredContent` — but no `resultType`.

This specific blocker was fixed in `2.0.0-preview.3`.

## SDK 2.0.0-preview.3 revalidation

`2.0.0-preview.3` was released on 2026-07-15. Its release notes explicitly
include “Fix missing resultType on complete result responses” and extract Tasks
into `ModelContextProtocol.Extensions.Tasks`.

The revalidation used a standalone .NET 10 ASP.NET Core server with
`ModelContextProtocol` and `ModelContextProtocol.AspNetCore`
`2.0.0-preview.3`, driven by raw `2026-07-28` Streamable HTTP requests.

Observed results:

- `server/discover` returned `resultType: "complete"`;
- `tools/list` returned `resultType: "complete"`;
- `tools/call` returned `resultType: "complete"`;
- explicit MRTR returned `resultType: "input_required"` and completed on retry;
- `McpServer.ElicitAsync(...)` still compiled, but failed at runtime in
  stateless HTTP exactly as documented by the SDK.

The SDK is now credible enough to define the migration plan and tests, but it
is still a preview targeting a protocol revision that has not yet been
ratified. That is not sufficient justification for moving the production
server off stable SDK `1.4.0`.

## SDK 2.0.0-rc.1 pre-finalization check (2026-07-27)

`2.0.0-rc.1` was published on **2026-07-25**, confirmed via NuGet. Both
`ModelContextProtocol` and `ModelContextProtocol.AspNetCore` moved to `rc.1`
simultaneously (same date). The SDK maintainers state in the release notes:

> **We remain on-track to release 2.0.0 stable on or before 2026-07-28.**

The production wait — which has been in place since the original spike — is
now measured in hours, not weeks. As of this check (2026-07-27) no stable
`2.0.0` yet appears in the NuGet flat-container index; the latest is `rc.1`.

### RC.1 breaking changes — applicability to this repo

RC.1 ships four documented breaking changes. **None affect this repo**:

| RC.1 breaking change | Applies to this repo? | Reason |
|---|---|---|
| Remove `McpTasksServerExtensions.CreateMcpTaskScope(...)` | No | Repo does not use the Tasks extension API anywhere. `grep` for `CreateMcpTaskScope`, `McpTasksServerExtensions`, and `ModelContextProtocol.Extensions.Tasks` returns no hits in `src/`. |
| Harden OAuth issuer validation — `AuthorizationRedirectDelegate` → `AuthorizationCallbackHandler` | No | The server surface uses custom bearer/OIDC middleware (`BearerTokenMiddleware`, `OidcJwtProvider`). The repo does embed one internal MCP **client** — `PodLocalInvestigationProxyClient` (`src/DotnetDiagnostics.Mcp/Orchestrator/Investigations/PodLocalInvestigationProxyClient.cs`), used for pod-to-pod proxying — but it injects its bearer token directly via `HttpClientTransportOptions.AdditionalHeaders` and never uses the SDK's client-side OAuth flows (`ClientOAuthProvider`, `AuthorizationRedirectDelegate`), so this change does not apply. Follow-up: confirm this client's per-handle `initialize`-handshake caching (`GetOrCreateClientAsync`) is still correct once SEP-2567 removes protocol-level sessions. |
| Require advertised PKCE S256 support | No | Client-side OAuth concern only. |
| Send Dynamic Client Registration `application_type` | No | Client-side OAuth concern only. |

Other notable changes in RC.1:
- Reject mismatched `initialize` protocol versions (#1724): server-side hardening; no code changes required here, behavior is enforced by the SDK automatically.
- HTTP task-filter composition fix (#1722): fixes a ship-stopping Tasks/HTTP defect; not applicable since this repo does not use the Tasks extension.

### Conclusion

The RC.1 content does not introduce any new migration work for this repo beyond
what was already documented at the `preview.3` stage. The release gate is now
purely temporal (stable 2.0.0 landing on or before 2026-07-28) rather than any
outstanding protocol, wire, or API shape concern.

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
| Dump approval elicitation | **Concrete focused migration** | Replace stateless HTTP `ElicitAsync` with explicit `InputRequiredException` + retry handling while retaining legacy compatibility. |
| Orchestrator attach/proxy session binding | **Addressed** | #554 / PR #559 made explicit handles primary; session binding is a fallback. |
| `ping` / `logging/setLevel` / `roots/list_changed` | **Low/no impact found** | No direct repo usage found. |
| `subscriptions/listen` | **Low impact today** | Repo does not depend on out-of-band list/resource change streams. |
| SDK 2.0 preview as migration base | **Wire shape validated, release gate satisfied (rc.1)** | `preview.3` fixed `resultType` and validated MRTR; `rc.1` (2026-07-25) advances to release-candidate status with stable 2.0.0 confirmed for on or before 2026-07-28. |

## Recommended migration order

1. **Upgrade the main repo to SDK 2.0.0 stable immediately after it ships.**
   - The wire-compliance blocker is fixed; MRTR is validated; RC.1 breaking
     changes do not affect this repo; stable 2.0.0 is confirmed for on or
     before 2026-07-28.
   - Bump `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` in
     `Directory.Packages.props` from `1.4.x` to `2.0.0` (or whatever stable
     label ships); build + run full test suite; address any compile errors.

2. **Treat the orchestrator state refactor as complete.**
   - #554 / PR #559 delivered explicit handle threading.
   - Retain and test the legacy session fallback during the dual-era window.

3. **Do a narrow transport/protocol migration immediately after the package bump.**
   - bump protocol version string,
   - validate `server/discover`, per-request `_meta`, standard HTTP headers,
   - confirm `resultType` on all result shapes,
   - move Tasks references to `ModelContextProtocol.Extensions.Tasks` when #548 is actioned,
   - retain validation for legacy `2025-11-25` clients where supported.

4. **Migrate dump approval as an explicit dual-era MRTR flow.**
   - Use `InputRequiredException` for stateless `2026-07-28` HTTP.
   - Consume and validate `InputResponses` / `RequestState` on retry.
   - Preserve the current `confirm=true` fallback for clients without native
     interaction support and the fail-closed behavior for advertised-but-broken
     approval flows.

5. **After transport + elicitation are proven, clean up residual docs/comments/tests.**
   - cancellation wording,
   - header expectations,
   - legacy handshake assumptions in integration tests and docs.

## Timeline recommendation

**Recommendation: stable 2.0.0 is imminent — prepare to migrate immediately after it ships.**

More concretely (updated 2026-07-27):

- **Stable 2.0.0 is confirmed for on or before 2026-07-28.** The previous "wait on changing `Directory.Packages.props`" recommendation is now superseded: the blocking condition (stable, non-preview SDK against a finalized spec) is within hours of being satisfied.
- **Start the narrow package bump (step 1 above) as soon as stable 2.0.0 appears on NuGet.** This is now the gate, not an aspirational future date.
- **Run MRTR dump-approval prep in parallel:** the API shape is stable enough to draft the `InputRequiredException` retry path against `rc.1`; the work does not need to wait for the final package label.

## Follow-up work at finalization

1. **Protocol 2026 finalization bump + dual-era validation**
   - Upgrade to stable SDK 2.x.
   - Validate `server/discover`, per-request `_meta`, headers, `resultType`, and legacy-client fallback behavior.

2. **Explicit investigation-handle routing redesign — completed**
   - Delivered by #554 / PR #559.

3. **Dump-approval MRTR rewrite**
   - Implement the now-validated `input_required` / retry pattern explicitly
     for `collect_process_dump`.
   - Keep the legacy elicitation/fallback path during the supported dual-era
     window.

4. **HTTP/proxy/test modernization for standardized headers**
   - Ensure custom HTTP paths/tests/proxy code remain correct under `MCP-Protocol-Version`, `Mcp-Method`, and `Mcp-Name` requirements.

5. **Comment/doc cleanup for modern cancellation + subscriptions semantics**
   - Normalize the remaining `notifications/cancelled` / session-era wording to the final draft model.

## Bottom line

The repo's **diagnostic artifact handle model is already in the right architectural direction**.

The two things that matter most now are:

1. **the remaining tool-level incompatibility is dump approval's use of
   `ElicitAsync` on stateless HTTP**, with a validated explicit-MRTR replacement
   whose API shape is known and stable as of `rc.1`;
2. **the release gate is now satisfied in practice**: `2.0.0-rc.1` shipped
   2026-07-25 with no repo-impacting breaking changes, and the SDK maintainers
   confirmed stable `2.0.0` ships on or before 2026-07-28.

That combination supports a clear and immediate plan: **start the package bump
in `Directory.Packages.props` as soon as stable 2.0.0 appears on NuGet, run the
narrow transport/protocol migration and the MRTR dump-approval rewrite, then
clean up residual docs and comments.**
