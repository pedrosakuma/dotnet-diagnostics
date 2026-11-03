# Operational MCP evaluator replay reliability gate

Issue: [#1364](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1364)

Parents: [#1348](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1348),
[#1350](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1350)

Frozen evidence source:
[`operational-mcp-allocation-field-isolation-v7.md`](./operational-mcp-allocation-field-isolation-v7.md)

## Scope

This is an evaluator reliability gate, not a new allocation experiment. It does
not create or execute Protocol 8, rerun Protocol 7, alter Protocols 1 through 7,
or reinterpret the frozen Protocol 7 result. The Protocol 7 decision remains
**INCONCLUSIVE** and its four incomplete held-out attempts remain in their
original denominators.

The gate answers two infrastructure questions from the one-time Protocol 7 run:

1. why two assessor responses failed with
   `Unexpected assessment property 'text'.`; and
2. why two otherwise complete investigations reported 24 and 18 unresolved
   citation locations.

## Recovered frozen evidence

The original evaluator-private Protocol 7 report was still present in the
producing session's artifact storage. Its SHA-256 was rechecked as
`9faaf9bea6c749ad065956f19eaf6c4a7a92a41a36fa4fa739d018b57a997bdf`,
matching the frozen Protocol 7 documentation.

No empirical command, target workload, model request, assessor request, capture,
or MCP call was rerun. The report was used read-only to recover the exact 42
failed citation locations. It could not recover the two rejected assessor
bodies: those attempts retained neither the response body nor its hash, byte
count, JSON path, or value kind.

## Citation root cause

Every one of the 24 and 18 failed locations used the exact form:

```text
tool-result://<call-id>#/json/<pointer>
```

The isolated Copilot CLI transport instructed the diagnosing model with the
example `tool-result://call-id#/json/pointer`. The resolver, however, accepted
only the retained JSON document root and RFC 6901 pointers below that root. It
did not recognize the transport's virtual `json` envelope segment.

Removing exactly the first `/json` segment made all 42 locations resolve
against the model-visible JSON retained in the original report. No other
normalization, case folding, substring search, value matching, or fuzzy lookup
was required.

The resolver now has two explicit syntaxes:

- direct retained-document pointers, including `#`, `#/`, and
  `#/data/...`; and
- the exact Copilot CLI virtual-envelope form `#/json` or `#/json/...`.

A real top-level property named `json` has precedence. The virtual envelope is
considered only when direct RFC 6901 resolution fails. Invalid escapes, missing
tool-call ids, ambiguous ids, malformed retained JSON, missing properties,
out-of-range indices, and traversal through scalar values remain failures.

The deterministic replay fixture copies all 42 frozen locations but uses
synthetic evidence values:
[`operational-mcp-citation-replay-v7.fixture.json`](../../tests/DotnetDiagnostics.ScenarioEvaluation.Tests/OperationalMcp/operational-mcp-citation-replay-v7.fixture.json).
Positive tests cover both roots, direct and envelope pointers, arrays, and RFC
6901 `~0`/`~1` escapes. Negative tests preserve the distinction between invalid
locations, unavailable artifacts, locations outside retained evidence, invalid
retained artifacts, and ambiguous artifact identities.

## Assessor evidence gap and new observability

The original `text` shapes are unreconstructable. The frozen report proves only
that strict validation encountered an unexpected property whose leaf name was
`text`; it does not establish whether either property was at the root, inside a
claim, inside an unsupported-certainty finding, or another object, and it does
not establish the value kind. No shape is inferred from that missing evidence.

The conformance fixture therefore labels its root-string and nested-array
`text` cases as synthetic equivalents, not reconstructions. They prove that a
future rejection records:

- the full unambiguous JSON path;
- property name and JSON value kind;
- parse-failure category;
- protocol fingerprint and attempt identity;
- SHA-256 and exact UTF-8 byte counts for both the normalized raw transport
  decision and assessor content;
- a bounded canonical replay JSON value;
- retained byte count plus truncation and redaction state.

The canonical replay JSON preserves object/array shape, property names, JSON
value kinds, schema indices, frozen enum vocabulary, and equality relationships
between strings. Arbitrary string content is replaced with stable synthetic
tokens; unrelated numeric and Boolean values are canonicalized. This retains
the structure needed to replay strict-schema failures without persisting
target-controlled names, paths, stacks, symbols, prompts, credentials, or
unrestricted assessor prose.

Canonical replay evidence is capped at 16 KiB and also limited by the attempt's
remaining artifact budget. If the canonical response exceeds the cap, a minimal
failure envelope retains the category, path, property, and value kind. If even
that envelope cannot fit, no replay JSON is retained, but hashes, exact source
byte counts, and the explicit truncation state remain.

## Accounting and diagnostics

Each attempt now records:

```text
retainedArtifactBytes =
    retained MCP exchange bytes
  + retained assessor-failure replay bytes
```

The same helper enforces the attempt artifact limit and rejects an over-budget
sum. Successful assessments add zero replay bytes. Pilot accounting sums the
preflight replay evidence, if any, plus each attempt's exact retained total.

Citation assessment keeps the exact failure count and original invalid-location
list. The diagnostic projection is separately bounded to 64 failures and 16
retained evidence namespaces, with explicit truncation flags. Each projected
failure carries its category, bounded detail, and considered
`tool-result://<call-id>#` namespace; no unresolved citation is converted into
success.

## Gate result

The citation failures were a deterministic resolver defect caused by the
transport's exact virtual-envelope syntax, and the resolver correction is
limited to that syntax. The assessor failure bodies remain unavailable, so no
new additive `text` policy is justified. Root and nested assessment objects
remain strict, `nextSteps` remains the only Protocol 7 additive property, and
future malformed or contradictory outputs remain failed attempts.

This gate establishes deterministic replay and sufficient future
observability. It does not make the four Protocol 7 attempts retrospectively
assessable and does not authorize an allocation product change or a new
empirical protocol by itself.
