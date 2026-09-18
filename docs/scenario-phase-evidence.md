# Culture scenario phase evidence

The isolated `culture-lookup@2.0.0` trial artifact includes an optional
`phaseTimeline` alongside `evidence` and `report`. This is **test-only lifecycle
evidence**, not an MCP response or a change to CPU attribution.

This addresses the evidence gap in [#992](https://github.com/pedrosakuma/dotnet-diagnostics/issues/992).
In run [35401542327](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/35401542327),
the shared 60-second budget was observed canceled in the second/ordinal ETW
capture delay. The first culture phase had returned, but final evidence was null.
The retained stack did not establish which preceding stage consumed the time.
This instrumentation is **not a confirmed latency or root-cause fix** and does
not dismiss possible observer overhead.

## Boundaries and interpretation

Each culture/ordinal phase records `Started` followed by `Completed`, `Canceled`
or `Failed` for:

| Stage | Observed boundary |
|---|---|
| `Startup` | The existing sample startup call, including its readiness work |
| `Capture` | The entire routing sampler `SampleAsync` call |
| `Disposal` | The sample's `DisposeAsync` call |

Entries contain the phase, stage, outcome, UTC timestamp and monotonic elapsed
seconds since the recorder was created. The isolated test creates it immediately
before calling the runner; this origin is **not exactly the scenario CTS start**
or the supervisor launch. Use elapsed differences for durations; UTC can jump.

`Capture` includes any routing/gate wait, ETW setup, requested eight-second
window, shutdown and trace processing within that call. It does **not** separately
measure those internals. If the sampler returns successfully, the completion
entry also copies the existing summary's capture, symbolication, aggregation and
total durations (seconds). Those are the sampler's own scopes, not a partition of
the outer span: for example the current ETW total stopwatch starts after gate
acquisition. Default unsupported finer timing fields are not copied as zeros.
Failed/canceled calls have `samplerTimings: null`, meaning **unknown**, not zero.
No Core sampler API or provider instrumentation is added.

A gap between capture exit and disposal entry can include load draining,
validation and evidence construction. It must not be labeled as one of those
operations without additional evidence. Startup failure records no invented
capture/disposal. Cleanup failures are recorded as failures and still propagate.

## Failure retention and bounds

The recorder retains at most **32 entries at insertion**; subsequent events
increment `omittedEntries`. `capacity` is included explicitly. A normal pair has
12 entries. Values have a fixed shape: no exception message, command line,
environment dump, frame inventory or arbitrary diagnostic strings are retained.
Snapshots copy the bounded entry array.

The isolated test attaches the snapshot after its existing success/failure
handling and **before writing the trial artifact and rethrowing the failure**.
Thus `evidence: null`, `report: null`, `outcome: Failed` can coexist with a
completed culture phase and canceled ordinal phase in `phaseTimeline`. Earlier
entries are not discarded when the later phase fails. Outcomes, exception
identity and cancellation tokens are not converted into success.

This is not crash-safe streaming: a killed testhost, stuck cleanup or failed
artifact write can still prevent publication. The separate supervisor/forensic
artifacts remain necessary for those cases.

The optional additive trial-artifact field retains schema version 1. Existing
artifacts without it deserialize to null; non-culture trials do not emit it.
Consumers should continue treating missing timeline evidence as unavailable.
There is no change to the scenario manifest, 60-second shared budget, eight-second
phase windows, verified HTTP responses, OS provenance, reciprocal ownership
controls or provider gates.

## Validation and further diagnosis

Deterministic tests use a manual monotonic/UTC clock and a completion barrier:
complete one owned fake phase, hold the second capture, then fail or cancel it.
They verify retained ordering, original exception/token, disposal outcomes,
insertion caps, UTC jumps, successful sampler timing provenance and JSON
round-trip with null final evidence.

Real hosted diagnosis requires a separately predeclared exact-revision batch
with zero retries. This change alone does not attribute the historical timeout
to startup, symbol processing, ETW setup, scheduling or forensic observation.
