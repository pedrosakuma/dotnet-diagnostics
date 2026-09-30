# DC4 bounded counter pipeline spike

**Status:** Accepted for internal DC4 use following independent review and
resolution of its conditions. This
does not approve a storage engine, representation, public API, production
budget, or release.

## Scope

DC4 implements a candidate-neutral in-memory admission, batching, sink, and
typed-query seam under the accepted revision 3 protocol. It changes no
production source and includes no SQLite or append-only adapter. Tests use only
constructed `CounterValue` observations and deterministic fake sinks: no live
process, EventPipe session, capture, database, filesystem package, network, or
calibration campaign is involved.

The implementation is in
`tests/DotnetDiagnostics.Core.Tests/DurableCounterSpike/`. It reuses the
production `CounterValue` and `CounterKind` types from
`src/DotnetDiagnostics.Core/Counters/CounterValue.cs`; it does not fork or
exercise `EventPipeCounterCollector` parsing. A constructed observation adds
nullable 100 ns source-relative ticks and required clock-domain/origin
metadata. Operational enqueue/commit time is deliberately absent from the
logical record.

## Record semantics

The owned logical record preserves provider, name, display name, unit, finite
value, `Mean`/`Sum`, source time, clock metadata, actual interval, display
scale, and explicit metadata states. A `Sum` remains a per-interval increment;
a falling value never creates a reset. `resetState` is always `unknown`.
Nonfinite interval metadata is retained as a null interval with state
`nonfinite`; missing and nonpositive states remain distinct.

Sequence is assigned in source-callback order before admission, so rejected
offers leave visible gaps. Per-key coverage is computed before storage
admission. It is unknown for missing/regressing time or a current interval that
is missing, nonpositive, or nonfinite. Otherwise a gap is true only when the
source-time delta is strictly greater than three times the current interval.
A regression test proves that a queue-rejected observation still advances
source history, preventing the stored series from being misreported as source
loss.

## Bounded ownership and writer behavior

The spike applies the accepted frozen limits:

- exactly 4,096 bytes of owned-buffer capacity is reserved before copying or
  bounded encoding;
- UTF-8 limits are 128 provider, 256 name, 512 display-name, and 64 unit bytes;
- 128 keys, one in-copy record, 256 queued records, 321 total owned records,
  and 2 MiB owned bytes;
- batches are at most 64 records and 262,144 owned bytes, with a 100 ms maximum
  age;
- typed series pages contain at most 100 rows and every result is capped at
  1 MiB;
- active-capture capacity is an injected host-global seam.

UTF-8 sizes, metadata, kind, finite value, key cardinality, and a declared
oversized encoded record are rejected before allocation/copy. A successful
reservation owns a new fixed-capacity byte array and copied strings before
`TryWrite` returns. The bounded writer cannot grow that buffer and no retained
shared pool exists. Queue-full, admission-closed, content-invalid,
owned-budget-full, and writer-failed outcomes are distinct. Every rejection,
queue-full path, bounded-encoding failure, known sink failure, and unknown
commit outcome restores owned reservations.

One supervised writer moves ownership from queue to active batch without
uncharging it. A batch remains charged until the sink returns committed,
failed, or unknown. The sink receives the same bounded encoded memory, valid
only for the commit call, plus the typed record; an adapter must write or copy
before acknowledging. The sink contract declares that result explicitly.
If batch age wins concurrently with a successful queue-availability wait, the
writer consumes that permit into the current batch; a canceled wait creates no
phantom permit. The regression first observes registration of the availability
wait, releases age, then offers the second record from a hook after `WhenAny`
has selected age but before cancellation. It therefore forces the fixed branch,
not merely availability winning normally. The second record commits without a
third offer or admission shutdown.
Known failures and unknown commit outcomes are separate terminal states;
unknown is not counted as committed or failed. Clean quiescent conservation is
reported separately from package/query operations.

Stopping normal admission and capture cancellation both allow an independent
finite drain. Cancellation has its own terminal state after the admitted tail
drains. Finalization has a separate deadline. If a noncooperative sink outlives
a drain/finalize deadline, its task, fixed buffers, and active-capture ownership
remain live until the actual operation ends.

## Typed query seam

The internal query seam exposes only:

- `Summary()`: at most 128 exact `(provider,name)` keys with retained count,
  first/last/min/max, source window, gap count, and unknown-coverage count;
- `Series(provider,name,afterSequence,pageSize)`: source-order rows using an
  exclusive sequence cursor and at most 100 rows;
- `Quality(accounting)`: known stage totals, unknown-commit state, unknown
  reset semantics, and the limited meaning of source coverage.

There is no SQL, raw-object view, live lookup, or public MCP/CLI surface.

## Frozen synthetic inputs and oracle

