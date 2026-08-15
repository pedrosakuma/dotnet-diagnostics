# MCP tool-catalog context budget

**Issue:** #791 (refreshing #628) · **Measured:** 2026-08-03

**Source:** `624e7948cf8461df51b10940c201fa8ee0ee9ef6`
(`v0.22.0-3-g624e794`) · **MCP SDK:** ModelContextProtocol 1.4.0

**Runtime:** .NET SDK 10.0.302 (`global.json` roll-forward), Linux x64

## Result

The maximal live registration surface (orchestrator and Azure discovery enabled)
contains 17 tools. The normal surface with both configuration gates disabled
contains 13 tools.

**Maximal tools:** 17 · **Default tools:** 13

The serialized `tools/list` results are:

- **258,477 UTF-8 bytes** for the maximal 17-tool catalog
- **approximately 64,620 tokens**, using the explicit, tokenizer-neutral estimate
  of four UTF-8 bytes per token
- **226,320 bytes / approximately 56,580 tokens** for the default 13-tool catalog

The measurement covers the `ListToolsResult` JSON object, including authorization
and safety metadata. It excludes the JSON-RPC envelope because its request id is
client-dependent and contributes only a small fixed overhead.

The maximal test sets `Orchestrator__Enabled=true` and
`AzureDiscovery__Enabled=true`. The default surface excludes the three
orchestrator-gated tools (`attach_to_pod`, `detach_from_pod`,
`list_orchestrator`) and the Azure-gated `discover_azure`. `collect_batch` is
part of both surfaces.

## Reproduce

Run from the repository root:

```bash
dotnet test tests/DotnetDiagnostics.Mcp.IntegrationTests/ \
  -c Release \
  --filter FullyQualifiedName~ToolCatalogBudgetTests \
  --logger "console;verbosity=detailed"
```

`ToolCatalogBudgetTests` starts the real ASP.NET Core server with every shipping
tool surface enabled, calls `tools/list` through the MCP client, serializes the
returned protocol models with the pinned SDK's `McpJsonUtilities.DefaultOptions`,
and prints this table:

| Tool | Total bytes | Input schema* | Output schema* | Prose | Schema structure | Other metadata |
|---|---:|---:|---:|---:|---:|---:|
| `collect_events` | 70,734 | 12,314 | 55,170 | 11,347 | 57,192 | 2,195 |
| `collect_sample` | 23,819 | 7,106 | 14,023 | 6,513 | 15,219 | 2,087 |
| `inspect_process` | 22,915 | 3,679 | 17,230 | 4,205 | 17,456 | 1,254 |
| `collect_thread_snapshot` | 21,502 | 2,722 | 16,182 | 3,837 | 16,460 | 1,205 |
| `export_investigation_summary` | 13,956 | 1,924 | 9,851 | 2,744 | 10,215 | 997 |
| `query_snapshot` | 13,330 | 8,520 | 2,585 | 8,144 | 3,701 | 1,485 |
| `start_investigation` | 10,642 | 1,843 | 6,751 | 2,739 | 7,280 | 623 |
| `inspect_heap` | 10,316 | 3,934 | 2,585 | 5,558 | 3,045 | 1,713 |
| `list_orchestrator` | 9,897 | 2,406 | 5,439 | 3,290 | 5,751 | 856 |
| `capture_method_bytes` | 9,537 | 2,607 | 4,382 | 3,999 | 4,639 | 899 |
| `attach_to_pod` | 9,001 | 3,038 | 3,489 | 4,159 | 3,895 | 947 |
| `get_bytes` | 8,407 | 2,635 | 2,585 | 4,147 | 2,909 | 1,351 |
| `discover_azure` | 7,952 | 1,203 | 5,028 | 1,839 | 5,192 | 921 |
| `collect_process_dump` | 7,348 | 1,630 | 3,217 | 3,010 | 3,210 | 1,128 |
| `collect_batch` | 7,259 | 1,487 | 3,847 | 2,481 | 3,955 | 823 |
| `compare_to_baseline` | 6,531 | 1,816 | 2,585 | 2,634 | 2,679 | 1,218 |
| `detach_from_pod` | 5,303 | 256 | 3,082 | 1,756 | 2,813 | 734 |
| **All tools** | **258,449** | **59,120** | **158,031** | **72,402** | **165,611** | **20,436** |

The remaining 28 bytes are catalog framing and array separators.

\* Input/output schema columns are serialized schema-value sizes and include
descriptions, so they overlap the prose column.

