# DC5 monitored comparison: readiness blocked, no scored executions

## Result

**No backend recommendation.** The revision-4 comparison was not admitted.
All 35 planned entries remain `not-run`, with zero scored attempts. This is a
readiness outcome, not a failed storage candidate or a performance result.
Neither direct SQLite (A) nor append-first with a derived SQLite index (B)
has been selected.

The runner implementation is committed at
`dbeb8cdb0cce663143d3ce7b831f69dc8cf7b75e`. Independently reviewed code and
component evidence are suitable for an experimental draft, not production or
campaign approval. No authorization receipt, campaign-start receipt, attempt
receipt, or campaign seal was issued.

The dedicated validator was invoked with the actual binary identities,
current host facts, frozen plan, and freshly generated commit-bound component
evidence. It exited with code 2:

```text
MonitorComponentEvidenceInsufficient: Monitor component evidence does not establish the required identity, encoding, FD, kill-handoff, and cap behavior.
```

The unresolved condition is
`descriptorOnlyCampaignFeasibilityEstablished=false`. The generator preserves
that condition explicitly, and the validator rejects it. This is not
equivalent to a caller-controlled `ready` flag.

## Evidence and limits

The [machine-readable report](monitored-readiness-dbeb8cd.json) includes the
exact implementation and binary identities, source-artifact hashes, component
measurements, and all 35 plan dispositions. Absolute local paths are omitted.
The original component artifact and its 30-file immutable raw evidence tree
remain local; their hashes are preserved in the report. Unit-test artifacts
with synthetic commit identities were not reused for publication.

| Component observation | Recorded result | What it establishes |
| --- | --- | --- |
| Maximum-width compact summary fixture | 854 framed bytes | Fits the unchanged 1,024-byte bound with enforced ASCII-safe boundary/alarm tokens |
| Maximum summary in the component observation | 397 framed bytes | This probe's measured encoding, not a campaign maximum |
| Shared source/recovery summaries | 8 records, 2,844 bytes | Two monitors share the same bounded output accounting |
| Shared control fixtures | 2 records, 478 bytes | Encoded fixtures charged to that budget, not worker-emitted campaign observations |
| Periodic managed-process observation | 4 summaries, maximum periodic gap 132.6788 ms | The component poller ran; not a scheduling guarantee |
| Runtime double-mapper | 33,627,447,296 apparent bytes excluded | Only the exact pinned runtime-backed memory identity with tmpfs, zero-link, executable backing and pinned CoreCLR mapping proofs |
| Representative package inventories | Maximum four final files | Tiny A/B component lifecycles, including observed A WAL/SHM; not all O1/live populations |
| Representative identity inventory | Five rooted, zero descriptor-only | What the component probes observed, not the full campaign requirement |

The shared-budget negative probes observed exhaustion and cancellation of
both linked monitor instances. The maximum worker-control-size field is a
measurement over a declared encoding fixture population, not an observed
maximum over real campaign traffic.

The scripted lifecycle component tests drive the actual harness event loop
with tiny shell children, including release tokens, kill barriers, recovery
handoff, unexpected exit, and cancellation. They intentionally omit the
unrelated VSTest host from lifecycle monitoring. Production always monitors
its harness; these tests do not establish production-configured harness
descriptor feasibility.

## Why admission remains blocked

The runner derives and enforces a 571-identity geometry: 539 rooted identities
plus 32 descriptor-only identities. Enforcing those limits proves that excess
is rejected; it does not prove that the real O1/live workers fit them.
Representative managed-process and small package probes cannot supply that
missing evidence.

The component package counts also remain representative rather than an
exhaustive characterization of native temporary files and finalization across
the full case inventory. Observable unreadable or unclassified resources
remain incomplete monitoring. They are not silently excluded to produce a
passing gate.

The readiness requirement cannot be discharged by setting the missing
evidence flag to true, reporting a configured cap as an observed maximum,
running an undeclared tuning campaign, or picking a winner from a convenient
subset. Further work must establish the missing real-worker feasibility
under the frozen contract, or obtain a separately reviewed prospective
protocol revision before any scored execution.

## Preserved contract and tracking

The frozen protocol and inputs are unchanged:

- Revision 4 JSON: `e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71`.
- Revision 3 JSON: `faaced68f26a53b7841713d4b1fe1044ce0dd3b277bbac2a9c6f9b6a09f498dd`.
- Exactly 35 planned single-attempt executions, 120 seconds per execution and
  4,200 seconds per campaign; no attempt has been consumed.

Component generation is not a scored campaign or a rehearsal of its frozen
live episodes. No CPU/record, live-disturbance, disk-amplification, or
comparative reopen result is claimed here. The weaker monitored-storage scope
still cannot establish an instantaneous storage peak or production quota.

DC5 (#1003) remains open for readiness and the comparison. DC6 (#1006) has no
production go. Schema compatibility, CLI/REPL integration, MCP integration,
and merging the draft stack remain separate gates.
