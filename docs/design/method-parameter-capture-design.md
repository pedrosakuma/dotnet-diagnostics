# Method Parameter Capture — Security, UX, and Capability-Gate Design

**Issue**: #556 · **Branch**: `docs/method-parameter-capture-design` · **Date**: 2026-07-06
**Status**: Design only — no production code, no shipping tool-contract changes in `docs/tool-reference.md` yet.

## Executive summary

This feature should ship, if at all, as a **new `collect_sample(kind="method-params")` branch** rather than a new MCP tool, keeping faith with the repo's “one tool per concept” rule and the existing discriminator pattern for bounded samplers (`collect_sample`) and unified drilldowns (`query_snapshot`). The feasibility spike already established that the underlying mechanism is a profiler + startup-hook pipeline on **.NET 8+ CoreCLR**, not a pure EventPipe collector, and that the security/operator-risk profile is materially higher than the repo's current EventPipe-only surfaces (`AGENTS.md:183-199`; `docs/research/method-parameter-capture.md:10-20,191-213,217-287`).

The design below therefore makes parameter capture a **triple-gated** feature:

1. the existing `collect_sample` tool-level **`eventpipe`** scope,
2. a new literal modifier scope **`sensitive-parameter-read`**, and
3. a server-wide feature flag **`Diagnostics:AllowMethodParameterCapture`** plus a per-call **`includeSensitiveValues=true`** acknowledgement.

V1 should be **MCP-server-only**, **bounded** (`durationSeconds`, `maxEvents`, preview count, per-value caps), **audited**, and **rejected** for `.NET < 8`, `NativeAOT`, active Hot Reload, and targets that already carry a non-notify-only profiler (`docs/authorization.md:34-36,52-76`; `src/DotnetDiagnostics.Mcp/Security/BearerPrincipal.cs:59-82`; `docs/research/method-parameter-capture.md:99-107,204-213,223-287`).

## Constraints inherited from the current repo

### Keep the tool surface flat

The repo explicitly prefers extending an existing discriminator tool over minting a new top-level tool. `collect_sample` already owns the bounded sampler family (`cpu`, `off_cpu`, `allocation`, `native-alloc`), and the discriminator envelope preserves the common `DiagnosticResult<T>` fields (`summary`, `hints`, `handle`, `handleExpiresAt`, `resolvedProcess`, `cancelled`) while moving kind-specific payloads under `data.kind` + one populated branch object (`AGENTS.md:191-199`; `src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs:17-60,189-228`; `src/DotnetDiagnostics.Core/DiagnosticResult.cs:14-99`).

### Stay aligned with the existing auth model

The MCP server already separates:

- **primary scopes** checked by `[RequireScope]` / `[RequireAnyScope]`, with `all` vs `any` semantics reflected in both runtime authorization and `tools/list` metadata, and
- **modifier scopes** checked by `HasExplicitScope`, where root / `*` does **not** auto-grant access (`docs/authorization.md:12-36,38-76`; `src/DotnetDiagnostics.Mcp/Security/RequireScopeAttribute.cs:11-19,47-58`; `tests/DotnetDiagnostics.Mcp.IntegrationTests/ToolScopeAttributesTests.cs:49-65,102-113,219-230`; `src/DotnetDiagnostics.Mcp/Security/BearerPrincipal.cs:59-82`).

That split is the right model here: method-parameter capture needs the ordinary sampler capability (`eventpipe`) **and** a stronger literal opt-in because the payload can contain secrets, PII, and business data (`docs/research/method-parameter-capture.md:191-215`).

### Reuse the existing bounded-artifact model

Heavy artifacts are already issued through `IDiagnosticHandleStore`, which records an opaque handle, TTL, and process-linked eviction semantics (`src/DotnetDiagnostics.Core/Drilldown/IDiagnosticHandleStore.cs:3-30`). The current collector families standardize on a **10-minute TTL** for live artifacts (`EventCollectionUseCases`, `SamplerUseCases`, `HeapInspectionUseCases`) and the in-memory store evicts on TTL expiry, process exit (for live-origin handles), or capacity pressure (`src/DotnetDiagnostics.Core/UseCases/EventCollectionUseCases.cs:43-46`; `src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs:21-24`; `src/DotnetDiagnostics.Core/UseCases/HeapInspectionUseCases.cs:27-32`; `src/DotnetDiagnostics.Core/Drilldown/MemoryDiagnosticHandleStore.cs:28-49,99-123,144-170`).

Method-parameter capture should follow that exact retention model rather than inventing persistent storage (`docs/research/method-parameter-capture.md:204-213,262-280`).

## 1. Security design

## 1.1 Authorization and opt-in semantics

### New scope name

Introduce a new **modifier** scope named **`sensitive-parameter-read`**.

Rationale:

