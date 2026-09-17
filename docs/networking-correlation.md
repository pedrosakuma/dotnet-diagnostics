# Networking correlation and capture quality

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
| `failureDiscarded` | Legacy failure-removal accounting; always zero for population v2 |
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

## Latency population v2: failed completions included

`correlation.byKind.http|dns|tls.counts.latencyPopulationVersion = 2` versions
the existing HTTP/DNS/TLS percentile semantics. They now describe **all accepted
completed Start/Stop pairs, including failures**, not successful-only operations.
HTTP `ByOperation` count/total/p95/max uses that same population. No separate
success-only or failed-only percentiles are exposed; the outcome denominators
below explain the mixed distribution. Queue timing is a different, unchanged
`RequestLeftQueue` payload population, not a request completion duration.

`Start -> Failed -> Stop` retains the start and marks its outcome on `Failed`;
only `Stop` contributes one latency sample, measured from Start to Stop.
Repeated failures neither add samples nor renew the original TTL. A missing
Stop remains unfinished (including at early/source-failure capture end);
missing starts, duplicate/reused/empty identities, invalid event ordering,
expiry and capacity exclusions never manufacture an interval. An orphan or
post-Stop failure is unmatched and qualifies coverage, not a retroactive
rewrite of an already sampled completion. A failure timestamp before its start
or previous failure invalidates the pending start; a Stop before the last
failure is also excluded.

The collector does **not** substitute a failure's `elapsedMilliseconds`
payload for a missing pair: its scope/identity/completeness cannot repair
ambiguous or missing endpoints. This also avoids mixing payload durations
with Start-to-Stop intervals.

Additional fixed count keys (all nullable typed accessors on Core records):

| Key | Meaning |
| --- | --- |
| `latencyPopulationVersion` | `2` identifies all-accepted-completion semantics; absent is legacy/unknown |
| `paired` | All accepted completions and total latency samples seen, including samples beyond the bounded reservoir's exact prefix |
| `pairedFailed` | Accepted completions with at least one observed Failed marker |
| `pairedWithoutFailure` | Accepted completions with no observed Failed marker; not proof of application success |
| `matchedFailureEvents` | Failed callbacks associated with a valid pending start, including repeats |
| `repeatedFailureEvents` | Additional matched failures for an already marked pending operation |
| `invalidTimestampFailures` | Failure callbacks invalidating a pending start due to backwards timestamps |
| `unfinishedFailed` | Failed-marked pending starts at capture end; subset of `unfinished`, not samples |
| `httpResponseStops` | HTTP Stops with a response status in 100-599, whether paired or not |
| `httpStatusErrorStops` | Subset of response Stops with status 400-599 (including 503), **not** RequestFailed |
| `httpStopsWithoutStatus` | HTTP Stops with no known response status, including runtime -1; not a cancellation classifier |

`pairedFailed + pairedWithoutFailure = paired`. Observed Stop count is
`paired + unmatchedStops + emptyStops + invalidTimestampStops` when all payloads
parsed. The existing `*Failed` headline fields count **failure events**, not
unique failed operations; repeats and unmatched failures remain distinguishable.
The successful-operation population is only *observed no-failure completions*:
HTTP 503 completes a response without a transport `RequestFailed`, and missing
failure events could also make an operation appear successful. `RequestFailed`
can represent cancellation, timeout or transport errors; this provider does not
reliably classify those causes. They remain **unknown**, never inferred by
parsing localized exception text. The controlled workload records actual
cancellation/timeout causes independently; production does not claim those
target-side witnesses.

All maps are fixed-size additions to existing nullable/init-only `Correlation`;
no positional constructors or MCP schemas change. Older artifacts with missing
Correlation **or existing accounting without a population version** retain
unknown outcome semantics. All five queries preserve these counts; shared Core
summaries, CLI, MCP and BDN reports label v2 and its outcome denominators.
Compare percentiles only with compatible population versions.

Zero accepted completions means **latency unavailable**, even though existing
nonnullable duration fields remain zero for compatibility. The HTTP headline
does not print measured-looking `0.0ms` for a known v2 zero-sample population.
Correlation gaps and `CaptureQuality` still independently qualify evidence:
adding failed samples does not turn a partial capture into complete acquisition.

