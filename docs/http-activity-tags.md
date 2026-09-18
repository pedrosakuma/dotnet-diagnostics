# Outbound HttpClient Activity tag provenance

Investigation [#960](https://github.com/pedrosakuma/dotnet-diagnostics/issues/960):
**empty tags are an expected runtime-only .NET 8 outcome**, not by themselves
evidence of collector loss. This applies to the outbound ActivitySource
`System.Net.Http`, operation `System.Net.Http.HttpRequestOut`, not inbound
ASP.NET Activities or the separate networking EventSource collector.

If a retained span has no destination metadata, **backend attribution is
unavailable**. Trace/span IDs and valid duration do not recover a URL, host, or
backend. Never assign a backend by matching durations. Empty tags also do not
prove that the target has no instrumentation: subscription configuration,
instrumentation behavior, and the selected output view matter.

## Opt-in HTTP destination evidence (#969)

Destination attribution **is feasible without changing the target**. Enable
`includeHttpDestination=true` on MCP `collect_events(kind="activities")` or
`kind="distributed_trace"`. `collect_batch` accepts the same opt-in for its
`collect_events/activities` entry; other entries are unchanged. CLI one-shot
and `session` accept `--include-http-destination` for `activities` and
`gc-activities`. BenchmarkDotNet uses
`[DiagnosticKind(BenchmarkDiagnosticKind.Activities, IncludeHttpDestination = true)]`.
All default to **false**.

The existing ActivitySource Stop transform is unchanged. When the source
filters include `System.Net.Http` (using the same case-insensitive wildcards),
the collector additionally subscribes to exactly this classic DiagnosticSource row:

```text
HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut.Start:-Request.RequestUri.Host;Request.RequestUri.Port;Request.RequestUri.Scheme;ActivityTraceId=*Activity.TraceId;ActivitySpanId=*Activity.SpanId
```

Its raw `Arguments` contain only `Host`, `Port`, `Scheme`, `ActivityTraceId`,
and `ActivitySpanId`. `*Activity` reads `Activity.Current`. A tuple of actual
non-zero W3C **(trace ID, span ID)** joins the start authority to the existing
completed span; EventSource header GUIDs, time, path, and duration are never
join keys. No runtime-version probe or instrumentation package is required.

`destination` is a separate nullable field on captured/list/trace/distributed
spans, not synthetic native tags:

```json
{
  "availability": "available",
  "scheme": "http",
  "host": "backend.example",
  "port": 8080,
  "provenance": "diagnostic-source-http-start"
}
```

This identifies the **requested URI authority**, not a physical server, proxy,
DNS address, or a redirect's final endpoint. Native HTTP tags remain unchanged
even when they disagree with this evidence. The trace tag allowlist is not
expanded. The newly selected Request payload exports **no whole URI, path,
query, fragment, userinfo, headers, or arbitrary Request object**. This is
**not global URL scrubbing**: existing .NET 9/10 or instrumented native tags
can still contain URLs in capture/list output as before.

Scheme/host/port expose potentially sensitive infrastructure, so opt-in is
required. Structured destination values pass through the configured
`SensitiveDataRedactor` before capture/export and again at consumer projections.
Both components and the assembled `scheme://host:port` form are scanned;
a match withholds all components and reports `availability="redacted"`.
Summaries/reports contain counts and limitations, never authority values.
Redaction is pattern-based, not a guarantee against all sensitive hostnames.

### Correlation limits and accounting

The new state exists only when opted in and HTTP is source-included. Trace
filtering happens before correlation retention: other trace IDs are counted,
not stored, and cannot exhaust the target's budget. This is independent of
the completed-span `maxActivities` / `maxMatchedActivities` cap. Stop identity
accounting continues after span retention fills, so a late duplicate still
invalidates an earlier retained span.

- `MaxIdentities=16384` bounds whole-window exact identity history at insertion.
  It covers classic HTTP starts and source-filtered Activity stops. Completed,
  invalid, and unmatched identities are never evicted. On saturation **all
  destination attribution is withheld** (`identity-cap`), since forgotten
  events could hide a duplicate. No TTL or lossy tombstone scheme is used.
- `MaxAuthorities=4096` independently bounds copied authorities; host length is
  at most 253, scheme is HTTP(S), port is 1..65535. Later authorities are withheld
  (`authority-cap`), but their identity history remains tracked.
- Exactly one start and one stop are required. Reordering is accepted only
  after the whole window drains. Duplicate starts (even identical), conflicting
  starts, duplicate stops, or observed identity reuse produce
  `ambiguous-identity`, including evidence invalidated after its first pair.
- Starts outside the capture window produce `missing-start`; malformed evidence
  is unavailable. Any measured EventPipe loss or non-normal completion withholds
  all authorities (`transport-incomplete`). Loss not reported by the runtime
  cannot be detected; these are observed-window facts, not proof of completeness.

`httpDestinationCorrelation` carries status, exact caps, start/stop/nonmatching/
invalid/duplicate/conflict/cap-event counters, unmatched identity counts, and the
number of retained available authorities. Counts describe the whole capture,
not a top-N query slice; unmatched stops can include non-HTTP sources. It is
preserved through list, trace, grouping/summary, and distributed Pod coverage.
Missing metadata on legacy/non-opted-in captures stays **null/unknown**, never
a manufactured clean-stream claim. `source-excluded` explicitly records an
opt-in whose source filter excludes HTTP.

## Where attributes originate

| Boundary | What it actually carries |
|---|---|
| Runtime Activity | .NET 8 HttpClient creates the Activity and sets timing/identity, but does not populate HTTP tags. |
| Optional target instrumentation | An application listener or a configured instrumentation package can enrich that Activity. This is separate from runtime behavior; diagnostics neither installs nor requires it. |
| DiagnosticSource event payload | `HttpHandlerDiagnosticListener` emits a `Request` object on `System.Net.Http.HttpRequestOut.Start`. Its `RequestUri` properties are not Activity tags. Their existence does not imply the ActivitySource bridge exported them. |
| EventPipe ActivitySource bridge | The unchanged Stop row requests `TagObjects.*Enumerate` from the stopped **Activity**. Only explicit destination opt-in adds the authority-only classic Start row above. |
| Core capture and `activities` list query | Parse and retain exported tags unchanged. Optional destination evidence is separately joined by real identity, never inferred from timing. |
| `trace` query | Deliberately projects a fixed low-cardinality allowlist, redacts allowed values, and limits them to 128 characters. `server.address`, `server.port`, `url.full`, and `http.url` are not on that allowlist, even when present in the full capture. |
| CLI and MCP | Use the same Core capture/query behavior. Collection JSON and the `activities` list retain captured tags; `trace` applies the narrower projection. |

The collector enables `Microsoft-Diagnostics-DiagnosticSource`, Verbose level,
keywords `0x3` (Messages and Events), with this exact-source transform:

```text
[AS]System.Net.Http/Stop:-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate
```

An omitted sampling suffix means `AllDataAndRecorded`, not propagation-only.
Requesting all data does not manufacture tags that the producer never sets.
Exact source names can be filtered in the provider; wildcard patterns use the
existing broader subscription plus local filtering. Trace-targeted retention
from #949 (implemented by #951) changes which completed spans are retained, not
which Activity properties the provider exports.

## Pinned source evidence and version boundary

- [.NET 8.0.31 `DiagnosticsHandler`](https://github.com/dotnet/runtime/blob/1219a42122cf5190c7f512850557e38c422430dc/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs):
  `CreateActivity` supplies no tags; `SendAsyncCore` starts/stops the Activity
  without setting HTTP tags. `ActivityStartData` exposes `Request` separately.
- [.NET 8.0.31 DiagnosticSource bridge](https://github.com/dotnet/runtime/blob/1219a42122cf5190c7f512850557e38c422430dc/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/DiagnosticSourceEventSource.cs):
  the `[AS]` grammar, default `AllDataAndRecorded` sampling, and `*Enumerate`
  transform explain the effective subscription.
- [.NET 9.0.20 `DiagnosticsHandler`](https://github.com/dotnet/runtime/blob/3879076d9a06ce098d37c3882fb1845a6627335b/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs)
  and [.NET 10.0.12 `DiagnosticsHandler`](https://github.com/dotnet/runtime/blob/4271d88e0aebf3d04f188f1334c2220d80555ef6/src/libraries/System.Net.Http/src/System/Net/Http/DiagnosticsHandler.cs)
  set `http.request.method`, `server.address`, `server.port`, and `url.full`
  when `IsAllDataRequested` is true, plus completion-dependent fields such as
  status and protocol. URI redaction and proxy behavior are version-specific;
  do not extrapolate this loopback evidence to every deployment.

## Controlled evidence

The repository regression uses `tests/HttpActivityTarget`, a package-free target
built for net8.0/net9.0/net10.0. A private loopback TCP server serves four
HttpClient calls: overlapping `/fast` and `/slow`, HTTP 503 `/unavailable`, and
an explicitly cancelled `/cancel` after server acceptance. There is no ASP.NET
server Activity, external backend, proxy, URL query, credential, or TLS bypass
in these original `plain`/`enrich` controls.

Two fresh-process modes separate origin from enrichment: `plain` changes no
Activity tags; `enrich` explicitly adds three sanitized tags in the test's
DiagnosticSource Start observer. This is a **test-only listener**, not a claim
to reproduce a particular OpenTelemetry package/configuration.

The source-stop witness records real trace/span IDs, start/duration ticks,
`IsAllDataRequested`, `Recorded`, and `TagObjects`. Its ActivityListener samples
`None`; the external bridge requests recording. A separate DiagnosticSource
witness records known request host/path under the same IDs, without treating
those properties as captured tags. Two EventPipe sessions use identical
arguments: the production collector and an independent raw payload observer.
Bounded readiness Activities establish reception in both before HTTP load.
The raw observer saves payloads before the product parser, not a `.nettrace`.

| Executed target | Plain source/raw/capture | Explicit enrichment | Trace query |
|---|---|---|---|
| Native Windows .NET **8.0.31** | All four HTTP spans have empty tags, despite all-data/recorded being true | All three supplied tags survive source, raw export, and capture | Empty in plain mode; method survives enrichment, destination is omitted |
| Linux .NET **8.0.26** | Same narrow observation; **not** an 8.0.31 substitute | Same positive control | Same projection policy |
| Native Windows .NET **9.0.20**, Linux **9.0.14** | Runtime supplies method/destination tags; they survive raw export and capture | Supplied tags also survive | Method/status/protocol as available; destination omitted |
| Native Windows and Linux .NET **10.0.12** | Runtime supplies method/destination tags; they survive raw export and capture | Supplied tags also survive | Same projection policy |

Each mode has four uniquely matched HTTP spans. Source/raw/capture start and
duration ticks are equal for these spans; this compares the same Activity's
exported timing, **not an independent wall-clock latency accuracy benchmark**.
Transport loss is zero, completion is normal, and no retention cap is hit in
these finite controls. These facts do not establish capture completeness under
arbitrary load, or accuracy for the user's 31/742-span captures.

The live regressions execute MCP's `DiagnosticTools.QueryCollection` entry
point through Core list/trace queries and serialize their results. This is not
an HTTP MCP protocol round trip. CLI regressions exercise real parsing,
collection dispatch, session query dispatch, and JSON output with deterministic
empty/enriched fixtures; optional replay also exercises the owned captures
without opening another collection. Replay is not a second live acquisition.

Reproduce the live ladder after a normal solution build:

```bash
dotnet test tests/DotnetDiagnostics.Core.Tests/ -c Release --no-build \
  --filter FullyQualifiedName~HttpActivityTagLiveTests
```

All three installed target runtimes are required. The target reports its actual
runtime; optional `HTTP_ACTIVITY_EXPECTED_8`, `_9`, and `_10` enforce exact patch
versions. `HTTP_ACTIVITY_EVIDENCE` saves bounded sanitized JSON for the six
cases. Setting `HTTP_ACTIVITY_REPLAY` to that directory adds those six records
to `CliActivityTraceTests`, in addition to its ordinary fixtures. Neither
variable enables raw production tracing or modifies target applications.

The additional `destination` mode keeps tags untouched and uses **two different
owned loopback ports** for concurrent calls, plus fixed fake userinfo, query,
and fragment markers. All four source/raw/capture identities and timing remain
equal; every new classic export contains only the five selected fields and none
of those markers or paths. HTTP 503 and cancellation also retain the correct
authority. `.NET 8.0.31` native Windows and Linux `8.0.26`, plus native
`9.0.20`/Linux `9.0.14` and `10.0.12` controls cover this separate opt-in.
A `window` control starts a real .NET 8 request before the destination collector
attaches; its later stop is retained with `missing-start`, not a guessed backend.
Evidence names are `net*-bridge.json` and `missing-start.json`. Source witnesses
are test-only observers; the product never installs them. Separate deterministic
tests exercise production correlation state under duplicates/reuse/loss/caps,
and real MCP HTTP collection/batch/fan-out/query dispatch plus CLI and
BenchmarkDotNet serialization/redaction boundaries.

## Resolution and limits

No loss was demonstrated between populated source tags, raw bridge payload,
capture, or the list projection in the original controls. #960 therefore
documented the runtime/projection limitation, not a tag-loss fix. #969 adds
the explicit, separately bounded authority bridge above: no new tool, target
instrumentation prerequisite, tag mutation, or full-URL allowlist expansion.
Default collection and existing span-retention budgets remain unchanged.

The user reported 31 calibration spans and 742 pipeline spans with empty tags
on .NET 8.0.31, and sub-millisecond timing differences for the 31-span control.
Their raw artifacts and original configuration were **not supplied and were
not independently verified**. The owned configuration explains how the reported
shape legitimately arises on that exact runtime; it is not an identical
reproduction of either original capture.

If a future sanitized case shows a tag already present on the same stopped
Activity but absent in the raw export or full capture/list, investigate that
specific boundary with its effective subscription and instrumentation versions.
An omission only from `trace` can instead be intentional projection. Source
properties, runtime versions, and output views must not be conflated.