- Scope names are stable, kebab-case trust boundaries (`docs/authorization.md:12-16`).
- Existing sensitive-data modifiers (`sensitive-heap-read`, `eventsource-any`, `symbols-remote`) are literal additive grants rather than wildcard-inherited primary scopes (`docs/authorization.md:58-76`; `src/DotnetDiagnostics.Mcp/Security/BearerPrincipal.cs:69-82`).
- The feasibility doc explicitly recommends a scope *stronger than* `sensitive-heap-read`, not reuse of the heap scope (`docs/research/method-parameter-capture.md:204-215,321-323`).

### Effective authorization rule

For `collect_sample(kind="method-params")`, the effective rule is:

- the tool entry remains statically gated by **`[RequireScope("eventpipe")]`** (the existing `collect_sample` contract), **and**
- the `kind="method-params"` branch performs a **runtime literal-scope check** for `sensitive-parameter-read`.

That means the branch is effectively **ALL-of**:

- `eventpipe` (primary scope; wildcard/root may satisfy it), and
- `sensitive-parameter-read` (modifier scope; wildcard/root does **not** satisfy it).

This mirrors how the repo already keeps a broader dispatcher surface and then tightens per-kind/per-view boundaries at runtime (`docs/authorization.md:34-36,52-76,204-214`; `src/DotnetDiagnostics.Mcp/Tools/InspectProcessTool.cs:147-167`; `src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.cs:178-190`; `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:3511-3535`).

Because the static MCP tool is still `collect_sample`, `tools/list` will continue to advertise the existing `eventpipe` requirement for the tool surface. The additional `sensitive-parameter-read` requirement is therefore a **runtime per-kind tightening**, just like the repo's other dispatcher-style boundaries; the contract docs for `kind="method-params"` must call that out explicitly rather than pretending the static `tools/list` metadata can fully describe it (`docs/authorization.md:34-36,52-56`; `src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs:48-60`; `src/DotnetDiagnostics.Mcp/Tools/InspectProcessTool.cs:147-167`).

### Server-wide gate

Add a new configuration flag under the existing `Diagnostics` section:

- config key: **`Diagnostics:AllowMethodParameterCapture`**
- env var: **`Diagnostics__AllowMethodParameterCapture=true`**
- default: **`false`**

Why this shape:

- repo-wide diagnostic policy flags already live under the `Diagnostics` section (`SecurityOptions.SectionName = "Diagnostics"`) and use ASP.NET Core env-var binding (`Diagnostics__...`) (`src/DotnetDiagnostics.Core/Security/SecurityOptions.cs:10-18,26-44`);
- this feature is a deployment policy decision, like the existing sensitive-value / allowlist controls, not an auth token bootstrap concern like `MCP_BEARER_TOKEN` (`src/DotnetDiagnostics.Mcp/Program.cs:115-166`; `docs/authorization.md:78-127`).

The gate is **not** a compatibility bypass. Unlike `SensitiveValueGate`, which still carries a legacy migration path for heap/EventSource flows, method-parameter capture should launch with **no legacy global-only mode**: the server flag is necessary but never sufficient (`src/DotnetDiagnostics.Core/Security/SensitiveValueGate.cs:4-37`; `src/DotnetDiagnostics.Mcp/Security/LegacyDiagnosticsFlagDeprecation.cs:7-29`).

### Per-call acknowledgement

Add **`includeSensitiveValues`** to the `kind="method-params"` request contract.

Semantics:

- the property is **required for this kind**;
- the only accepted V1 value is **`true`**;
- omitted or `false` requests are rejected before any profiler attach/startup-hook action.

This mirrors the repo's existing defense-in-depth pattern where a dangerous operation requires a request-level acknowledgement in addition to scope/policy (`collect_process_dump(confirm=true)`) and where sensitive surfaces already require explicit caller opt-in even after the server gate is open (`QuerySnapshot(includeSensitiveValues=true)`) (`docs/authorization.md:182-203`; `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:1903-1919`; `src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.cs:139`; `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:2166-2177`).

### Failure mode for auth/policy misses

Use structured `DiagnosticResult.Fail<T>(summary, new DiagnosticError(...), hints...)` envelopes, not exceptions, matching the existing repo error surface (`src/DotnetDiagnostics.Core/DiagnosticResult.cs:96-142`; `src/DotnetDiagnostics.Core/UseCases/AttachGuard.cs:143-183`).

Recommended policy/auth failures:

| Case | `DiagnosticError.Kind` | Required wording |
| --- | --- | --- |
| caller lacks `eventpipe` | existing `Forbidden`/tool-scope envelope | unchanged existing `collect_sample` authorization path |
| caller lacks `sensitive-parameter-read` | `Forbidden` | “`collect_sample(kind=\"method-params\")` requires the literal scope `sensitive-parameter-read`. Root or wildcard tokens do not auto-grant this modifier scope.” |
| server flag off | `MethodParameterCaptureDisabled` | “Method parameter capture is disabled by server policy. Set `Diagnostics:AllowMethodParameterCapture=true` (env `Diagnostics__AllowMethodParameterCapture=true`) to enable it.” |
| `includeSensitiveValues` omitted/false | `InvalidArgument` | “`collect_sample(kind=\"method-params\")` requires `includeSensitiveValues=true` for an explicit sensitive-data acknowledgement.” |