`NetworkingFailurePopulationLiveTests` exercises mixed 200/503/cancellation/
HttpClient-timeout and all-failed captures, plus a rejecting loopback TLS peer.
Each runs on .NET 8/9/10, retains bounded raw lifecycle witnesses, and compares
accepted operation durations with independently measured client Stopwatch
intervals. The TLS peer consumes ClientHello and sends a fatal handshake-failure
alert over plain TCP, without host certificate-policy changes.
Deterministic DNS (and TLS) cases invoke the production paired-event handler;
**no live DNS failure reproduction is claimed**. The normal TLS success fixture
and acquisition-quality cases remain separate regression controls.

## Latency availability and compatibility

The existing C# positional constructors/deconstruction and nonnullable `TimeSpan`
JSON scalars are preserved. **A scalar zero is a compatibility placeholder when
there are no accepted samples, not a measured duration.** Do not infer availability
from a positive scalar either, or from `Started`, `Stopped`, or `Failed`.

`latencyAvailability` is a derived, read-only map with `http`, `queue`, `dns`,
and `tls` keys on the snapshot and **all five** focused views. It is recomputed
from existing `Correlation` and `CaptureQuality`, not independently persisted
quality metadata. On JSON deserialization any supplied map is ignored and the
authoritative counts/quality determine it again. BDN's default serializer uses
`LatencyAvailability`; CLI/MCP use camel case.

| Value | Meaning |
| --- | --- |
| `measured` | At least one accepted sample, possibly exactly zero duration. This says nothing about full population coverage |
| `not-observed` | Known zero samples, no observed pairing gaps/pending operations (or rejected queue payloads), normal drain with known zero loss and no parse errors. No claim that no target activity occurred |
| `uncorrelatable` | Zero accepted pairs with observed identity/order/retention exclusions or orphan events; inspect the kind's existing correlation counts |
| `incomplete` | Zero samples with unfinished pairs or incomplete/uncertain acquisition; inspect `unfinished`, `CaptureQuality` and its nullable loss |
| `unavailable` | Queue events were observed but none carried a valid duration; inspect `queueRejectedSamples` and parse errors |
| `unknown` | No compatible sample evidence, or zero samples without enough acquisition evidence to say not-observed. Legacy absence is never promoted to known zero loss or a measured zero |

These are availability labels, not an exclusive taxonomy of all limitations.
Measured samples remain measured during loss/early/source-failure captures,
while the existing quality records still mark the evidence partial. With zero
samples, observed exclusions take precedence over unfinished/acquisition
limitations; both remain visible in their original records. Queue payloads do
not need activity-ID pairing and are not degraded by unrelated HTTP pairing
gaps. Missing population version means unknown HTTP/DNS/TLS sample semantics;
missing new retention counts on a v2 artifact means unknown reservoir retention,
not unknown accepted-pair count.

### Counts and populations

| Aggregate | Accepted valid samples / population | Retained percentile samples |
| --- | --- | --- |
| HTTP request p50/p95/max | `correlation.byKind.http.counts.paired`, population version 2: all accepted completed Start/Stop pairs, including Failed -> Stop | `percentileSamples` in the same counts map |
| DNS p50/p95/max | `dns.counts.paired`, same v2 semantics | `dns.counts.percentileSamples` |
| TLS p50/p95/max | `tls.counts.paired`, same v2 semantics | `tls.counts.percentileSamples` |
| Queue p50/p95/max | `http.counts.queueSamples`: finite, nonnegative, representable `RequestLeftQueue.timeOnQueueMilliseconds` payloads, including zero. Not HTTP completions or queue depth | `http.counts.queuePercentileSamples` |
| Each HTTP operation total/p95/max | Group `count`: accepted HTTP completions assigned to that group; inherits parent HTTP population version, correlation exclusions and capture quality | Group's additive nullable `percentileSamples` |

`http.counts.queueRejectedSamples` counts observed `RequestLeftQueue` events
that did not yield a duration. Missing, malformed, non-finite, negative or
unrepresentable payloads hit the existing parse-error boundary rather than
silently turning into zero. `HttpRequestsLeftQueue = queueSamples +
queueRejectedSamples` for events reaching that handler; acquisition/parser
failures before it are still independently recorded in `CaptureQuality`.
Queue validity does not imply that all queue events were observed.

C# `NetworkingCorrelationCounts` has nullable, JSON-ignored typed accessors
`LatencySamples`, `PercentileSamples`, `QueueSamples`, `QueuePercentileSamples`,
and `QueueRejectedSamples`; missing keys remain null. HTTP groups add nullable
`PercentileSamples` and derived `LatencyAvailability`: missing retention evidence
is `unknown`, positive `Count` with evidence is `measured`, otherwise
`unavailable`. Existing group constructors still work and produce unknown
availability rather than guessing from legacy scalar/Count values. An absent
group is not a zero-latency group: excluded/uncompleted requests cannot be
assigned confidently to a retained bucket. Top-N/summary trimming does not
change either per-group samples or the full artifact; the overflow bucket has
the same evidence fields.