`durable-counter-fixture-manifest.json` pins protocol revision 3, the accepted
protocol JSON hash, canonical encoding, and the exact Q1/Q2 input and
independently authored oracle hashes:

| Artifact | SHA-256 |
| --- | --- |
| Protocol JSON | `faaced68f26a53b7841713d4b1fe1044ce0dd3b277bbac2a9c6f9b6a09f498dd` |
| Q1 input | `e4fefbefddac4193103038771b5f3f2d7c3a1c01e0af9db4853b1b51780c6a16` |
| Q1 oracle | `9e46cddd49925945f6450921129c48126038d7a07df0107b1a6259b4a52318e3` |
| Q2 input | `1e757f6f1f3328eddf6dbc848eeef4c72a38291c93806ec9204b6e72955c2154` |
| Q2 oracle | `e59999f9ab66b9dbc58404bde6c1e359ad01c13dd0be43b28862b94561eb821b` |

Canonical hashing uses `System.Text.Json` web defaults, one ordinal-bearing
UTF-8 JSON value plus LF per row, invariant round-trip strings for finite
doubles, and protocol tokens for named nonfinite source values. Q1 follows the
revision 3 generated rules. Q2 is loaded from the JSON's explicit 16-row table
and does not apply the Q1 generator formula. Expected sequence, identity,
value, kind, source-time, metadata-state, gap, reset, and rejection results are
built by a separate oracle, not by the pipeline under test.
Tests calculate SHA-256 over the current protocol JSON and compare it with the
manifest rather than trusting a duplicate literal. They also compare the C#
pipeline defaults with the JSON's record, string, key, owned count/bytes,
queue, in-copy, batch, age, page, result, and active-capture limits.

These hashes are unit-fixture preparation for a future resolved run manifest.
They are not an A/B execution, measurement, or evidence that either backend
passes.

## Validation

The focused suite covers Q1 and explicit Q2 semantics, all retained identity,
display/unit, value/kind, interval/scale and clock fields, exact-size and
oversized records, multibyte UTF-8 limits, unknown kinds, nonfinite values,
invalid clock metadata, the 128-key cap, pre-admission gaps, queue and M1 owned
budget pressure, batch ownership, the age/availability permit race, controlled
batch age, clean conservation, normal cancellation, known failure, unknown
commit outcome, noncooperative drain/finalize deadlines, active-capture gating,
paging/cursor/result limits, quality reporting, copied ownership, source
immutability, actual protocol hashing, defaults-to-JSON parity, and frozen
fixture/oracle hashes.

Validation command, serialized by the shared lock and using SDK 10.0.201:

```text
flock --close <session>/files/ci-validation.lock \
  /home/pedrotravi/.dotnet/dotnet exec \
  /home/pedrotravi/.dotnet/sdk/10.0.201/MSBuild.dll \
  tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -restore:false -t:Build -p:Configuration=Release -clp:ErrorsOnly

flock --close <session>/files/ci-validation.lock \
  /home/pedrotravi/.dotnet/dotnet exec \
  /home/pedrotravi/.dotnet/sdk/10.0.201/MSBuild.dll \
  tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -restore:false -t:VSTest -p:Configuration=Release \
  -p:VSTestNoBuild=true \
  -p:VSTestTestCaseFilter=FullyQualifiedName~DurableCounterPipelineSpikeTests \
  -v:minimal
```

The initial no-restore build found a missing assets file. One restore was then
performed from only
`https://packagefeedproxy.microsoft.io/nuget/v3/index.json`.
The final focused result was **15 passed, 0 failed, 0 skipped**.

## Known limits and next gate

Claude Opus 5 reviewed the implementation and the first corrective pass.
The coordinator resolved its last condition by making the age-race regression
deterministic as described above. Removing only the consumed-permit drain
caused that single regression to fail waiting for record two; restoring it
returned the complete focused suite to 15 passed, zero failed/skipped. The
temporary mutation was removed before publication. This is regression evidence,
not an A/B campaign run or a change to the frozen fixture identities.

The spike does not demonstrate live parser integration, real callback cost,
multi-provider fairness, cross-process coordination, a durable package,
filesystem quotas, SQLite/framed-file behavior, commit durability, crash or
power-loss recovery, throughput, target impact, or the 35-run campaign. The
default pipeline age wait is 100 ms; focused tests substitute immediate or
manually released waiters to avoid wall-clock-dependent assertions. These
waiters and sinks are test seams, not production scheduling or persistence
implementations. Buffer accounting covers the fixed owned byte arrays;
count-bounded managed metadata is not claimed as exact heap accounting.

DC5 remains responsible for live integration, both storage adapters, resolved
manifest, fault injection, measurements, and protocol execution. Production
and public API decisions remain blocked on later review and maintainer approval.