`MethodParameterCaptureDisabled` is intentionally more specific than a generic `Forbidden`: unlike a pure bearer-scope miss, the remediation is an operator deployment change, and the repo already uses specific stable error kinds such as `SymbolServerNotAllowed` and `EventSourceProviderNotAllowed` when the denial is policy-shaped rather than transport-auth-shaped (`src/DotnetDiagnostics.Core/UseCases/SymbolPathValidation.cs:46-52`; `src/DotnetDiagnostics.Core/UseCases/EventCollectionUseCases.cs:1261-1266`).

## 1.2 Truncation, formatting, and redaction policy

The feasibility spike was explicit that this feature must be *stricter* than upstream dotnet-monitor defaults, with hard caps, shallow object rendering, and `DebuggerDisplay` off by default (`docs/research/method-parameter-capture.md:197-213,223-230`). V1 should adopt the following fixed caps:

| Control | Value | Rationale |
| --- | --- | --- |
| `durationSeconds` | hard max **30** | limits capture window and operator blast radius |
| `maxEvents` | hard max **500** | bounds retained invocations even for hot methods |
| method filters | **1-10** entries | prevents “capture everything” or giant allowlists |
| object-graph depth | **2** | enough for shallow DTO inspection, not recursive heap browsing |
| object members per object | **10** | prevents huge POCO expansion |
| enumerable elements per value | **10** | prevents dumping full lists/dictionaries |
| rendered value size per parameter | **4 KiB UTF-8 max** | bounded memory/log/API surface per argument |
| inline preview string per parameter | **256 UTF-16 chars max** | keeps the inline preview readable and safe |
| inline preview events | **10 default, 25 max** | bounded MCP payload size |

### Rendering rules

1. **Primitives / enums / strings** render directly, then pass through the existing sensitive-data redactor.
2. **Collections / arrays** may surface bounded element prefixes **only when they can be walked without executing target code** (for example raw array slots or debugger/runtime data that already materializes the elements). V1 must **not** call user `GetEnumerator()`, `MoveNext()`, `Current`, LINQ helpers, or `Count`/other property getters just to render a value.
3. **Objects** render as a shallow **field-only** bag of up to 10 members, depth-first to a maximum depth of 2. V1 must **not** invoke property getters, `ToString()`, custom formatters, or any reflected helper method.
4. **Unsupported / formatter-faulted values** render as metadata only (`typeName`, `renderError`) rather than failing the whole invocation.
5. **By-ref / pointer-like / ref-struct / unsafe payloads** render as metadata only in V1.

The redaction pass should reuse the existing `SensitiveDataRedactor` pattern set (PEM headers, bearer/JWT/basic tokens, password-like connection-string fragments, API keys, AWS-style keys, GitHub tokens) so method-parameter capture inherits the same “high-signal, low-false-positive” guardrails the heap surfaces already use (`src/DotnetDiagnostics.Core/Security/SensitiveDataRedactor.cs:5-18,26-54,91-137`).

### `DebuggerDisplay` policy

V1 keeps **`DebuggerDisplay` evaluation off**.

- The request contract does **not** expose `useDebuggerDisplayAttribute` in V1.
- If a future revision adds it, it must be an explicit boolean defaulting to `false`, documented as potentially executing target code, and independently auditable.

This matches the feasibility guidance exactly: `DebuggerDisplay`/formatter evaluation can execute target code via getters, `ToString()`, or helper methods and should not silently ride along with a capture of already-sensitive values (`docs/research/method-parameter-capture.md:208-210,229-230`).

## 1.3 Audit and logging requirements

The repo already treats authorization and sensitive operations as structured `ILogger` events that log the **principal name**, tool, and request identifiers — never the bearer itself (`src/DotnetDiagnostics.Mcp/Security/ToolScopeAuthorizationFilter.cs:27-29,63-80`; `src/DotnetDiagnostics.Mcp/Security/BearerPrincipal.cs:6-10`; `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:1929-1946,3538-3573`). `Program.cs` configures single-line console logging with timestamps, so parameter-capture audit lines should follow the same style (`src/DotnetDiagnostics.Mcp/Program.cs:34-39`).

Required audit events:

