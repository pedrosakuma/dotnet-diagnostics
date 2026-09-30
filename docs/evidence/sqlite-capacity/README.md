# SQLite capacity characterization: measured prototype limits

**Outcome:** the synthetic characterization completed, including a measured
batching correction. SQLite remains a plausible architectural direction, but
these results do **not** approve production migration or establish a universal
event-rate limit. Issue #1041 and the DC6 release gates remain open.

The initial run measured all 96 planned cases. A separately reviewed revision
measured 48 predeclared matched cases after correcting an aged-backlog batching
cliff. Neither run was stopped by its workspace budget; no cases were rerun or
discarded. Sixteen 32-record component warmups are excluded from capacity results.
The original DC5 A/B comparison remains inconclusive and is not pooled here.

## Scope and provenance

| Item | Initial run (`01`) | Batching follow-up (`02`) |
|---|---|---|
| Source | `edcea21f0363c19a8d568deecce31a53dda18780` (#1042) | `e32c45f7777af32937e59bc0268773b33eff1034` (#1043) |
| Protocol | `sqlite-capacity-prototype/1` | `sqlite-capacity-prototype/2` |
| UTC execution, 2026-09-24 | 19:46:42-20:06:02 | 20:17:03-20:26:33 |
| Measured cases / warmups | 96 / 8 | 48 / 8 |
| Retained workspace bytes | 1,215,788,309 | 1,701,902,784 |
| Retained artifact hashes | 312 | 168 |
| Matched binary/runtime identities before and after | 458 | 458 |
| Not run / unknown record populations | 0 / 0 | 0 / 0 |

Host: Linux `6.18.33.2-microsoft-standard-WSL2`, 16 available logical CPUs,
AMD EPYC 7763, ext4 on `/dev/sdd`. This is a shared WSL2 host, not a dedicated
storage appliance; the underlying Windows storage and external interference
were not controlled. SDK 10.0.201 built the frozen sources; the private runtime
was .NET 10.0.12, provider assembly 10.0.5.0, native SQLite 3.53.3.
No global tuning, cache flush or competing intentional build/workload was used.
Cgroup membership and available counters were retained; unavailable root
controller limits must not be interpreted as proof of no ancestor limit.

Each case used a fresh worker, ten-second acquisition, bounded queue and one
writer. SQLite retained WAL/FULL, 256-record batches, a 4,096-record queue,
16,384-stack dictionary cap, 96-MiB logical payload cap and 128-MiB main-file cap.
Five indexes were built after acquisition/drain; checkpoint, close, worker exit
and a separate read-only verifier followed. No `.nettrace` was required.
The [protocol](../../design/sqlite-high-throughput-characterization.md) defines
all schemas, caps, oracle fields and the exact follow-up subset.

## Observed operating envelope

All points below have three repetitions. These are **logical observations**,
not allocations, instructions, SQL rows or raw provider events. A completed
overload case faithfully preserves its retained subset; it is not a lossless
capture.

| Profile | Highest tested lossless point | Follow-up overload result |
|---|---|---|
| Numeric occurrence | 50,000 offered/s: all 500,000 retained, both revisions | At 100,000 offered/s, 84,463-89,879 committed/s including drain; 9.81-15.20% queue rejection |
| Repeated four-frame stacks | 50,000 offered/s: all 500,000 retained in run 01; this point was outside run 02 | At 100,000 offered/s, 84,787-87,139 committed/s including drain; 12.46-14.79% queue rejection |
| Activity-like occurrence with four attributes | 10,000 offered/s: all 100,000 retained, both revisions | At 50,000 offered/s, 42,420-44,055 committed/s including drain; 11.13-14.40% queue rejection. At 100,000 offered/s: 42,273-44,058 committed/s and 55.51-57.27% rejection |
| A new four-frame stack every occurrence | 1,000 offered/s: all 10,000 retained in run 01; this point was outside run 02 | All run 02 high-rate cases retained exactly 16,384 occurrences, reaching the dictionary bound; queue and dictionary losses both occurred |

The numeric 50k/s measured commit rate is approximately 49,978-49,979/s because
its denominator includes drain after the declared ten-second source window.
Every measured source offered all planned slots; this does not imply perfectly
uniform arrival timing. The per-second counts and lateness are retained.

The novel-stack average of roughly 1,638 retained observations/s across the
whole window is **not** an engine throughput measurement: retention stops at
the dictionary ceiling. Producer-only novel-stack controls also reached that
ceiling. Activity producer-only controls at 100k/s reached the logical payload
cap; they are not uncapped source-throughput controls. Numerical and repeated
stack controls offered and consumed their full populations.

Run 01 recorded 63 lossless/control-complete outcomes and 33 capacity-negative
outcomes: 18 queue-overload, 12 dictionary-cap and 3 logical-cap.
Run 02 recorded 21 lossless/control-complete and 27 capacity-negative outcomes:
18 queue-overload, 6 dictionary-cap and 3 logical-cap. The reported primary
outcome has precedence; in particular, run 02 novel-stack cases labeled
`queue-overload` also have nonzero `DictionaryRejected`.

## Batching correction and retained adverse evidence

Revision 1 checked the oldest offer's age immediately after dequeuing one
record. Once a backlog was already older than 100 ms, that could repeatedly
flush a singleton FULL transaction, worsening the backlog.

The retained first-repetition 50k/s cases averaged 1.25 committed observations
per transaction for novel stacks and 1.51 for activity-like records. Numeric
100k/s repetition 2 averaged 2.00 and committed only 18,894 observations, while
the other two repetitions committed 864,167 and 860,341. That adverse result
was not excluded or replaced.

Revision 2 drains only currently available records up to the same 256-record
bound before testing the same oldest timestamp. It does not wait for new
arrivals, reset age, weaken durability or increase retention budgets.
Follow-up average committed batch sizes stayed approximately 256. Activity
50k/s improved from 696-755 committed/s to 42,420-44,055/s, including drain.
Numeric 100k/s no longer reproduced the singleton-transaction collapse, but
still rejected offers. Novel stacks reached their explicit dictionary bound.

The deterministic component regressions and observed transaction populations
support the batching mechanism. The runs were sequential, not randomized:
do not attribute every timing difference or calculate a general SQLite speedup
from this before/after comparison.

## Finalization, read-only queries and resources

In run 02, building all five indexes took 1.39-1.59 seconds for 500,000 numeric
records. At the overloaded 100k/s numeric point, 848,011-901,933 retained
records took 2.58-3.56 seconds to index and occupied 107.84-114.89 MiB.
Activity at 50k/s retained 428,022-444,369 records plus four attributes each;
index creation took 1.23-1.65 seconds and final databases occupied
108.25-112.38 MiB.

All 72 measured SQLite cases, including lossy/capped captures, passed their
streaming occurrence/attribute/dictionary oracle and five intended covering
index plans. The eight SQLite warmups also passed. A separate Python
read-only immutable traversal recomputed the three hashes and row populations
for **all 80 databases**, with unchanged database hashes and no WAL/SHM created.

Run 02 connection reopen took 22.05-34.15 ms; connection-open through first
count-query result took 35.13-75.09 ms. The largest individual query sample was
37.66 ms. These are five count shapes with five repetitions each, not detail
retrieval, joins or a useful p99 estimate. Some stack/trace selections are empty
by construction or because the dictionary filled before the queried window.
OS caches were warm/unspecified; these are not cold-cache guarantees.

Peak sampled supervisor-plus-child RSS was 198.43 MiB in run 01 and
201.91 MiB in run 02. Maximum sampled worker package sizes were 129.48 and
132.89 MiB; maximum sampled WAL sizes were 17.48 and 17.94 MiB. Neither
revision reported a supervisor failure, forced stop, deadline expiry, native
SQLite error, unknown population or active path-miss observation.
Twenty-millisecond polling cannot prove native transient peaks or instantaneous
physical containment. CPU includes the synthetic busy-wait producer, writer
and oracle; it is not writer-only CPU or target overhead.

## Evidence inventory and qualifications

- [`cells.csv`](cells.csv) contains all 144 measured cases, source identities,
  accounting populations, separate logical/SQL rates, exact timing denominators,
  per-second counts, finalization/query/resource measurements and report hashes.
  Blank control-only SQL/query fields mean not applicable, not measured zero.
- [`verification.json`](verification.json) binds both manifests, complete result
  files and artifact inventories by SHA-256 and records the independent
  traversal counts. Binary/runtime identities matched again after execution.
- Raw workspaces and controls remain separately retained as
  `sqlite-capacity-run-01`, `sqlite-capacity-run-01-controls`,
  `sqlite-capacity-run-02` and `sqlite-capacity-run-02-controls` in the execution
  session. Large synthetic databases are not committed to Git. Public compact
  evidence does not replace access to those raw artifacts for full reproduction.

An initial independent checker labeled run 01 `acceptance: failed` because it
asserted successful exits for capacity-negative cases. Its original report is
retained unchanged; the failure is in that success-only classification, not in
the independently recomputed data hashes. The checks here explicitly separate
evidence integrity from lossless-capacity outcomes.

Preparation failures are retained too: incompatible generated NuGet assets
after an implicit newer-SDK build were regenerated with SDK 10.0.201. The
follow-up's first fresh restore/build left copy-local output incomplete and
aborted testhost startup; a separate build evaluation resolved it without
source changes. No measured cell was retried for either preparation failure.

## Decision boundary and remaining gates

These observations support continuing **SQLite-only research**, with bounded
off-callback batching and explicit loss/cardinality semantics. They do not
support promising lossless 100k/s across profiles or importing this synthetic
schema as the production design. No unexplained data mismatch was observed;
this is not proof that no critical SQLite issue exists.

Before production migration, calibrate actual provider-delivered demand and
bursts on owned workloads, include real payload lengths and stack depths,
measure end-to-end target impact, and test mixed/concurrent captures. Then
relate retained detail and index costs to the complete capture/query coverage
matrix and agreed resource/overhead/retention requirements. Compatibility,
schema evolution, recovery and release-wide Core/CLI/MCP integration remain
DC6 gates. The requirement is still **one public feature release**, with native
trace retention opt-in and explicit dump-dependent exceptions.
