# RFC 0002 — Tool surface consolidation

- **Tracking issue:** [#197](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/197)
- **Status:** Draft
- **Author:** Copilot (drafting on behalf of @pedrosakuma)

## 1. Context

### 1.1 Why this RFC exists

`dotnet-diagnostics-mcp` now registers **34** `[McpServerTool]`s. That is well above the
"≤10 typical" guidance called out in `AGENTS.md`'s _One MCP tool per concept_ section.
The repo's standing guidance has been: tool count >10 is acceptable when the concepts are
truly distinct, but revisit the surface once there is a real consolidation opportunity.
Phase 8 / Wave 1 is that opportunity.

The current cost is real in three places:

1. **LLM context tax.** Every extra tool description steals prompt budget from the actual
   investigation.
2. **Selection mistakes.** Several current tools are siblings that differ only by artifact
   kind (`query_*`, `collect_*`, `inspect_*`, `get_*_bytes`).
3. **Client churn.** SDK clients and orchestrators that consume the tool list as JSON Schema
   now carry a larger-than-necessary surface area.

This RFC proposes a consolidation path from **34 → 15** without dropping capability.
The stretch goal of **≤10** is intentionally _not_ the recommendation for Wave 1 because it
would require merges that cross meaningful side-effect boundaries.

### 1.2 Inputs considered

This RFC is grounded in:

- `AGENTS.md` — especially the _One MCP tool per concept_ guidance.
- `docs/rfcs/0001-per-tool-authorization-scopes.md` — format/voice reference and the current
  34-tool inventory baseline.
- `docs/tool-reference.md` — current public-facing tool contracts.
- `docs/central-orchestrator-design.md` — orchestrator boundaries.
- `docs/handoff-contract.md` — external stability constraints for MethodIdentity and byte fetch.
- `src/DotnetDiagnostics.Mcp/Tools/DiagnosticTools.cs`
- `src/DotnetDiagnostics.Mcp/Tools/OrchestratorTools.cs`

### 1.3 Design constraints

1. **Do not break the `MethodIdentity` / byte-fetch handoff story.**
   `dotnet-assembly-mcp` and `dotnet-native-mcp` depend on payload shapes and hints such as
   `(moduleVersionId, metadataToken)` and the byte-fetch envelopes. Tool-name churn is allowed;
   contract churn is not.
2. **Do not merge across destructive boundaries unless the verb still stays obvious.**
   `attach_to_pod`, `detach_from_pod`, and `collect_process_dump` remain distinct for this reason.
3. **Prefer "single tool + discriminator" only when the server already dispatches by kind.**
   This is already true for `query_collection`, `query_heap_snapshot`, `query_thread_snapshot`,
   and the heap snapshot handle store.
4. **Keep well-scoped workflow tools intact.**
   `start_investigation`, `export_investigation_summary`, and `compare_to_baseline` are already
   conceptually clean and should not be folded into larger polymorphic tools.

## 2. Current inventory (34 tools)

### 2.1 Inventory method

The authoritative tool count comes from:

```bash
grep -rn '\[McpServerTool\b' src/DotnetDiagnostics.Mcp/Tools/
```

Doc/test touch-points below were gathered by exact-name search on the current branch.
They should be read as **direct references**, not a complete dependency graph; helper-level
coverage may exist even when a tool name is not mentioned literally.

### 2.2 Inventory by concept

#### Process bootstrap (5)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `list_dotnet_processes` | `DiagnosticTools.cs:42` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-k8s-design.md`, `docs/central-orchestrator-design.md`, `docs/client-setup.md`, `docs/cloud-integrations-design.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs`, `KubernetesPodAttachOrchestratorTests.cs`, `KindIntegrationTests.cs`, `ToolScopeIntegrationTests.cs` |
| `get_process_info` | `DiagnosticTools.cs:75` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-orchestrator-design.md`, `docs/investigation-playbooks.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |
| `get_diagnostic_capabilities` | `DiagnosticTools.cs:114` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/investigation-playbooks.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |
| `get_container_signals` | `DiagnosticTools.cs:162` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `DepthContractTests.cs`, `McpToolsTests.cs` |
| `get_memory_trend` | `DiagnosticTools.cs:269` | docs: `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |

#### EventPipe collectors (5)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `snapshot_counters` | `DiagnosticTools.cs:355` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-orchestrator-design.md`, `docs/cloud-integrations-design.md`, `docs/consumer-install.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `InvestigationPlannerTests.cs`, `DepthContractTests.cs`, `McpToolsTests.cs`, `InvestigationProxyCallToolFilterTests.cs`, `InvestigationProxyEndpointTests.cs`, `StdioTransportSmokeTests.cs`, `ToolGuardTests.cs`, `ToolScopeAttributesTests.cs` |
| `collect_exceptions` | `DiagnosticTools.cs:1089` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/consumer-install.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `InvestigationPlannerTests.cs`, `DepthContractTests.cs`, `McpToolsTests.cs` |
| `collect_gc_events` | `DiagnosticTools.cs:1161` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/consumer-install.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `DepthContractTests.cs`, `McpToolsTests.cs` |
| `collect_activities` | `DiagnosticTools.cs:1229` | docs: `README.md`, `docs/consumer-install.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `McpToolsTests.cs` |
| `collect_event_source` | `DiagnosticTools.cs:1293` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/consumer-install.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `InvestigationPlannerTests.cs`, `DepthContractTests.cs`, `McpToolsTests.cs` |

#### Sampling (3)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `collect_cpu_sample` | `DiagnosticTools.cs:443` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-orchestrator-design.md`, `docs/cloud-integrations-design.md`, `docs/consumer-install.md`, `docs/handoff-contract.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `MethodIdentityHandoffTests.cs`, `DepthContractTests.cs`, `McpToolsTests.cs`, `InvestigationProxyCallToolFilterTests.cs`, `SymbolPathSecurityTests.cs`, `ToolScopeAttributesTests.cs`, `ToolScopeIntegrationTests.cs` |
| `collect_allocation_sample` | `DiagnosticTools.cs:580` | docs: `docs/aot-coverage.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |
| `collect_off_cpu_sample` | `DiagnosticTools.cs:722` | docs: `README.md`, `docs/aot-coverage.md`, `docs/cloud-recipes-design.md`, `docs/consumer-install.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`, `docs/windows-sidecar-service.md`; tests: `McpToolsTests.cs`, `SymbolPathSecurityTests.cs` |

#### Drilldown (5)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `get_call_tree` | `DiagnosticTools.cs:659` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |
| `query_off_cpu_snapshot` | `DiagnosticTools.cs:844` | docs: `README.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |
| `query_collection` | `DiagnosticTools.cs:952` | docs: `README.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs`, `ToolScopeAttributesTests.cs`, `ToolScopeIntegrationTests.cs` |
| `query_heap_snapshot` | `DiagnosticTools.cs:1697` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |
| `query_thread_snapshot` | `DiagnosticTools.cs:2558` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `LinuxNativeThreadSnapshotInspectorTests.cs`, `DepthContractTests.cs`, `McpToolsTests.cs` |

#### Dump + heap capture (3)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `collect_process_dump` | `DiagnosticTools.cs:1415` | docs: `README.md`, `docs/README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-k8s-design.md`, `docs/cloud-integrations-design.md`, `docs/cloud-recipes-design.md`, `docs/consumer-install.md`, `docs/cross-mcp-byte-fetch-runbook.md`, `docs/investigation-playbooks.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `ByteFetchToolsTests.cs`, `McpToolsTests.cs`, `ToolScopeAttributesTests.cs`, `ToolScopeIntegrationTests.cs` |
| `inspect_dump` | `DiagnosticTools.cs:1526` | docs: `README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-k8s-design.md`, `docs/central-orchestrator-design.md`, `docs/cloud-integrations-design.md`, `docs/cloud-recipes-design.md`, `docs/consumer-install.md`, `docs/handoff-contract.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs`, `SymbolPathSecurityTests.cs` |
| `inspect_live_heap` | `DiagnosticTools.cs:1625` | docs: `README.md`, `docs/README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-k8s-design.md`, `docs/cloud-integrations-design.md`, `docs/cloud-recipes-design.md`, `docs/consumer-install.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs`, `SymbolPathSecurityTests.cs`, `ToolGuardTests.cs`, `ToolScopeAttributesTests.cs` |

#### Ptrace inspection (2)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `collect_thread_snapshot` | `DiagnosticTools.cs:2148` | docs: `README.md`, `docs/README.md`, `docs/aot-coverage.md`, `docs/bad-code-scenarios.md`, `docs/central-k8s-design.md`, `docs/cloud-integrations-design.md`, `docs/cloud-recipes-design.md`, `docs/consumer-install.md`, `docs/local-docker-sidecar.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `DepthContractTests.cs`, `McpToolsTests.cs`, `SymbolPathSecurityTests.cs` |
| `capture_method_bytes` | `DiagnosticTools.cs:2284` | docs: `docs/README.md`, `docs/aot-coverage.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |

#### Byte fetch (2)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `get_module_bytes` | `DiagnosticTools.cs:2411` | docs: `docs/central-orchestrator-design.md`, `docs/cross-mcp-byte-fetch-runbook.md`, `docs/handoff-contract.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `ByteFetchToolsTests.cs`, `McpToolsTests.cs` |
| `get_dump_bytes` | `DiagnosticTools.cs:2475` | docs: `docs/central-orchestrator-design.md`, `docs/cross-mcp-byte-fetch-runbook.md`, `docs/handoff-contract.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `ByteFetchToolsTests.cs`, `McpToolsTests.cs` |

#### Investigation workflow (3)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `start_investigation` | `DiagnosticTools.cs:2739` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`; tests: `McpToolsTests.cs` |
| `export_investigation_summary` | `DiagnosticTools.cs:2794` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`; tests: `McpToolsTests.cs`, `ToolScopeAttributesTests.cs` |
| `compare_to_baseline` | `DiagnosticTools.cs:2858` | docs: `README.md`, `docs/aot-coverage.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`; tests: `McpToolsTests.cs` |

#### Legacy job control (2)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `get_collection_status` | `DiagnosticTools.cs:2932` | docs: `README.md`, `docs/aot-coverage.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `DepthContractTests.cs`, `McpToolsTests.cs` |
| `cancel_collection` | `DiagnosticTools.cs:3054` | docs: `README.md`, `docs/aot-coverage.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `McpToolsTests.cs` |

#### Orchestrator (4)

| Tool | Source | Current consumer touch-points |
|---|---|---|
| `list_pods` | `OrchestratorTools.cs:31` | docs: `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`, `docs/tool-reference.md`; tests: `InvestigationProxyCallToolFilterTests.cs`, `KindIntegrationTests.cs`, `ToolScopeAttributesTests.cs` |
| `attach_to_pod` | `OrchestratorTools.cs:141` | docs: `docs/central-k8s-design.md`, `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`; tests: `InvestigationProxyCallToolFilterTests.cs`, `KindIntegrationTests.cs`, `ToolScopeAttributesTests.cs` |
| `detach_from_pod` | `OrchestratorTools.cs:328` | docs: `docs/rfcs/0001-per-tool-authorization-scopes.md`; tests: `InvestigationCloserTests.cs`, `InvestigationProxyCallToolFilterTests.cs`, `KindIntegrationTests.cs`, `OrchestratorToolsP4Tests.cs` |
| `list_active_investigations` | `OrchestratorTools.cs:440` | docs: `docs/central-orchestrator-design.md`, `docs/rfcs/0001-per-tool-authorization-scopes.md`; tests: `InvestigationProxyCallToolFilterTests.cs`, `OrchestratorToolsP4Tests.cs` |

### 2.3 What the inventory says

The strongest consolidation seams are already visible:

- **Five handle-driven drilldown tools** are all doing the same job: dispatching a handle to a
  kind-specific view projection.
- **Three samplers** differ mostly by `kind` and a handful of kind-specific parameters.
- **Two heap inspectors** already share the same `HeapSnapshotArtifact` and downstream query tool.
- **Two byte fetch tools** already share envelope, chunking, and cross-MCP purpose.
- **Two legacy job-control tools** exist only because MCP Tasks are not yet the universal client path.

## 3. Recommended target surface (15 tools)

### 3.1 Recommended end-state

| Target tool | Current tools absorbed / retained | Decision | Rationale |
|---|---|---|---|
| `inspect_process(view=...)` | merge `list_dotnet_processes`, `get_process_info`, `get_diagnostic_capabilities`, `get_container_signals`, `get_memory_trend` | **Merge** | These are all bootstrap/read-only process inspection verbs. They differ by projection, not by trust boundary. |
| `collect_events(kind=...)` | merge `snapshot_counters`, `collect_exceptions`, `collect_gc_events`, `collect_activities`, `collect_event_source` | **Merge** | Same EventPipe collector family, same handle-store follow-up via `query_snapshot`. `kind` carries the semantic split. |
| `collect_sample(kind=...)` | merge `collect_cpu_sample`, `collect_off_cpu_sample`, `collect_allocation_sample` | **Merge** | All are bounded-time sampling operations that emit ranked summaries plus handles. |
| `query_snapshot(handle, view, ...)` | merge `get_call_tree`, `query_collection`, `query_off_cpu_snapshot`, `query_heap_snapshot`, `query_thread_snapshot` | **Merge** | The server already dispatches by handle kind. One query verb is the cleanest mental model. |
| `inspect_heap(source=...)` | merge `inspect_dump`, `inspect_live_heap` | **Merge** | Same conceptual question, same artifact kind, same downstream query path. |
| `get_bytes(kind=...)` | merge `get_module_bytes`, `get_dump_bytes` | **Merge** | Same byte-streaming envelope, same chunking behavior, same cross-MCP consumer story. |
| `collect_process_dump` | keep | **Keep** | Destructive, approval-gated, disk-writing action. Distinct boundary. |
| `collect_thread_snapshot` | keep | **Keep** | Single-shot ptrace-backed structural snapshot; distinct from heap inspection and sample collection. |
| `capture_method_bytes` | keep | **Keep** | Very specific JIT-byte extraction workflow with dotnet-native-mcp handoff. |
| `start_investigation` | keep | **Keep** | Well-scoped planning verb; not a consolidation candidate. |
| `export_investigation_summary` | keep | **Keep** | Well-scoped portable summary export; should remain explicit. |
| `compare_to_baseline` | keep | **Keep** | Distinct diffing verb with stable JSON contract. |
| `list_orchestrator(kind=pods|investigations)` | merge `list_pods`, `list_active_investigations` | **Merge (last)** | Smallest and weakest merge, but it is the cleanest way to hit 15 without folding side-effectful orchestrator verbs together. |
| `attach_to_pod` | keep | **Keep** | Side-effectful attach remains separate. |
| `detach_from_pod` | keep | **Keep** | Side-effectful cleanup remains separate. |

### 3.2 Explicit removals from the long-term surface

`get_collection_status` and `cancel_collection` should **leave the tool surface** only after the
supported client matrix no longer needs them as the bridge for either `runAsJob=true` _or_ MCP task
polling/cancellation. They are compatibility tools, not enduring domain concepts.

That yields the recommended **15-tool** end-state:

1. `inspect_process`
2. `collect_events`
3. `collect_sample`
4. `query_snapshot`
5. `inspect_heap`
6. `get_bytes`
7. `collect_process_dump`
8. `collect_thread_snapshot`
9. `capture_method_bytes`
10. `start_investigation`
11. `export_investigation_summary`
12. `compare_to_baseline`
13. `list_orchestrator`
14. `attach_to_pod`
15. `detach_from_pod`

### 3.3 Why the stretch goal (≤10) is not recommended yet

A ≤10 surface would require at least one of these bad trades:

- collapsing `attach_to_pod` + `detach_from_pod` into an action multiplexer,
- collapsing `collect_process_dump` into a broader `inspect_runtime` super-tool,
- collapsing the investigation workflow trio into a generic summary tool,
- or folding thread snapshots and JIT-byte capture into the same ptrace verb.

All four would make the surface _smaller but less legible_. Wave 1 should stop at **15**.

## 4. Merge-by-merge decisions

### 4.1 `query_*` + `get_call_tree` → `query_snapshot(handle, view, ...)`

**Decision:** adopt.

**Why:**

- `query_collection` already dispatches by handle kind.
- `query_heap_snapshot` and `query_thread_snapshot` already behave like view routers.
- `get_call_tree` is semantically just another handle-based projection over a CPU/allocation artifact.

**Recommended contract:**

- `handle` stays mandatory.
- The server inspects the stored handle kind.
- `view` remains kind-specific.
- Unsupported handle kind or unsupported view returns the same structured failure pattern used today.
- Parameter bag stays additive: `threadId`, `address`, `topN`, `framesToHash`, `rankBy`, etc. remain
  optional and are only validated for the chosen `(kind, view)` combination.
- **Authorization must remain tied to the legacy drilldown boundary for the resolved `(handle family, origin, view)` tuple, not just one broad scope.**
  `query_snapshot` cannot collapse everything behind one permission check. Counter collection views should use
  `read-counters`; EventPipe collection views use `eventpipe`; call-tree views keep `investigation-export`;
  thread views keep `ptrace`; dump-derived heap views require `heap-read`; and live-heap views must keep
  `heap-read + ptrace`. In practice that means the handle store needs enough metadata to distinguish origins
  such as dump-vs-live heap, while the query dispatcher keeps a small authorization table for view families
  such as call-tree. For collection handles this is also a deliberate tightening versus today's coarser
  `query_collection` approximation, and it should be called out as such in the migration notes.

**Do not change:** heap/query payloads, object-address semantics, `MethodIdentity` stamping, or the
legacy drilldown authorization boundary.

### 4.2 `collect_cpu_sample` + `collect_off_cpu_sample` + `collect_allocation_sample` → `collect_sample(kind=...)`

**Decision:** adopt, but not in the first implementation wave.

**Why:**

- These are all windowed samplers returning a summary plus a handle.
- The server already routes by backend internally (`RoutingCpuSampler`, off-CPU backend choice, allocation sampler).
- LLM selection improves when the verb is stable and the differentiator is explicit (`kind="cpu"`, `kind="off-cpu"`, `kind="allocation"`).

**Risk to manage:** CPU sampling has a richer parameter set (`resolveSourceLines`, symbol path, method instantiation enrichment, Tasks/job support). The merged tool must keep kind-specific parameters grouped and clearly documented.

### 4.3 `inspect_dump` + `inspect_live_heap` → `inspect_heap(source=live|dump, ...)`

**Decision:** adopt.

**Why:**

- Both tools already produce `HeapSnapshotArtifact`.
- Both feed the same drilldown path.
- The only real discriminator is **source** (`live` vs `dump`) and the presence of `processId` vs `dumpFilePath`.

**Important guardrail:** `source="live"` must keep the current `heap-read + ptrace` requirements and the current warnings about suspend time. `source="dump"` remains offline and read-only.

### 4.4 `get_module_bytes` + `get_dump_bytes` → `get_bytes(kind=module|dump, ...)`

**Decision:** adopt.

**Why:**

- Same `ByteFetchEnvelope`.
- Same chunking and offset contract.
- Same external consumers (`dotnet-assembly-mcp`, `dotnet-native-mcp`, orchestrator proxy).

**Compatibility note:** `docs/handoff-contract.md` should keep documenting the envelope as stable even if the entry-point name changes.

### 4.5 EventPipe collectors → `collect_events(kind=...)`

**Decision:** adopt.

**Why:**

- `snapshot_counters`, `collect_exceptions`, `collect_gc_events`, `collect_activities`, and `collect_event_source`
  are already one conceptual family: windowed EventPipe captures that issue handles later read by a query tool.
- A single entry-point makes the relationship between capture and `query_snapshot` much clearer.

**Important exceptions and guardrails:**

- `kind="counters"` must preserve today's lighter `read-counters` authorization boundary; the other kinds stay under `eventpipe`.
- `kind="event-source"` remains the only case that requires provider-specific parameters such as
  `providerName`, `keywords`, and `eventLevel`, and it must keep the current provider-allowlist / unsafe-provider behavior.
- MCP task metadata is tool-level today, not `kind`-level. That means `collect_events` should advertise task
  support only after all supported kinds reach task parity, or the merge should wait until that parity work exists.

### 4.6 Process bootstrap reads → `inspect_process(view=...)`

**Decision:** adopt.

**Why:**

- These are all cheap, read-only, pre-collector inspection steps.
- They share the same `processId` optional auto-resolution story.
- The user intent is consistent: "tell me about the process before I collect anything heavy."

**Guardrails:**

- keep `view="list"` the only view that does _not_ require or use `processId`.
- preserve `get_memory_trend`'s current behavior: when the caller supplies `processId` explicitly, `view="memory-trend"`
  must continue to accept any OS PID, not just attachable .NET processes.

### 4.7 Orchestrator listing tools → `list_orchestrator(kind=pods|investigations)`

**Decision:** adopt, but make it the **last** merge and only after the more obvious consolidations land.

**Why:**

- This merge is how the RFC reaches **15** without crossing destructive boundaries.
- Both tools are read-only, paginated, and orchestrator-scoped.
- The merged tool must still authorize by `kind`: `kind="pods"` keeps `orchestrator-list`, while
  `kind="investigations"` keeps the more privileged `orchestrator-attach` boundary because it exposes
  minted investigation handles and session state.

**Why not merge `attach_to_pod` / `detach_from_pod`:**

The orchestrator design document is clear that discovery, attach, cleanup, and session introspection are distinct side-effect boundaries. `attach_to_pod` and `detach_from_pod` therefore remain explicit.

### 4.8 Legacy job-control tools

**Decision:** deprecate, then remove.

`get_collection_status` and `cancel_collection` should survive only as compatibility wrappers while
MCP Tasks become the supported async contract. Importantly, they are not just `runAsJob=true` shims today:
they also bridge MCP task IDs for non-task-aware clients. They should disappear only once the supported
client matrix no longer needs that bridge, not merely once Tasks are documented as the preferred path.

## 5. Migration policy

### 5.1 Recommendation: deprecate, then remove

Even though the project is pre-1.0, a hard cutover would be unnecessarily disruptive because some
consumers discover and cache the server's tool list programmatically.

Recommended policy:

1. **Release N (pre-1.0 minor):** add the consolidated tool(s); keep legacy tool names as aliases.
2. **Mark legacy tools deprecated in docs and tool descriptions.** Point every old tool at its successor.
3. **Release N+1 (next pre-1.0 minor):** remove legacy names whose clients have migrated; for the async bridge tools,
   removal is explicitly conditional on the supported client matrix no longer depending on `get_collection_status`
   / `cancel_collection` for task polling or cancellation.

During the overlap window the raw tool count may briefly rise above 34. That is acceptable only if the
migration waves are time-bounded and the follow-up removal release is planned up front.

This should be treated as a **minor** version bump, not a patch, because JSON-schema consumers will
see a meaningful tool-surface change even if payloads stay stable.

### 5.2 Backwards-compat horizon

Recommended horizon: **one pre-1.0 minor release** of overlap by default, with an explicit exception for the
legacy async bridge if the supported client matrix still needs it.

That is enough time for:

- MCP SDK clients that mirror the tool list to refresh their allowlists.
- Documentation (`docs/tool-reference.md`, client setup docs, runbooks) to move to the new names.
- Prompt/playbook content to stop teaching the old surface.
- Orchestrator proxies and sibling MCPs to validate that handoffs still work.
- The deliberate `query_collection` → per-kind `query_snapshot` authorization tightening to be validated against
  low-privilege consumers before removal.
- The supported client matrix to prove whether any still depend on the `get_collection_status` / `cancel_collection`
  bridge for task polling/cancellation.

### 5.3 External dependency impact

The most important downstream dependency is still the **payload contract**, not the tool name:

- `MethodIdentity` in `docs/handoff-contract.md` must remain unchanged.
- Byte-fetch envelopes must remain unchanged.
- Heap/thread/call-tree payloads should remain structurally stable across the rename/merge.

That means `dotnet-assembly-mcp` and `dotnet-native-mcp` should not need behavioral changes as long
as the consolidated tools keep the same handoff payloads and hints.

### 5.4 Semver recommendation

Because the repo is pre-1.0:

- **Adding** consolidated tools plus deprecations: next **minor**.
- **Removing** legacy aliases: following **minor**.
- Do **not** hide the change inside a patch release.

## 6. Per-merge risk analysis

| Merge | Count delta | LLM confusion risk | SDK/client break risk | Impl cost | Test churn | Token-savings / risk rank |
|---|---:|---|---|---|---|---|
| `get_module_bytes` + `get_dump_bytes` → `get_bytes` | -1 | Low | Low | Low | Low | **1** |
| `inspect_dump` + `inspect_live_heap` → `inspect_heap` | -1 | Low | Low | Low-medium | Medium | **2** |
| `query_*` + `get_call_tree` → `query_snapshot` | -4 | Medium | Medium | Medium-high | High | **3** |
| EventPipe collectors → `collect_events` | -4 | Medium-high | Medium | High | High | **4** |
| Bootstrap reads → `inspect_process` | -4 | Medium | Medium | Medium | Medium | **5** |
| Sampling tools → `collect_sample` | -2 | Medium-high | Medium | High | Medium-high | **6** |
| Remove `get_collection_status` + `cancel_collection` after Tasks | -2 | Low for LLMs, medium for legacy clients | High if clients still depend on job mode | Medium | Medium | **7** |
| `list_pods` + `list_active_investigations` → `list_orchestrator` | -1 | Medium-high | Medium | Medium | Medium | **8** |

### 6.1 Commentary by merge

- **Highest-confidence wins:** `get_bytes` and `inspect_heap` should go first. They save less, but their shape is already nearly unified.
- **Biggest absolute savings:** `query_snapshot`, `collect_events`, and `inspect_process` each save four tool slots and should deliver the biggest context win.
- **Most subtle merge:** `collect_sample` is valuable, but parameter sprawl makes it the easiest place for an LLM to pass the wrong option to the wrong backend.
- **Most optional merge:** `list_orchestrator` has the worst savings-to-risk ratio. It is recommended only because it gets the end-state to 15 without crossing destructive boundaries.

## 7. Implementation plan (outline only)

This RFC should be followed by a single meta-issue and then sub-issues. Do **not** open the sub-issues from this PR; the breakdown below is the template.

### 7.1 Meta-issue

**Title:** Phase 8 — tool surface consolidation implementation

**Checklist buckets:**

1. shared compatibility scaffolding
2. low-risk pair merges
3. unified query path
4. unified collector paths
5. bootstrap consolidation
6. async/task cutover
7. orchestrator cleanup
8. docs + alias removal

### 7.2 Sub-issue breakdown

1. **Compatibility scaffolding**
   - add deprecation metadata/support in tool descriptions and docs
   - add centralized argument dispatch helpers for discriminated tools
   - teach the handle store to persist enough origin metadata (for example live-vs-dump heap) and expose a small legacy-boundary authorization table so merged query tools can authorize correctly
   - add test helpers that assert both legacy and successor entry-points during overlap
2. **Low-risk pair merges**
   - implement `inspect_heap`
   - implement `get_bytes`
   - keep legacy aliases
3. **Unified query path**
   - implement `query_snapshot`
   - migrate heap/thread/off-cpu/collection/call-tree handles
   - move docs/playbooks to the new query verb
4. **Unified EventPipe collector**
   - implement `collect_events(kind=...)`
   - migrate `snapshot_counters`, exceptions, GC, activities, event source
5. **Unified sampling collector**
   - implement `collect_sample(kind=...)`
   - preserve CPU-specific enrichment options and off-CPU symbol-path behavior
6. **Bootstrap consolidation**
   - implement `inspect_process(view=...)`
   - retire the five bootstrap tools behind aliases
7. **Async cutover**
   - update docs/prompts to prefer MCP Tasks
   - deprecate `runAsJob=true`
   - remove `get_collection_status` and `cancel_collection` only after the overlap window ends **and** the
     supported client matrix no longer needs the polling/cancellation bridge
8. **Orchestrator list consolidation**
   - implement `list_orchestrator(kind=...)`
   - keep `attach_to_pod` and `detach_from_pod` distinct
9. **Alias removal wave**
   - remove legacy tool names
   - trim docs/test matrices to the consolidated surface
   - update `AGENTS.md` tool-count note

### 7.3 Recommended execution order

1. `get_bytes`
2. `inspect_heap`
3. `query_snapshot`
4. `collect_events`
5. `inspect_process`
6. `collect_sample`
7. async/task cutover
8. `list_orchestrator`
9. alias removal

This order front-loads the best savings-to-risk work and postpones the weakest merge until the rest of the target surface is already real.

## 8. Final recommendation

Adopt a **34 → 15** plan built around **seven** consolidations plus **two** legacy-tool retirements:

- `inspect_process`
- `collect_events`
- `collect_sample`
- `query_snapshot`
- `inspect_heap`
- `get_bytes`
- `list_orchestrator`
- remove `get_collection_status`
- remove `cancel_collection`

If the team decides not to merge the orchestrator listing tools, the realistic fallback is **16**, not 15.
The strict 15-tool target therefore assumes `list_orchestrator` lands as the final consolidation wave.

Keep these explicit because they are already well-scoped or side-effectful:

- `collect_process_dump`
- `collect_thread_snapshot`
- `capture_method_bytes`
- `start_investigation`
- `export_investigation_summary`
- `compare_to_baseline`
- `attach_to_pod`
- `detach_from_pod`

That gets the tool surface back into a size that is defensible for an LLM, without pretending that every distinct diagnostic action is "just another view."