| Level | When | Required fields |
| --- | --- | --- |
| `Warning` | request denied by missing modifier scope | `tool`, `tokenName`, `processId`, `reason=MissingSensitiveParameterScope`, `methodFilters`, `durationSeconds`, `maxEvents` |
| `Warning` | request denied because server flag is off | `tool`, `tokenName`, `processId`, `reason=ServerPolicyDisabled`, `methodFilters`, `durationSeconds`, `maxEvents` |
| `Warning` | request denied by capability gate | `tool`, `tokenName`, `processId`, `reason=<gate>`, `runtimeVersion`, `methodFilters` |
| `Information` | capture starts | `tool`, `tokenName`, `processId`, `runtimeVersion`, `methodFilters`, `durationSeconds`, `maxEvents`, `previewCount`, `includeSensitiveValues=true` |
| `Information` | capture completes | `tool`, `tokenName`, `processId`, `runtimeVersion`, `methodFilters`, `elapsedMs`, `captureCount`, `droppedCount`, `truncatedValueCount`, `redactedValueCount`, `handleId`, `handleExpiresAt` |
| `Warning` | capture aborts after attach/startup-hook/profiler preflight failure | same fields as start + `errorKind`, `detail` |

Important constraints:

- **Never log parameter values**.
- For pre-auth / pre-policy denials, log only the **validated caller-supplied filters** (or their count). Do **not** resolve overloads against the target just to enrich a denied audit event.
- For capture start/completion, log the **resolved method filters** (including canonical identities when known), so operators can audit which overloads were actually instrumented.
- Keep the logger in the MCP/tool layer rather than burying the only audit trail inside Core, because the MCP layer is where bearer identity (`tokenName`) exists today (`src/DotnetDiagnostics.Mcp/Security/ToolScopeAuthorizationFilter.cs:57-80`).

## 2. UX / tool-contract design

## 2.1 Request contract

The capability extends the existing `collect_sample` tool with a new discriminator value:

```json
{
  "kind": "method-params",
  "processId": 4242,
  "durationSeconds": 10,
  "maxEvents": 100,
  "previewCount": 10,
  "includeSensitiveValues": true,
  "methods": [
    {
      "moduleName": "MyService.dll",
      "typeName": "MyService.Auth.TokenService",
      "methodName": "Validate",
      "signature": ["System.String", "System.Threading.CancellationToken"]
    }
  ]
}
```

### Properties

| Property | Type | Required? | Default | Rules |
| --- | --- | --- | --- | --- |
| `kind` | `string` | yes | none | must equal `"method-params"` |
| `processId` | `integer` | no | auto-resolve when one visible target exists | same semantics as other `collect_sample` kinds |
| `durationSeconds` | `integer` | no | `10` | range **1-30** |
| `maxEvents` | `integer` | no | `100` | range **1-500**; collection stops early when reached |
| `previewCount` | `integer` | no | `10` | range **1-25**; controls only inline preview length |
| `includeSensitiveValues` | `boolean` | yes for this kind | none | must be `true` |
| `methods` | `array<MethodFilter>` | yes | none | **1-10** entries; empty array is invalid |

### `MethodFilter`

| Property | Type | Required? | Matching semantics |
| --- | --- | --- | --- |
| `moduleName` | `string` | yes | exact assembly/module file name, case-insensitive ordinal (`MyService.dll`) |
| `typeName` | `string` | yes | exact managed full type name, case-sensitive ordinal |
| `methodName` | `string` | yes | exact managed method name, case-sensitive ordinal; constructors use `.ctor` / `.cctor` |
| `genericArity` | `integer` | no | exact generic arity when the caller needs to distinguish `Foo()` from `Foo<T>()` |
| `signature` | `array<string>` | no | exact ordered parameter-type full names; omitted means “all overloads matching module+type+method” |
| `moduleVersionId` | `string` | no | optional GUID string disambiguator when the same `moduleName` can be loaded in multiple ALCs or from multiple copies |

The implementation must resolve every requested filter to one or more **canonical method identities** before instrumentation. Two rules keep that predictable:

1. omitting `signature` intentionally allows **multi-overload expansion** within the same resolved module/type/method family; every matched overload is instrumented and later surfaced with its own canonical identity in the response, and
2. if the filter still spans **multiple module copies / MVIDs** (for example the same `moduleName` loaded in different ALCs) and the caller did not disambiguate with `moduleVersionId`, the call must fail with `InvalidArgument` and surface the candidate canonical identities rather than choosing arbitrarily.

This follows the repo's existing `MethodIdentity` model, which treats `(ModuleVersionId, MetadataToken)` plus `GenericArity` as the stable round-trip key and treats names as display aids (`src/DotnetDiagnostics.Core/Memory/InvestigationSummary.cs:88-105`).

### Invalid inherited knobs

`collect_sample` already has kind-specific knobs (`topN`, `depth`, `symbolPath`, `resolveSourceLines`, `resolveMethodInstantiations`, `nativeAotMapFile`, `exportTrace`, `nativeAllocSamplePeriod`) for its current sampler family (`src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs:69-103`). For `kind="method-params"`, those fields should be **rejected with `InvalidArgument`**, not silently ignored. This keeps the contract crisp and prevents callers from assuming e.g. symbol-resolution or trace-export semantics that do not exist for this branch.