## Schema versus prose

The exact, non-overlapping partition removes properties in a fixed order:

1. remove every string-valued `title` and `description` annotation recursively;
   property schemas whose names happen to be `title` or `description` are
   preserved; the byte delta is prose;
2. remove `inputSchema` and `outputSchema`; the next delta is schema structure;
3. the remainder is names, annotations, execution/auth metadata, and punctuation.

This gives:

- **165,611 bytes (64.1%) schema structure**
- **72,402 bytes (28.0%) prose**
- **20,436 bytes (7.9%) other per-tool metadata**
- **28 bytes catalog framing**

The catalog is therefore primarily a schema-shape cost, not simply verbose tool
descriptions. `collect_events` alone contributes 27.4%; the four largest tools
contribute 53.8%. Its 55,170-byte output schema is the dominant single payload.
Removing safety prose would not address the main cost.

## Why the catalog grew

The stale 2026-07-15 report predated `collect_batch`, proxy/delegated-scope
guidance, and the current invocation-safety contract. `collect_batch` now adds
7,259 bytes (2.8% of the maximal catalog) and is intentionally part of the
default surface.

The largest later increase is deliberate safety metadata. The pre-safety
Phase 16 baseline was 220,804 bytes. Issue #773 raised its measured baseline to
258,367 bytes, an increase of 37,563 bytes (17.0%), by adding:

- each tool's static maximum resolved-risk summary and conditional-safety flag
  under `_meta.dotnetDiagnostics.safety`;
- compact `safety`, `safetyWarnings`, and `safetyApproval` result fields; and
- the reserved acknowledgement input schema only for tools that can resolve to
  high or critical risk.

The fresh measurement is 110 bytes above that issue #773 baseline. These fields
are client-visible safety controls, not schema-trimming candidates. This issue
only corrects measurement and documentation parity; it does not optimize schemas
or alter tool behavior.

## Guidance placement

Potential future reductions should be evidence-driven:

- Repeated `processId` auto-selection and `investigationHandleId` routing text is
  shared guidance, but each parameter still needs enough local semantics for a
  client that sees one tool in isolation.
- Exhaustive workflow examples and cross-tool navigation can live in existing
  prompts/resources/results. Valid discriminator values, required combinations,
  authorization requirements, defaults, and safety controls must remain
  discoverable in schemas.
- Dump approval, ptrace/UID requirements, sensitive-value gates, remote symbol
  allowlisting, and target-suspension warnings are safety controls, not trimming
  candidates.
- Large generated output schemas should be revisited only when the stable MCP
  protocol/SDK offers a portable composition or deferred-schema mechanism.
  Hand-written preview-specific schema tricks would create migration debt.

No descriptions were shortened as part of this issue.

## Guardrails

The integration test caps the maximal catalog at **280,000 bytes**. Issues #828
(`collect_sample(kind="cpu-efficiency")`) and #830
(`collect_sample(kind="native-lock-contention")`) each added a new kind
discriminator value, parameter, and description to the already-large
`collect_sample` schema; combined, the measured catalog is 271,316 bytes.
280,000 restores roughly 3% headroom above that measured baseline. The fixed
byte budget is portable, deterministic, and independent of model tokenizer
changes.

An intentional increase must update both the test baseline comment and this
document with a fresh live measurement and rationale. The guardrail is not a
mandate to delete safety or argument semantics; exceeding it should first prompt
inspection of generated output-schema growth and accidental new surface area.

`ToolReferenceDocParityTests` also derives the maximal and default counts from
`PodLocalToolSurfaces` and requires this document's cardinality stamp to match.
Adding, removing, or re-gating a tool therefore cannot silently leave this
research artifact with stale counts.

## MCP 2026 coordination

The helper uses only the current stable public protocol models and serializer.
It does not depend on the 2.x preview, `server/discover`, or draft-only APIs.
If the MCP 2026 migration changes discovery framing, the measurement can move at
one serialization seam and be rebaselined. Per-tool payload measurements remain
useful regardless of whether discovery is initiated by `tools/list` or a future
mechanism.

## Model-selection benchmark limitation

The repository cannot currently run a reproducible strong-model versus
small-model selection benchmark. Such a test would require external credentials,
model/version pinning, stable provider behavior, and repeated stochastic trials;
it would not be deterministic CI evidence. Existing lexical discoverability tests
continue to protect important trigger phrases. This report deliberately does not
invent an accuracy score from unrepeatable manual prompts.
