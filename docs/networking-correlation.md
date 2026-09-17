# Networking identity correlation

`collect_events(kind="networking")`, CLI `collect --kind networking`, and the
BenchmarkDotNet `networking` kind use the same Core collector and accounting.
Latency is **accepted observed Start/Stop pairs only**, not every request or
handshake, and not proof that acquisition was complete.

## Activity flow and target effects

In addition to the four `System.Net.*` providers at Verbose/all keywords, the
collector enables `System.Threading.Tasks.TplEventSource` at Informational with
**TasksFlowActivityIds (`0x80`) only**. Without this, fresh .NET 8/9/10 processes
can emit empty IDs: concurrent HTTP starts overwrite one another in the old
implementation. Task scheduling/transfer/debug keywords are not requested.
The existing event-source allowlist and catalog already recognize this provider;
no permission, package, target instrumentation, or MCP tool is added.

This enables runtime `ActivityTracker` and its `AsyncLocal` activity flow. It is
**not reversible by stopping the session**: the runtime's `m_current` stays
initialized. Other EventPipe sessions and EventListeners share target provider
enablement, and a later session may benefit from previously enabled tracking.
Do not describe this collection as zero overhead or as restoring the target's
original activity-tracking state. Restarting a process resets that state; the
collector does not restart or modify the target.

Pinned runtime implementations:
[.NET 8](https://github.com/dotnet/runtime/blob/v8.0.31/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/ActivityTracker.cs),
[.NET 9](https://github.com/dotnet/runtime/blob/v9.0.20/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/ActivityTracker.cs),
[.NET 10](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/ActivityTracker.cs).
`TplEventSource.OnEventCommand` enables this tracker for the flow keyword;
disabling the provider does not undo tracker initialization.

## Conservative identity handling and bounds

HTTP, DNS and TLS maintain separate correlators. Neither thread ordering,
elapsed-time similarity, related activity IDs nor path guesses substitute for
identity:

* Empty IDs never produce latency.
* A duplicate pending ID invalidates **both** starts; neither subsequent stop
  can identify a valid pair. A failure cannot make that ID reusable.
* Every observed nonempty ID is remembered for the **whole session**, including
  completed, failed, orphan-stop, expired and evicted identities. Subsequent
  starts for it are excluded. Only a different, unseen ID can recover pairing.
* Pending values are capped at **4096 per kind**, with a **2-minute,
  event-time TTL** checked at insertion and lifecycle callbacks (expiry includes
  the exact TTL boundary). The oldest pending value is evicted at insertion.
  Its identity is not forgotten. Idle pending values are reported as unfinished
  at capture end; no timer is required to account for them.
* Exact identity history is capped at **65536 per kind**. A new identity beyond
  this cap latches fail-closed behavior for that kind until capture end: all
  pending and future starts are capacity-suppressed, and subsequent stops are
  unmatched. History is never aged out or evicted to manufacture apparent
  recovery. This deliberately trades latency coverage in high-volume/long
  captures for bounded memory and protection against arbitrarily late stops.
  The other kinds and event counts continue. Shorter captures can avoid this
  limit, but do not repair an ambiguous lifecycle.
* A stop earlier than its start is excluded, not clamped to a zero-duration
  sample.

The 256 HTTP operation buckets, overflow bucket, bounded percentile samplers
and 64 MiB EventPipe buffer are unchanged. These are collector retention bounds,
**not a universal bound on target CPU or AsyncLocal activity allocation**.
Loss or missing events outside the observed stream can still prevent detection
of ambiguity; pairing accounting does not certify transport completeness.

## Public accounting and consumers

`NetworkingSnapshot.Correlation` and all five networking query views add an
**init-only, nullable** property. Existing positional constructors and
deconstruction remain intact. Missing/null metadata in older serialized
artifacts means **unknown**, never zero exclusions or complete coverage.

JSON uses `correlation.byKind.http|dns|tls` (the default BDN serializer uses
`Correlation.ByKind`). Each kind contains `counts`, `identityCapacityReached`
and `hasLimitations`. Count keys are:

| Key | Meaning |
| --- | --- |
| `started` | Observed starts |
| `paired` | Accepted nonnegative-duration pairs |
| `emptyStarts` | Starts with no identity |
| `ambiguousStarts` | Duplicate/reused starts, invalidated pending starts, and starts with invalid timestamp stops |
| `expired` / `evicted` | Pending starts removed by TTL / pending-cap pressure |
| `failureDiscarded` | Starts removed by the existing failure-event policy |
| `unfinished` | Pending at capture end |
| `capacitySuppressedStarts` | Pending/future starts excluded after exact identity history saturates |
| `unmatchedStops` | Nonempty stops without a valid pending start, including late stops |
| `emptyStops` | Stops with no identity |
| `invalidTimestampStops` | Stops earlier than their matched start |
| `unmatchedFailures` | Failure callbacks without a valid pending start (including empty identities) |

The start outcomes partition `started`; the three stop-exclusion counts are
independent. `hasLimitations=false` means **no observed pairing gaps**, not
complete acquisition, supported activity coverage, or success-only population
correctness. The collection summary reports accepted/start counts and explicitly
qualifies latency. CLI JSON/human summaries, MCP envelopes/drilldown and BDN
JSON/report headlines preserve that qualification.

The bounded maps avoid repeating an entire schema per protocol in `tools/list`;
Core also exposes typed accessors. The existing MCP catalog byte limit is not
raised. No new tool is introduced.

Failure-event population (#959), acquisition/drain lifecycle (#958), and broader
availability semantics (#961) remain separate work. In particular, a failed
HTTP/TLS lifecycle is still excluded from percentiles; its removal is now
counted, not silently presented as complete coverage.

## Regression evidence and limits

`NetworkingActivityCorrelatorTests` calls the production pairing implementation
directly, covering empty/duplicate identities, late stops, TTL, eviction, exact
history saturation, failure/completion reuse, new-identity recovery and timestamp
rejection.

`NetworkingCorrelationLiveTests` runs fresh .NET 8/9/10 targets on both Linux
and native Windows. Each target performs baseline, two separate observed
sessions, and post-session workloads. Each workload has three serial and three
concurrent HTTP paths plus a local TLS client/server handshake. A target-side
provider-enabled handshake precedes the measured capture workload; a note alone
cannot satisfy the assertions.

Each HTTP request is compared with an independent target `Stopwatch` around
`GetAsync(ResponseHeadersRead)` with an empty body, the same response-header
scope as the events. The oracle is not a configured server delay. Every path
must occur exactly once per session, and the observed start/stop/pair counts
must all equal six. TLS must either produce measured accepted pairs or explicitly
exclude both overlapping identities; the deterministic duplicate-ID control
does not assume that every runtime will reproduce a TLS collision. Successful
local TLS controls are not proof of universal TLS correlatability.

The bounded six-cell run used Linux runtimes 8.0.26/9.0.14/10.0.12 and native
Windows 8.0.31/9.0.20/10.0.12. All cells observed tracker state false before
capture and true during both sessions **and after stopping them**. Test output
retains each path witness, snapshot, target PID exit, whole-workload CPU time,
allocated bytes and elapsed time. These small controls include JIT, networking
and certificate generation: they demonstrate nonzero, workload-dependent costs,
**not an isolated TPL overhead estimate or a production performance ceiling**.
There is no concurrent external-session overhead benchmark or universal TLS
coverage claim.

Run the bounded matrix with installed target runtimes:

```sh
dotnet test tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -c Release --filter FullyQualifiedName~NetworkingCorrelationLiveTests
```

Missing runtimes fail the test; they are not silently skipped.