## 2.2 Response contract

The top-level envelope remains the existing `DiagnosticResult<CollectSampleEnvelope>` shape (`src/DotnetDiagnostics.Core/DiagnosticResult.cs:14-99`; `src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs:189-212`). The new branch adds `data.kind = "method-params"` plus a populated `data.methodParams` object.

### Top-level result

| Property | Type | Notes |
| --- | --- | --- |
| `summary` | `string` | must include filters, runtime version, counts, and redaction/truncation state |
| `data.kind` | `string` | `"method-params"` |
| `data.methodParams` | `MethodParameterCaptureSample` | populated only for this discriminator |
| `handle` | `string` | opaque handle to the full bounded artifact |
| `handleExpiresAt` / `handleExpiresInSeconds` | timestamp / integer | standard handle TTL fields |
| `resolvedProcess` | `ProcessContext` | already carried on successful live-process responses (`src/DotnetDiagnostics.Core/ProcessDiscovery/ProcessContext.cs:5-31`) |
| `cancelled` | `boolean` | standard collector cancellation semantics |

### `MethodParameterCaptureSample`

| Property | Type | Required | Meaning |
| --- | --- | --- | --- |
| `runtimeFlavor` | `string` | yes | `"CoreClr"` in V1 |
| `runtimeVersion` | `string` | yes | copied from resolved target context/capability probe |
| `methodFilters` | `array<MethodFilter>` | yes | echoed effective filters |
| `durationSeconds` | `integer` | yes | effective bounded window |
| `maxEvents` | `integer` | yes | effective cap |
| `previewCount` | `integer` | yes | effective inline cap |
| `captureCount` | `integer` | yes | number of retained invocations |
| `droppedCount` | `integer` | yes | invocations seen but not retained because the bounded store filled or capture stopped |
| `truncatedValueCount` | `integer` | yes | number of parameter values truncated by size/depth/member caps |
| `redactedValueCount` | `integer` | yes | number of parameter values changed by sensitive-data redaction |
| `valuesTruncated` | `boolean` | yes | convenience flag = `truncatedValueCount > 0` |
| `valuesRedacted` | `boolean` | yes | convenience flag = `redactedValueCount > 0` |
| `stopReason` | `string` | yes | one of `duration_elapsed`, `max_events_reached`, `cancelled` |
| `events` | `array<MethodParameterInvocation>` | yes | **first `previewCount` retained invocations only** |

### `MethodParameterInvocation`

| Property | Type | Meaning |
| --- | --- | --- |
| `sequence` | `integer` | 1-based stable order within the capture |
| `timestampUtc` | `string` | RFC 3339 / ISO-8601 UTC timestamp |
| `method` | `ResolvedMethodIdentity` | resolved module/type/method/signature that fired |
| `parameters` | `array<CapturedParameterValue>` | bounded, rendered parameter list |

### `ResolvedMethodIdentity`

| Property | Type | Meaning |
| --- | --- | --- |
| `moduleName` | `string` | resolved module/assembly name |
| `moduleVersionId` | `string` | resolved PE module MVID (GUID string) |
| `typeName` | `string` | resolved managed full type name |
| `methodName` | `string` | resolved method name |
| `genericArity` | `integer` | resolved generic method arity |
| `metadataToken` | `integer` | resolved IL method-def token when available |
| `signature` | `array<string>` | resolved ordered parameter-type full names |

### `CapturedParameterValue`

| Property | Type | Meaning |
| --- | --- | --- |
| `name` | `string` | managed parameter name when available |
| `typeName` | `string` | managed parameter type full name |
| `value` | `string` | bounded rendered representation after redaction |
| `redacted` | `boolean` | whether redaction changed the rendered value |
| `truncated` | `boolean` | whether size/depth/member caps changed the rendered value |
| `notes` | `array<string>` | optional markers such as `metadata-only`, `byref`, `formatter-error` |

### Summary text requirements

The summary string should follow the repo's existing collector style: concise but decision-useful, with the handle called out when follow-up drilldown exists (`src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs:171-180,274-285`; `src/DotnetDiagnostics.Core/UseCases/HeapInspectionUseCases.cs:73-79`).

Required summary content:

- target runtime version,
- method filter count (or the single resolved filter),
- actual `captureCount` vs `maxEvents`,
- `droppedCount`,
- whether values were truncated and/or redacted,
- preview/full-artifact split (`previewCount` inline, full bounded capture behind handle).

Recommended shape:

> Captured 37 method invocations over 10s on CoreClr 10.0.2 for 1 method filter (`MyService.Auth.TokenService.Validate`). Retained 37/100 events (dropped 2). Values truncated: 9, redacted: 4. Inline preview shows first 10 invocations; handle `01H...` retains the full bounded capture for ~10 minutes.

## 2.3 Artifact retention and handle kind

Register the retained artifact as:

