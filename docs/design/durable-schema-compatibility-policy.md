# Durable schema compatibility policy proposal

**Status:** Proposed requirements for [#1006](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1006);
maintainer decision pending. This document does not freeze a v2 schema, implement
compatibility, report a demonstration, select a backend, or authorize DC6.

**Tracking:** [#999](https://github.com/pedrosakuma/dotnet-diagnostics/issues/999),
[#1003](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1003), and
[#1006](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1006).

The SQLite-direct and append-first candidates are accepted independently as
internal, test-only, unregistered experiment components. SQLite-direct stores
canonical rows in SQLite. Append-first stores canonical framed JSON records and
builds its derived SQLite query index before sealing; it is not a custom
non-SQLite index design. Neither currently demonstrates backward reading:
their exact version and adapter-identity checks are appropriate spike guards,
not compatibility evidence. The unchanged DC3 35-case campaign has not run,
and its aggregate 256 MiB apparent-file-length bound, including SQLite
temporary/journal files and recovery work, remains unproven.

## Independent version axes

A durable package must identify these axes separately; equality on one must not
stand in for compatibility on another:

| Axis | Meaning |
| --- | --- |
| Package contract version | Manifest, seal, lifecycle, integrity, identity, and derivation semantics |
| Representation kind and record-format version | Canonical record encoding and logical field semantics |
| Writer/application build version | The implementation build that emitted the package; provenance, not a schema |
| Adapter identity/version | The physical backend implementation and its declared canonical representation |
| Derived-index schema version | Rebuildable query acceleration derived from canonical records |
| Minimum reader contract version | The writer's declared reader-contract floor; not proof that any reader supports the record format |
| Executing reader version | The build that interpreted or converted the evidence, reported in results and derivation provenance |

Units, meanings, clock domains/origins, ordering, missingness, reset state, and
aggregation rules are semantic schema. A DDL migration alone cannot make a
change to any of them compatible.

## Proposed support window

For the first approved durable representation, a current reader should support
exactly the current frozen record format and the immediately preceding frozen
record format for that representation kind, once a preceding format exists.
The window counts record formats, not application releases. Pre-freeze
experimental formats are excluded. Additional versions require an explicit
maintainer decision; dropping any previously supported format, including when
advancing the rolling window, requires a separately recorded compatibility
decision and evidence review.

This two-format rolling window is the minimum proposal for #1006, not an
indefinite-support promise. It does not promise that old readers accept future
formats. Package-contract majors, representation kinds, and required feature
sets remain explicit allowlists even when a numeric version falls in the
window.

A reader may ignore an unknown optional field only when the governing frozen
format documents that field class as ignorable and omission cannot change the
meaning of known fields. It must fail closed for unknown required fields,
required features, incompatible semantics, or unsupported versions. Missing
historical data stays unavailable/unknown; readers must not map absence to zero,
an empty value, or a value reconstructed from a derived index.

## Read, reindex, and conversion boundaries

Ordinary reopen is read-only. It validates the seal and declared members, then
interprets explicitly supported formats without migrating canonical records,
rebuilding indexes, changing file inventories or hashes, or updating seals.

Persistent reindex and record conversion are explicit derived-package
operations under the same bounded staging, finalization, publication,
retention, and recovery lifecycle as capture:

- **Reindex** reads unchanged canonical records and writes a new derived package
  with new capture/artifact IDs, source hashes, executing reader/tool version,
  derivation reason, and a new index-schema version. It cannot recover metadata
  that the canonical source never captured.
- **Record conversion** interprets a supported source format and writes a new
  canonical representation version plus any derived index, again with new IDs
  and complete provenance. It never rewrites or reseals the source.

Both operations charge source/destination overlap, canonical output, indexes,
SQLite journals/WAL, temporary files, conversion space, and recovery output to the applicable
aggregate apparent-file-length budget. A failed or interrupted derivation must
not alter the source or appear as a sealed successful package.

## Prospective v1-to-v2 demonstration

This demonstration is separately scoped from the frozen DC3 campaign, budgets,
fixtures, oracles, and no-retry protocol. No benchmark or v2 freeze is added by
this proposal.

The demonstration should freeze a v1 package fixture independently before the
v2 writer exists; the current writer must not regenerate or normalize that
fixture. A proposed v2 then adds one deliberately optional metadata field whose
absence is distinguishable from a present zero/empty value and does not change
v1 record semantics.

| Case | Required acceptance |
| --- | --- |
| Frozen v1 opened by current reader | Opens under an explicit v1 support entry; all logical queries are deterministic; the new metadata is reported unavailable, not zero/empty/defaulted |
| Independently written v2 opened by current reader | Preserves all v1 logical results and returns the optional metadata when present |
| Different writer builds using the same supported format | Build identity remains provenance; it does not alone make the package incompatible |
| Unknown optional field allowed by the frozen rule | Known logical results remain deterministic; result/provenance records the current reader |
| Unsupported future format, required field/feature, or reinterpretation of unit/meaning/clock/aggregation | Fails closed with a typed unsupported-semantics/version result before querying; legitimate per-record metadata variation within a supported format is not a schema break |
| Ordinary reopen of v1 and v2 | Before/after canonical hashes, sealed member file set, lengths, and seals are identical; no index rebuild or auxiliary file creation |
| Index-only derivation | Publishes a new bounded derived package with new IDs and provenance; canonical source is unchanged; absent v1 metadata remains unavailable |
| Record conversion | Publishes a new bounded derived package and canonical version with new IDs/provenance; source bytes remain unchanged |
| Repeat reads | Typed logical results, ordering, missingness, and availability states are deterministic and identify the executing reader version |

Acceptance evidence must include the frozen prior-version fixture origin and
hashes, source file-set/hash assertions before and after every case, expected
typed results, failure codes, derivation manifests, and bounded-lifecycle
accounting. Passing only SQL open/DDL checks, adapter identity equality, or
same-version round trips is insufficient.

## Decision gate

Before an explicit DC6 go, maintainers must review a completed compatibility
result and decide the support window, format evolution rules, fixture custody,
and removal policy. Compatibility maintenance effort and failure surface are
backend-selection factors; this proposal does not claim that either candidate
wins. Production approval, public API placement, and durability claims remain
separate decisions.
