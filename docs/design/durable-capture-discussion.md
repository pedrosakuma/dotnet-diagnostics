# Durable capture RFC: multi-model discussion record

**Status:** Advisory design discussion, not an approved implementation.

**Tracking:** [#999](https://github.com/pedrosakuma/dotnet-diagnostics/issues/999).

**RFC proposal:** [Local durable diagnostic captures](durable-capture-rfc.md).

**Source baseline:** `3e5ea03e2cb981f1358ee191559b6a4a20dd8568`.

## Method and limits

Three local agents using different models received a common, neutral brief
covering existing architecture, maintainer constraints, four storage candidates,
failure cases and verified SQLite/.NET caveats. Each wrote an independent
proposal before seeing the others. They then read and challenged one another's
positions. The editor challenged unsupported claims and separated agreement
from product decisions and unmeasured guarantees.

There were three rounds: independent proposals, cross-model challenges and
targeted final adjudication. In the final round all three accepted the internal
one-artifact contract spike followed by a single-counter-series A/B experiment.
They did not require shipping a separate continuity product first.

This is a summarized argument record, not a verbatim transcript or a scored
model evaluation. Working proposals remain session artifacts. No storage
implementation, performance experiment, production capture or dependency change
was performed for this discussion. It does not rerun or modify #997's frozen
advisory experiments.

The agents share the same brief and source context, so different model names
do not establish statistically independent evidence. Later rounds are explicitly
correlated by peer reading and editorial challenges. Agreement is not a vote
that approves architecture, and it does not substitute for maintainer review.

## Independent positions and peer-review changes

| Model | Independent proposal | Position after peer challenges |
| --- | --- | --- |
| `gpt-5.6-sol` | Prefer A, direct SQLite for retained normalized records; B is the strongest alternative; C could provide continuity first. | Keep A as a prototype preference, not engine approval. Prefer a narrow continuity/lifecycle milestone before timestamped-counter experiments. Clarify canonical data versus rebuildable indexes and explicit recovery. |
| `claude-sonnet-5` | Prefer A, with C as a possible phase zero and counters as the first streaming increment. | Correct copy/enqueue ordering and post-seal indexing; reject automatic mutation on reopen. Prefer continuity first, then one-stream counter experiments. Larger batches cannot excuse exceeding fixed budgets. |
| `gemini-3.8-flash` | Prefer direct SQLite with deferred indexing; initially propose four streams and concrete settings/thresholds. | Withdraw broad four-stream scope, unconditional performance claims, invented numeric gates and API/handle assumptions. Accept the internal lifecycle spike but prefer counters as the first delivered capability, with pre-seal indexes. |

The common A preference is **which candidate to prototype first**, not a
measured A-over-B result. Sol's round-two wording explicitly rejects an
empirically justified engine consensus.

## Convergence

The proposals converge on explicit local packages rather than a shared remote
database; ephemeral capture remains the default. One logical writer owns each
package, analytical queries wait until acquisition and finalization/recovery
end, and queue items must have bounded ownership rather than borrowed parser
state.

They also converge on stable capture/artifact identity separate from paths,
PIDs and temporary handles; typed selective queries without arbitrary SQL or
a speculative eighteenth MCP tool; explicit quality and unknown crash-tail
loss; and current authorization rather than treating recorded scopes as grants.
Large native files stay separate and retain their sensitive-artifact boundary.

Persisting an existing aggregate improves continuity only. Time-series or
cross-signal questions need additional retained observations and explicit
clock, interval and population semantics. Persistence does not manufacture
alignment, omitted detail or causality.

## Challenges that changed the proposal

| Challenged claim | Resolution used by the RFC |
| --- | --- |
| Direct SQLite is already the justified engine choice. | It is a prototype preference. Compare A and B under equivalent fidelity, quotas and durability. |
| Append-only records always minimize observer effect. | No universal ranking. Serialization, filesystem, workload and deferred indexing determine total cost. |
| WAL plus a particular synchronous setting is mandatory. | Journal/sync mode is an experimental variable; process-crash and power-loss guarantees must be separated. |
| Model-proposed batch sizes, memory floors, event rates or latency percentages are accepted defaults. | None are adopted. Freeze numeric budgets before experiments, not after choosing a favorable result. |
| Every current handle disappears when the target exits. | Self-contained handles often survive exit; expiration, eviction, host restart and live-only reaccess are different limits. |
| Data can be copied after queueing the parser event. | Reserve budget before expensive copying; finish owned-copy/lease transfer before enqueue and callback return. Charge active batches until commit or terminal disposal. |
| Index creation or recovery can mutate a sealed package on reopen. | Minimal package-owned indexes precede sealing. Normal reopen is read-only; recovery is explicit and exclusive. Optional later indexes require a separate derived cache. |
| Committing proves power-loss-safe package publication. | Database transactions do not atomically publish sibling native files and an external manifest. Synchronization, dependencies and failure boundaries need explicit guarantees. |
| Observed events equal admitted plus dropped records. | Report observable stages; filters, merges, multi-row records and unknown losses break that equation. |
| One counter stream establishes multi-stream fairness. | It does not. Mixed-stream contention and higher-volume collectors need later scoped evidence. |
| Stored timestamps imply aligned counter ticks. | Preserve actual source windows/intervals and alignment uncertainty; never synthesize alignment. |
| Suggested type names, handle prefixes and command syntax are existing APIs. | They are unapproved sketches. The RFC specifies semantics, not shipping names. |

The editor also retained historical-store quotas and a shutdown caveat:
a cancellation deadline cannot be treated as proof that synchronous native
I/O has stopped. A still-running writer retains ownership of its files.

Not every attempted model correction was correct. The final record does not
adopt Sonnet's claim that an application-process crash discards committed data
from the OS page cache when no per-commit sync occurred. SQLite documents
transaction durability across application crashes independently of that sync
choice; WAL with `NORMAL` can instead lose recent commits after a system crash
or power loss while preserving consistency. These database guarantees do not
cover this proposal's volatile queues, companion files or package publication.
The [synchronous-mode documentation](https://www.sqlite.org/pragma.html#pragma_synchronous),
not model agreement, resolves that distinction.

## Strongest case for B

The strongest alternative is not "flat files are faster." It is that a measured
workload may have insufficient acquisition-time CPU/I/O headroom for A under
fixed budgets, while B can retain equivalent records and defer indexing
without violating the agreed overall budget. Offline analysis that does not
need immediate query readiness can value this trade differently.

That case must include B's framing/recovery implementation, extra read/index
work, temporary space and time-to-first-query. It must also include A's journal,
checkpoint and pre-seal index costs. Raising A's buffers, weakening sync
semantics or comparing C's aggregates to B's detailed stream is not a fair win.

## Actual dissent and editor recommendation

**First delivery remains a product choice.** Sol and Sonnet prefer proving
continuity before new temporal retention. Gemini argues that counters-first
better addresses the motivating analytical gap; its claim to share the same
sequence did not match the peers' actual round-two proposals.

The final round accepted a small common-contract spike with one already-bounded,
self-contained artifact, then a timestamped-counter A/B experiment. Sol
explicitly relaxed its earlier "ship continuity first" wording; Sonnet accepted
the same no-separate-product constraint. Gemini accepted that research sequence
while retaining a preference for counters-first public delivery. This is not
a binding two-versus-one release vote. Whether the first supported delivery is
continuity-only or counter series remains open to maintainer review.

**Index placement:** all accept immutable canonical evidence. Sonnet treats
pre-seal indexes and a separate derived cache as legitimate alternatives;
Gemini favors pre-seal indexing for simplicity and read-only-volume operation.
The proposed first prototype uses minimal pre-seal indexes; a later external
cache is not forbidden.

**Not decided by model agreement:** engine approval, journal/sync mode,
canonical schema/layout, migration/recovery identity, exact API placement,
numeric quotas, shutdown escalation and performance acceptance thresholds.

## Authoritative caveats

- [SQLite WAL](https://www.sqlite.org/wal.html): checkpointing, side files,
  synchronous settings and read-only access have distinct constraints.
- [SQLite synchronous modes](https://www.sqlite.org/pragma.html#pragma_synchronous):
  transaction durability depends on configuration and failure boundary.
- [Microsoft.Data.Sqlite async](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async):
  ADO.NET async calls execute synchronously.
- [.NET channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels):
  bounded item capacity is not a byte quota; full-mode behavior matters.
- [Resource boundedness](../resource-boundedness.md): collector retention and
  loss semantics cannot be replaced with a generic storage completeness claim.