- handle kind: **`method-params-capture`**
- origin: **`HandleOrigin.Live`**
- `evictWhenProcessExits`: **`true`**
- TTL: **10 minutes**

This aligns with the established live-collector convention (`EventCollectionUseCases.CollectionHandleTtl`, `SamplerUseCases.SampleHandleTtl`, `HeapInspectionUseCases.HeapSnapshotHandleTtl`) and with the handle store's process-exit invalidation for live-origin artifacts (`src/DotnetDiagnostics.Core/UseCases/EventCollectionUseCases.cs:43-46`; `src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs:21-24`; `src/DotnetDiagnostics.Core/UseCases/HeapInspectionUseCases.cs:27-32`; `src/DotnetDiagnostics.Core/Drilldown/IDiagnosticHandleStore.cs:19-30,54-62`).

The handle's payload should be the **full bounded capture artifact**, not the preview prefix. The inline `events` array is merely the first `previewCount` retained invocations.

### Follow-up reads of the handle

V1 must not mint a handle whose retained sensitive payload can later be read under a weaker boundary than the original collection. Any follow-up read path for `method-params-capture` — whether implemented immediately as `query_snapshot` support or added in the same implementation spike as a resource-backed read — must require:

- the originating live-collector boundary (`eventpipe` / live-handle authorization), and
- the literal modifier scope **`sensitive-parameter-read`** on **every** read of the handle.

At minimum, the retained artifact should support:

- a **summary** projection (counts, filters, runtime, truncation/redaction totals, no extra event rows), and
- an **events** projection that returns retained invocation rows using the same redaction/truncation semantics as the inline preview.

The same triple gate from collection time must still apply to any follow-up read that returns **parameter values**:

- the server policy must still allow method-parameter capture (`Diagnostics:AllowMethodParameterCapture=true`),
- the caller must still hold the literal `sensitive-parameter-read` scope, and
- the follow-up request must carry **`includeSensitiveValues=true`** again.

If the implementation offers a metadata-only summary projection with **no parameter values**, that projection may omit `includeSensitiveValues=true`; the value-bearing projection may not. If the operator disables `Diagnostics:AllowMethodParameterCapture` after a capture has already been collected, metadata-only summary reads may continue, but value-bearing reads must fail closed.

If the implementation chooses the unified drilldown path, `query_snapshot` must learn the new handle kind and the handle-authorization table must map the `method-params-capture` projections to `sensitive-parameter-read`, following the same “collector mints handle, drilldown re-applies the required boundary” rule used elsewhere in the repo (`docs/authorization.md:204-214`; `src/DotnetDiagnostics.Core/Drilldown/HandleAuthorizationTable.cs:5-18,26-44`; `src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.cs:178-190`).

## 3. Capability gates

The repo's existing pattern is to fail with a structured `DiagnosticError` plus recovery hints instead of surfacing raw attach exceptions (`src/DotnetDiagnostics.Core/UseCases/AttachGuard.cs:143-183`; `src/DotnetDiagnostics.Core/DiagnosticResult.cs:96-142`). Method-parameter capture should do the same, but most of its preflight rejections are **`NotSupported`** / **`Conflict`**, not `PermissionDenied`.

### Capability-gate table