### Reservoir retention is not capture coverage

Each sampler retains every accepted sample up to **4096**, then uses a bounded
reservoir. `paired`/`queueSamples`/group `Count` keep counting the full accepted
population, while the retained count stays 4096. p50/p95 are approximate when
retained < accepted. Max (and group total) still uses all accepted samples.
No global percentile algorithm or empty-sampler behavior changes here.
Zero accepted samples makes all associated latency scalars unavailable.
An accurately computed percentile over retained samples does not prove full
pairing or acquisition coverage, even below the reservoir cap.

Shared Core summaries explicitly label unavailable HTTP, queue, DNS and TLS
p95 instead of printing `0.0ms`; real measured zero still prints `0.0ms`.
They also report retained/accepted percentile denominators, independently of
correlation and capture quality. Handles, focused views, actual CLI output,
MCP envelopes, and BDN JSON/report headlines preserve these distinctions.

Deterministic regressions cover empty, zero, positive, all-failed, partial
pairing, unfinished, loss, unknown loss, invalid queue payloads and legacy
artifacts through the real shared consumer boundaries. Production paired
handlers, queue validation, operation-group sampler and reservoir counts are
exercised directly. Existing loopback failure and acquisition tests additionally
assert measured availability and counts; the idle post-cancellation session
asserts not-observed. Consumer fixtures isolate projection/serialization, not
independent live networking workloads in each consumer.

## Acquisition quality

`NetworkingSnapshot.CaptureQuality` (JSON `captureQuality`, BDN `CaptureQuality`)
is separate from `Correlation`. This nullable, init-only addition also appears
on **every** networking drilldown: `summary`, `byOperation`, `queue`, `tls`, `dns`.
Existing constructors/deconstruction and `Duration` retain their meanings.
Missing/null metadata is **unknown**, not a successful zero-loss capture.

| Field | Meaning |
| --- | --- |
| `completion` | `normal`: source drained after requested stop; `early`: source ended before requested stop; `source-failure`: source construction, configuration or processing threw; `unknown`: normal drain could not be established |
| `eventsLost` | EventPipe-reported transport loss after drain; null when unavailable (including source failure). Zero is a known report, not proof of complete target activity coverage |
| `streamReadDuration` | Local monotonic elapsed time spent constructing/reading the source, including buffering and drain. Not a replacement for requested `Duration`, target-observed coverage, or a last-event timestamp |
| `parseErrors` | Count of events rejected by the collector's payload-parsing boundary; parsing continues and useful data is retained |
| `hasLimitations` | True unless normal completion, known zero loss and no payload parsing errors were recorded |

The requested `Duration` is never silently shortened. Summaries label it
**requested**, report completion/loss/parsing quality, and qualify partial or
uncertain observation. A target that exits early can still yield a successful
diagnostic envelope with useful partial evidence; success is not a declaration
of full-window coverage. Shared Core results and stored artifacts, CLI JSON and
human summaries, MCP envelopes and BDN JSON/report headlines retain the quality.
Identity accounting cannot detect unseen starts lost in transport; even normal,
zero-loss drain does not prove every target operation was observable.

Networking stops waiting when the processing task ends, rather than waiting out
the requested window after target exit or source failure. Stop and drain retain
their separate five-second budgets and session disposal/task-fault observation.
Caller cancellation remains cancellation, not a partial successful result.
If processing cannot drain safely within its budget, no mutable snapshot is
returned. Other shared-runner collectors retain their existing wait-window
behavior. Event parsing errors add a count and a single aggregate quality note,
not an unbounded per-failure message list; capture, sampler and identity caps
are unchanged.

`NetworkingCaptureQualityLiveTests` uses fresh .NET 10 loopback targets on Linux
and native Windows. It tests normal drain, **actual early target exit**, caller
cancellation followed by another session, and explicitly labeled injections:
a third-HTTP-start exception in the real `source.Process` callback, a payload
parsing failure, and replacement of the real drained loss report with nonzero
or unavailable loss. The source failure retains the first two completed
requests; payload failure retains the other five. Loss injections exercise real
session lifecycle/result wiring, **not naturally observed event-flood loss**.
No actual nonzero transport-loss reproduction is claimed.

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
