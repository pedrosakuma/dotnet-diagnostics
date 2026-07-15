# MCP tool-catalog context budget

**Issue:** #628 · **Measured:** 2026-07-15 · **SDK:** ModelContextProtocol 1.4.0

## Result

The maximal live registration surface (orchestrator and Azure discovery enabled)
contains 16 tools. Its serialized `tools/list` result is:

- **199,760 UTF-8 bytes**
- **approximately 49,940 tokens**, using the explicit, tokenizer-neutral estimate
  of four UTF-8 bytes per token
- **177,577 bytes / approximately 44,395 tokens** for the default 12-tool subset
  when the four opt-in orchestrator/Azure tools are excluded

The measurement covers the `ListToolsResult` JSON object, including authorization
metadata. It excludes the JSON-RPC envelope because its request id is
client-dependent and contributes only a small fixed overhead.

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
| `collect_events` | 63,351 | 10,940 | 51,417 | 9,818 | 53,261 | 272 |
| `collect_sample` | 18,917 | 6,675 | 11,571 | 5,629 | 13,033 | 255 |
| `collect_thread_snapshot` | 17,824 | 2,160 | 14,501 | 2,518 | 15,045 | 261 |
| `inspect_process` | 17,625 | 2,729 | 14,194 | 2,760 | 14,596 | 269 |
| `query_snapshot` | 10,129 | 7,387 | 2,021 | 6,502 | 3,313 | 314 |
| `start_investigation` | 9,514 | 1,843 | 6,187 | 2,271 | 6,973 | 270 |
| `export_investigation_summary` | 8,408 | 1,573 | 5,677 | 1,894 | 6,274 | 240 |
| `inspect_heap` | 7,701 | 3,503 | 2,021 | 4,701 | 2,747 | 253 |
| `capture_method_bytes` | 7,676 | 2,176 | 3,818 | 3,077 | 4,341 | 258 |
| `list_orchestrator` | 7,571 | 2,339 | 4,097 | 2,490 | 4,794 | 287 |
| `discover_azure` | 6,093 | 772 | 4,464 | 939 | 4,894 | 260 |
| `get_bytes` | 5,966 | 2,204 | 2,021 | 3,098 | 2,611 | 257 |
| `collect_process_dump` | 5,740 | 1,630 | 2,653 | 2,438 | 3,031 | 271 |
| `attach_to_pod` | 4,731 | 978 | 2,656 | 1,408 | 3,059 | 264 |
| `compare_to_baseline` | 4,703 | 1,814 | 2,021 | 1,972 | 2,500 | 231 |
| `detach_from_pod` | 3,784 | 256 | 2,518 | 885 | 2,634 | 265 |
| **All tools** | **199,733** | **48,979** | **131,837** | **52,400** | **143,106** | **4,227** |

The remaining 27 bytes are catalog framing and array separators.

\* Input/output schema columns are serialized schema-value sizes and include
descriptions, so they overlap the prose column.

## Schema versus prose

The exact, non-overlapping partition removes properties in a fixed order:

1. remove every `title` and `description` recursively; the byte delta is prose;
2. remove `inputSchema` and `outputSchema`; the next delta is schema structure;
3. the remainder is names, annotations, execution/auth metadata, and punctuation.

This gives:

- **143,106 bytes (71.6%) schema structure**
- **52,400 bytes (26.2%) prose**
- **4,227 bytes (2.1%) other per-tool metadata**
- **27 bytes catalog framing**

The catalog is therefore primarily a schema-shape cost, not simply verbose tool
descriptions. `collect_events` alone contributes 31.7%; the four largest tools
contribute 58.9%. Its 51,417-byte output schema is the dominant single payload.
Removing safety prose would not address the main cost.

## Guidance placement

Potential future reductions should be evidence-driven:

- Repeated `processId` auto-selection and `investigationHandleId` routing text is
  shared guidance, but each parameter still needs enough local semantics for a
  client that sees one tool in isolation.
- Exhaustive workflow examples and cross-tool navigation can live in existing
  prompts/resources/results. Valid discriminator values, required combinations,
  authorization requirements, and defaults must remain discoverable in schemas.
- Dump approval, ptrace/UID requirements, sensitive-value gates, remote symbol
  allowlisting, and target-suspension warnings are safety controls, not trimming
  candidates.
- Large generated output schemas should be revisited only when the stable MCP
  protocol/SDK offers a portable composition or deferred-schema mechanism.
  Hand-written preview-specific schema tricks would create migration debt.

No descriptions were shortened as part of this issue.

## Guardrail

The integration test caps the maximal catalog at **220,000 bytes**. This is
20,240 bytes, or 10.1%, above the measured baseline. The fixed byte budget is
portable, deterministic, and independent of model tokenizer changes.

An intentional increase must update both the test baseline comment and this
document with a fresh live measurement and rationale. The guardrail is not a
mandate to delete safety or argument semantics; exceeding it should first prompt
inspection of generated output-schema growth and accidental new surface area.

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