| Gate | Error kind | Required summary/message |
| --- | --- | --- |
| target runtime `< 8.0` | `NotSupported` | “`collect_sample(kind=\"method-params\")` requires a .NET 8+ CoreCLR target because runtime startup-hook injection depends on `DiagnosticsClient.ApplyStartupHookAsync(...)` on .NET 8+.” |
| target runtime is `NativeAot` | `NotSupported` | “`collect_sample(kind=\"method-params\")` is unsupported for NativeAOT targets. V1 requires CoreCLR profiler attach + ReJIT instrumentation.” |
| Hot Reload active | `Conflict` | “`collect_sample(kind=\"method-params\")` cannot run while Hot Reload is active for the target process. Stop the Hot Reload session and retry.” |
| non-notify-only profiler already attached | `Conflict` | “`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.” |

### Remediation hints

Every gate should carry at least one `NextActionHint`, following the existing pattern (`src/DotnetDiagnostics.Core/DiagnosticResult.cs:101-129`; `src/DotnetDiagnostics.Core/UseCases/AttachGuard.cs:152-177`). Recommended hints:

- runtime `< 8`: `inspect_process(view="info")` / `inspect_process(view="capabilities")` to confirm runtime flavor/version.
- NativeAOT: suggest `collect_sample(kind="cpu")`, `collect_sample(kind="native-alloc")`, or EventPipe collectors instead of parameter capture.
- Hot Reload: retry after stopping `dotnet watch` / Hot Reload.
- existing profiler: retry on a clean target, or use the other profiler owner's tooling.

### Why these exact gates

The feasibility spike already narrowed the support envelope to **.NET 8+**, **CoreCLR only**, and explicit rejections for Hot Reload and pre-existing non-notify-only profilers (`docs/research/method-parameter-capture.md:88-106,223-230,281-287,320-323`). That matches the current repo's runtime classification surface, which already carries `RuntimeFlavor` (`CoreClr` vs `NativeAot`) and `RuntimeVersion` in both capabilities and resolved process context (`src/DotnetDiagnostics.Core/Capabilities/DiagnosticCapabilities.cs:16-27`; `src/DotnetDiagnostics.Core/ProcessDiscovery/ProcessContext.cs:11-31`).

`PermissionDenied` remains the right kind only for *host privilege* failures that occur after a theoretically supported request is attempted (for example, attach denied by OS capability/permission), mirroring the repo's ptrace/perf conventions (`src/DotnetDiagnostics.Core/UseCases/AttachGuard.cs:143-183`; `src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs:229-257,323-350`).

## 4. Delivery-surface recommendation

## 4.1 MCP server: yes, in V1

This feature belongs first on the MCP server because the HTTP transport is where the repo's bearer-scope model, modifier scopes, and tool-level audit identity already live (`docs/authorization.md:1-10,12-76`; `src/DotnetDiagnostics.Mcp/Security/ToolScopeAuthorizationFilter.cs:15-29,63-80`). The existing handle + `DiagnosticResult<T>` model is also already optimized for “small inline preview + expiring opaque artifact” workflows (`src/DotnetDiagnostics.Core/DiagnosticResult.cs:36-62`; `src/DotnetDiagnostics.Core/Drilldown/IDiagnosticHandleStore.cs:3-30`).

## 4.2 `dotnet-diagnostics-cli`: no, not in V1

Do **not** expose method-parameter capture in the standalone CLI for the first iteration.

Reasons:

1. The CLI has **no bearer transport and no scope check**; the auth doc explicitly scopes bearer authorization to HTTP and excludes the CLI/stdio path (`docs/authorization.md:7-10,78-84`).
2. The CLI is intentionally **Core-only** and must not re-couple to the server assembly or MCP auth layer (`AGENTS.md:9-11`; `tests/DotnetDiagnostics.Cli.Tests/NoServerReferenceTests.cs:7-23`).
3. The feature's risk model depends on **operator policy + caller identity + auditability**, which the CLI does not currently provide.
4. The feasibility doc already identified packaging native profiler payloads as a major cost driver and explicitly called out CLI exposure as extra work, not a prerequisite for server delivery (`docs/research/method-parameter-capture.md:134-145,291-297`).

A later CLI design can revisit a human-operated variant with a separate explicit consent UX, but that should not block or dilute the safer server-first model.

## 4.3 BenchmarkDotNet diagnoser: no, not in V1

Do **not** expose this in the BenchmarkDotNet diagnoser in V1.

Reasons:

1. The diagnoser, like the CLI, has **no bearer/scope boundary** (`docs/authorization.md:7-10`).
2. ReJIT/profiler attach/startup-hook injection is a poor fit for benchmark determinism; a feature designed to instrument every invocation during a live window materially changes the measured code path.
3. The feasibility doc already flags multi-RID native payload distribution as a major packaging burden; adding the benchmark distribution to V1 increases that surface before the core security model is proven (`docs/research/method-parameter-capture.md:134-145,291-297`).

Recommendation: keep BenchmarkDotNet out of scope until the server implementation is stable, audited, and operationally understood.

## 5. File-location safety (`docs/design/`)

`docs/design/` is the correct location for this document.

Why:

- `ToolReferenceDocParityTests` only reads **`docs/tool-reference.md`** and asserts that each *shipping* MCP tool has a dedicated `## tool_name` section there; it does **not** scan `docs/design/**` (`tests/DotnetDiagnostics.Mcp.IntegrationTests/ToolReferenceDocParityTests.cs:48-64,93-110`).
- The CLI and BenchmarkDotNet doc parity tests similarly guard their own live references, not arbitrary design docs: `CliDocParityTests` reads only `docs/cli-reference.md`, and `BenchDocParityTests` reads only `src/DotnetDiagnostics.BenchmarkDotNet/README.md` (`tests/DotnetDiagnostics.Cli.Tests/CliDocParityTests.cs:14-31`; `tests/DotnetDiagnostics.BenchmarkDotNet.Tests/BenchDocParityTests.cs:24-41`).
- Because this issue is **design-only** and the tool is **not yet implemented**, `docs/tool-reference.md` would be the wrong canonical home: that file is for the reflected shipping surface, not speculative or pre-merge contracts (`tests/DotnetDiagnostics.Mcp.IntegrationTests/ToolReferenceDocParityTests.cs:8-17`).

## Final recommendation

Ship method-parameter capture only if all of the following are true:

- it is an **`eventpipe` + literal `sensitive-parameter-read`** branch of `collect_sample`,
- the server operator has explicitly enabled **`Diagnostics:AllowMethodParameterCapture=true`**,
- the caller has explicitly acknowledged the sensitive surface with **`includeSensitiveValues=true`**,
- the target is **.NET 8+ CoreCLR**, with **no Hot Reload** and **no conflicting profiler**, and
- the result is **bounded**, **audited**, and retained only as a **10-minute live handle**.

Anything looser would undercut the repo's existing auth, handle-retention, and “one tool per concept” conventions rather than extending them.

## Sources

### This repository

- `AGENTS.md:183-199` — tool-count / discriminator guidance.
- `docs/authorization.md:7-10,12-76,182-214` — HTTP-only bearer scopes, primary vs modifier scopes, per-call confirmation, runtime tightening.
- `src/DotnetDiagnostics.Core/DiagnosticResult.cs:14-142` — standard success/error envelope shape.
- `src/DotnetDiagnostics.Core/Drilldown/IDiagnosticHandleStore.cs:3-62` — handle model, TTL, process-exit eviction.
- `src/DotnetDiagnostics.Core/Drilldown/MemoryDiagnosticHandleStore.cs:28-49,99-123,144-170` — live handle eviction semantics.
- `src/DotnetDiagnostics.Core/ProcessDiscovery/ProcessContext.cs:5-31` — runtime/version carried on successful live-process responses.
- `src/DotnetDiagnostics.Core/Security/SecurityOptions.cs:10-44` — `Diagnostics` config-section conventions.
- `src/DotnetDiagnostics.Core/Security/SensitiveDataRedactor.cs:5-18,26-54,91-137` — existing redaction policy and patterns.
- `src/DotnetDiagnostics.Core/Security/SensitiveValueGate.cs:4-37` — existing server-gate + caller-opt-in pattern.
- `src/DotnetDiagnostics.Core/UseCases/AttachGuard.cs:143-183` — structured capability/permission failure style.
- `src/DotnetDiagnostics.Core/UseCases/EventCollectionUseCases.cs:43-46,1240-1288` — 10-minute collector TTL and policy-gated EventSource precedent.
- `src/DotnetDiagnostics.Core/UseCases/HeapInspectionUseCases.cs:27-32` — 10-minute heap-handle TTL.
- `src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs:21-24,171-180,229-257,274-285,323-350` — sampler TTLs and `NotSupported`/`PermissionDenied` shapes.
- `src/DotnetDiagnostics.Core/UseCases/SymbolPathValidation.cs:46-52` — policy-shaped specific error kinds.
- `src/DotnetDiagnostics.Mcp/Program.cs:34-39,115-166` — logging and bearer-registry/config conventions.
- `src/DotnetDiagnostics.Mcp/Security/BearerPrincipal.cs:59-82` — wildcard vs literal modifier-scope checks.
- `src/DotnetDiagnostics.Mcp/Security/LegacyDiagnosticsFlagDeprecation.cs:7-29` — scope-first, legacy-flag deprecation posture.
- `src/DotnetDiagnostics.Mcp/Security/RequireScopeAttribute.cs:11-19,47-58` — `all` vs `any` semantics.
- `src/DotnetDiagnostics.Mcp/Security/ToolScopeAuthorizationFilter.cs:15-29,63-80` — per-tool audit logging and structured authorization behavior.
- `src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs:17-60,69-103,189-228` — current discriminator surface and envelope projection.
- `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs:1856-1879,1903-1946,2166-2177,3511-3573` — explicit opt-in/event-source precedent, dump approval audit precedent, sensitive-value opt-in, structured audit logging.
- `src/DotnetDiagnostics.Mcp/Tools/InspectProcessTool.cs:147-167` — runtime per-branch scope tightening.
- `src/DotnetDiagnostics.Mcp/Tools/QuerySnapshotTool.cs:139,178-190` — per-call sensitive-value opt-in and per-kind runtime gates.
- `tests/DotnetDiagnostics.Cli.Tests/NoServerReferenceTests.cs:7-23` — CLI must remain Core-only.
- `tests/DotnetDiagnostics.Mcp.IntegrationTests/ToolReferenceDocParityTests.cs:8-17,48-64,93-110` — `docs/tool-reference.md` parity scope; `docs/design/` is out-of-band.
- `tests/DotnetDiagnostics.Mcp.IntegrationTests/ToolScopeAttributesTests.cs:49-65,102-113,219-230` — scope-registry semantics and modifier-scope literal-membership expectations.

### Feasibility spike this design follows

- `docs/research/method-parameter-capture.md:10-20` — feature is feasible but security-sensitive.
- `docs/research/method-parameter-capture.md:88-106` — `.NET 8+` support envelope.
- `docs/research/method-parameter-capture.md:191-215` — security/operator-risk implications.
- `docs/research/method-parameter-capture.md:217-287` — MVP shape, `collect_sample(kind="method-params")`, bounded window, allowlisted methods, capability gates.
- `docs/research/method-parameter-capture.md:291-323` — remaining cost/risk and follow-up issues.
