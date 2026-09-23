# DC5 bounded prevalidation executor

This is the implementation companion to the frozen
[real-worker addendum](durable-capture-real-worker-prevalidation.md), not an
authorization or a report of real-workload execution. Component tests do not
establish actual-worker feasibility. The addendum, revision-4 JSON, and
historical blocked-readiness report are not rewritten by this implementation.

## Separate admissions and commands

The benchmark executable's `durable-capture-spike` command has four public
prevalidation routes:

| Route | Effect |
| --- | --- |
| `prevalidation-plan` | Prints the fixed matrix, bounds, summary-map hash, environment fingerprint, and layout derivation; no workload |
| `prevalidation-validate --repository-root R --manifest M` | Bounded admission only; requires the immutable prevalidation authorization |
| `prevalidation-run --repository-root R --manifest M` | Consumes the private suite once and executes the separately authorized matrix |
| `prevalidation-inspect --manifest M` | Checks final/partial result shape and, for a seal, the exact immutable inventory and raw coverage |

`prevalidation-admission`, `prevalidation-harness`, and `prevalidation-worker`
are internal subprocess routes. The admission subprocess runs the **current
executable**, not an unvalidated binary named by an input. Harnesses and workers
must find their exact PID/start-time/role in the coordinator-owned ownership
journal after their start-release handshake. Replaying a descriptor in an
unregistered process is rejected. The coordinator is the only journal writer;
its bounded in-memory ledger retains ownership even if persistence fails.
Capture and artifact IDs also carry the complete `pv-...` suite namespace,
including separate source/recovered suffixes; they cannot collide with the
ordinary campaign's ordinal-only IDs.

The closed records are defined in `PrevalidationProtocol.cs`,
`PrevalidationExecutor.cs`, `PrevalidationGeometry.cs`, and
`PrevalidationReportValidation.cs`. JSON rejects unknown fields, duplicate
members, missing required constructor arguments, and nulls for non-nullable
members. The manifest schema is `durable-prevalidation-manifest/1`; the scope is
only `monitor-feasibility-only`. Authorization, adoption, implementation
acceptance, start, attempt, worker descriptor, coverage, report, seal, descriptor
fixture proof, and inspection results have distinct prevalidation schemas.

The shared component validator takes an explicit admission stage.
`PrevalidationComponentProof` validates the preserved component observations
without demanding the unavailable real-worker feasibility result.
`ScoredCampaign` still requires that result. Neither stage changes the input
evidence's `DescriptorOnlyCampaignFeasibilityEstablished` value. There is no
manufactured `MonitoredValidatedManifest`, campaign receipt, readiness override,
or call to `MonitoredDecisionEngine` in the new route.

## Frozen matrix and call paths

The only order is **PV-O1-A, PV-O1-B, PV-LIVE-E, PV-LIVE-A, PV-LIVE-B,
PV-F3-A, PV-F3-B, PV-GEOMETRY**. `PrevalidationProtocol.Plan()` maps these IDs
to the existing `O1`, `L1`, and `F3` worker discriminators. The prevalidation ID
does not rename or shorten the worker's workload.

| Stage | Concrete call path |
| --- | --- |
| Admission | Benchmark command -> `PrevalidationAdmission.ValidateAsync` -> bounded current-executable admission child -> `PrevalidationProtocol.Validate` |
| Suite | `PrevalidationExecutor.RunAsync` -> immutable suite/attempt receipts -> fixture construction under the coordinator monitor -> fresh standalone harness |
| Actual workload | `RunHarnessAsync` -> neutral `MonitoredExecutionContext` -> `MonitoredCampaignRunner.RunExecutionAsync`, with `monitorHarnessProcess: true` |
| Worker launch | `RunOwnedWorkerAsync` -> `StartToolWorker` -> synchronous authority registration -> start release -> `prevalidation-worker` -> `PrevalidationWorkerAdmission.Validate` -> existing `MonitoredWorkerExecutor` |
| O1 | Existing `ExecuteCandidateAsync` and scheduled-offer path: 50,000 512-byte offers, eight keys, 5,000/s, ten seconds |
| Live E | Existing `ExecuteLiveAsync`, `LiveSampleProcess.StartPublishedAsync`, and the unmodified shipping collector; fresh CoreClrSample process |
| Live A/B | Same live episode and providers, existing durable admission pipeline and candidate factory |
| F3 | Existing `WorkerFaultController` barrier -> authority pre-kill boundary -> coordinator `KillOwnedAsync` under the sweep gate -> exact exit/removal -> authority post-kill inventory -> existing explicit recovery worker |
| Publication | Existing `MonitoredPackagePublisher`: finalization, manifest/seal, immutable publication, ordinary reopen and first query |
| Geometry | `PrevalidationGeometry.RunAsync`: synthetic rooted inventory and positively identified anonymous descriptor fixtures; no producer or HTTP load |
| Observation | `MonitoredExecutionMonitor` -> bounded harness stdout/stdin controls -> coordinator `PrevalidationMonitorControl.DispatchAsync` -> the entry's single `MonitoredStorageMonitor`, Linux pinned handles, runtime proof, bounded writer |
| Target startup | `LiveSampleOptions.ProcessStarted` immediately after spawn -> target identity event -> synchronous `live-target-startup` boundary -> diagnostic/HTTP readiness; scored startup is unchanged |
| Cleanup | In-memory owned ledger -> `PrevalidationOwnership.StopAsync` signal-all pass -> shared-deadline aggregate confirmation -> `cleanup.json` -> final authoritative boundary and writer close |
| Results | Raw worker results retained; only the coordinator projects complete entry coverage after cleanup/writer close, never by racing an active JSONL writer or comparing performance |

The shared execution context carries execution settings, not scored admission.
The existing scored route constructs it only after its original validator.
Prevalidation constructs it only after the separate admission. The scripted
component seams are not reachable through a manifest option. Their VSTest-host
omission is confined to explicitly named component methods; real actual-workload
harnesses and the suite coordinator remain monitored.

Source validity preserves O1's frozen 95% achieved-offer floor against the
unchanged 50,000-offer schedule; it does not replace that schedule with a
smaller fixture or require a new 100% threshold. E preserves the stock
collector's first/latest/max coverage and explicitly unavailable raw-tick
counts, rather than inventing tick totals or replacing the collector. A/B live
coverage requires every reported source tick to reach admission without source
parse/admission losses. F3 additionally checks the actual batch-2, sequence
65-128 post-commit/pre-ack control event and the recovery descriptor's offered
128 / acknowledged 64 population. Its aggregate does not invent a source
committed count from the recovery worker's default zero.

## Identity derivation from creation sites

`PrevalidationLayout` names the fixed control/evidence populations. Its finite
whole-suite bound is **2,506 identities**, not eight times 571:

| Population | Upper bound and creation site |
| --- | --- |
| Seven actual-workload histories | `6 * 63 * 4 + 64 * 4 = 1,768` files, `CreateHistoryFixtures` |
| Common worker evidence | `7 * 6 = 42`: attempt, source descriptor, source stdout/stderr, worker result, ordinal outcome; `RunExecutionAsync`/`RunOwnedWorkerAsync` |
| F3 recovery evidence | `2 * 3 = 6`: recovery descriptor/stdout/stderr; same shared runner, no duplicate monitor file |
| Per-context controls | `7 * 8 = 56`: harness request/stdout/stderr/result, coordinator monitor, ownership journal, coverage, cleanup; `PrevalidationExecutor` |
| Known active package/recovery geometry | `6 * 8 = 48`, inherited `MaximumActivePackageAndRecoveryFiles`; E creates no package |
| Geometry rooted layout | 539, including its own history, descriptor-proof artifact, monitor, result slots, and padding; `PrevalidationGeometry` |
| Simultaneous descriptor-only allowance | 32; completed contexts have no surviving owned harness/worker |
| Suite control reserve | 6: suite start, report, seal, partial marker, admission monitor, final monitor; `PrevalidationLayout.SuiteFiles` |
| Immutable input control copies | 9: resolved manifest, authorization, adoption, acceptance, attribution, encoding, component descriptor, historical report, fixture manifest; `CopyControlInputs` |
| Total | `1768 + 42 + 6 + 56 + 48 + 539 + 32 + 6 + 9 = 2506 <= 4096` |

Admission-child stdout/stderr are bounded in memory. The coordinator then
records admission observations and copies the nine immutable control inputs
into the charged suite before any fixture or workload. A partial marker and
a seal are both reserved because a late finalization failure can
invalidate an already-written seal. Shared suite controls counted inside the
geometry observation are conservatively counted again in the bound above.
No path is silently truncated to fit this derivation.

The eight active package/recovery positions are the existing runner's reviewed
layout derivation, not a newly asserted instantaneous native-file theorem.
Candidate A uses `canonical/counters.db`, its native SQLite lifecycle, and
manifest/seal; B uses `canonical/records.bin`, `query/index.db`, its native
index lifecycle, and manifest/seal. Publication moves the same package
identities. F3 retains its unsealed source alongside recovery. Native unknown
transients remain unknown and stop monitoring when observed unclassified;
the eight-position derivation must be independently checked with the component
inventories and source creation paths before implementation acceptance.

Each actual context has 63 immutable four-file, 512-byte synthetic inventory
slots plus its real published/recovered slot. E has 64 synthetic slots. These
files explicitly say they are **not capture packages** and are never opened as
capture data. They do not prove 2 GiB occupancy or native storage semantics.
F3's unsealed source is active workspace, not a retained slot. The monitor's
`rh` field measures the actual retained-history root separately from source
package and recovery bytes.

Geometry counts the already-created current control files, reserves its result
and coverage/cleanup files, and fills the remaining rooted positions to exactly 539.
Its 32 anonymous 512-byte memfds are deliberately created inventory fixtures,
not hidden or deleted native output. A bounded immutable artifact preserves
each fixture's native identity, owner identity, target, length, and attribution.
The coordinator checks all 32 exact native identities against the full boundary
inventory before replying to the geometry measurement request. Its creation-time
classification is restricted to the registered geometry harness, the 32 fixed
memfd names, zero links, and 512-byte length; it does not need to omit harness
monitoring while the immutable identity proof is being written. Other unlinked
or outside-root native files remain incomplete; there is no temp-directory
exemption. The fixture handles close at the end, while all rooted evidence and
all previous contexts remain present.

## Resource ownership and stopping

The coordinator owns **one periodic loop and one summary writer per entry**,
starting before fixture construction and continuing through owned cleanup.
Every sweep observes all registered coordinator, harness, diagnostic, and target
identities plus all suite roots. Source, recovery, and geometry never start a
second periodic loop. The scored runner retains its original local monitor.

Harness stdout/stdin carries bounded synchronous registration, stage, boundary,
termination, and exact-kill requests. Registration is acknowledged only after
the coordinator records the identity and admits it to the monitor. Harnesses
and workers cannot proceed past their start gate first. Target registration
occurs immediately after spawn, before readiness waits. A terminal monitor
issue aborts the coordinator's harness wait even when no worker control arrives.
The kill handoff drains in-flight sweeps, kills and confirms the exact source
under the same sweep gate, and removes its process reference before the
mandatory post-kill inventory and recovery launch. It does not stop/restart the
periodic loop. Any resulting observation gap still fails the unchanged cadence
check.

Harness RSS aggregates coordinator and harness against the unchanged 256 MiB
allowance; diagnostic and target limits remain 512/768 MiB. Role CPU totals
retain the last observed contribution of confirmed-dead processes rather than
resetting or counting the source twice when recovery registers.

Current-context identities include shared suite controls and descriptors not
positively attributable to completed rooted immutable contexts. Limits are
**539 rooted + 32 descriptor-only = 571 current-context identities**, plus
**4,096 across the complete suite union**. Completed roots remain charged to
the one **3 GiB** workspace. History remains **64 slots / 2 GiB per context**;
source plus recovery and exact final packages retain the **256 MiB** limits.

An immutable attempt receipt precedes fixture-file creation and process launch.
The same 120-second absolute deadline covers source, recovery, fixture setup,
startup, and owned cleanup. Five seconds are reserved inside that deadline for
cleanup, not added to it. Admission uses an owned current-executable child with
118 seconds of work and two seconds of cleanup inside its 120-second phase.
The suite clock starts before admission and caps admission, eight entries,
and finalization at 1,200 seconds. Finalization has at most 120 seconds of that
same remaining budget. Existing drain/finalize/reopen budgets stay 5/10/2 seconds.
A blocked filesystem/process cleanup cannot be relabeled successful: deadline
or identity-confirmation failure leaves incomplete/partial evidence.

The full-window output derivation follows the non-restarting production loop:

| Population | Full-entry upper bound |
| --- | --- |
| Periodic records at 100 ms over all 120 seconds | `floor(120000 / 100) + 1 = 1201`; sweep work only reduces the count |
| All boundary headroom, including coordinator and source/recovery | 64 |
| Total summaries | `1201 + 64 = 1265 <= 2048` |
| Newline-inclusive summaries at the contractual maximum width | `1265 * 1024 = 1,295,360 <= 2,097,152` bytes |
| Authority requests and replies | `2 * 256 * 2048 = 1,048,576` bytes; each direction has a 256-frame cap |
| Start/exit handshakes and conservative worker release allowance | `256 + 64 * 128 = 8,448` bytes |
| Existing worker control parser, source/recovery combined | `64 * (8192 + 1) = 524,352` bytes (the actual worker encoder is narrower) |
| Conservative combined full-window output | `1,295,360 + 1,048,576 + 8,448 + 524,352 = 2,876,736 < 8,388,608` bytes |

Each of the at most 64 worker controls requires at most three authority
operations; two launches and two exit/status pairs still fit 256 requests.
Geometry uses two boundary requests and the same writer, not another stream.
Replies carry bounded summary/stat snapshots, not unbounded identity lists.
The largest-width reply is checked by a component test against 2,048 bytes.

Runtime reservations conservatively allocate the entire 2 MiB summary cap,
1,057,024 bytes for authority/handshake controls, and the remaining 5,234,432
bytes of the unchanged 8 MiB stdout budget to shared source/recovery output.
Worker source/recovery share stderr at 8 MiB minus 64 KiB; harness stderr gets
64 KiB. Exhaustion fails closed. No 1,024/1,024 split, slower poll, shorter
entry, missing-observation acceptance, or assumed fast workload is used.

The shared stderr accounting also corrects the original scored executor's
per-worker allowance: revision 4 specifies 8 MiB per execution, including
source and recovery, not 8 MiB for each. This intentional conformance fix can
reject an execution the old implementation would incorrectly accept; it does
not change the frozen protocol's allowance. Stderr exhaustion is reported as
`WorkerStderrLimit`, not misattributed to stdout or monitor summaries.

Summary records remain at most 1,024 bytes **including newline**. The original
maximum-width encoding remains 854 bytes. The prevalidation extension adds
`ci`, `cr`, and `rh`, with a separate manifest-pinned field-map hash; its
maximum-width component fixture is **911 bytes**. All file observations remain
non-atomic. Prevalidation gap checks include sweep time throughout the entry,
including startup and cleanup; poll target is 100 ms and the completion-gap
limit remains 1,000 ms.

Cleanup first signals **every verified eligible identity**, even if the
deadline was already cancelled or another signal failed. It then checks all
remaining identities under one shared remaining deadline, with no per-process
extension. Original absence, an exited/zombie original, or a reused PID means
the original cannot write; a reused PID is never signalled. Permission errors
and unknown identity state are not absence. Exit races are rechecked after
process-handle or exact-signal failure. Explicit errors and all unconfirmed
identities are retained in bounded `cleanup.json` evidence. The coordinator's
outer cleanup runs even if harness I/O/finally fails.

An unconfirmed tree is **not frozen, hashed, or sealed**. It remains explicitly
partial and potentially writable; no immutability is claimed. The geometry
slot reserves cleanup evidence before padding, and `ObservedFixtureFiles` is
derived by enumerating the actual 512-byte fixture files, not the literal 256.

Any failed outcome, missing required boundary, invalid source population,
incomplete observation, overshoot, unexpected exit, or cleanup failure stops the
whole matrix. Remaining entries are `not-run`. A fresh ID does not match the
existing manifest-bound authorization; the private suite root cannot be reused.
Internal subprocess routes also require the exact recorded owned process.
There is no automatic retry or replacement manifest generation.

The official aggregate result takes precedence over raw harness coverage if
later context cleanup/freezing fails. An explicit `partial.json` invalidates
any seal, including one written immediately before a deadline/finalization
failure. Inspection never treats partial evidence as readiness. Even an intact
all-coverage seal says `coverage-observed-awaiting-independent-review` and
`campaignAdmissionGranted: false`.

## Preparation recipe for the parent coordinator

Do not execute this recipe's run step until publication, independent review,
resolved artifacts, and the separate authorization are complete.

1. Publish/review the implementation and rebuild it at its **actual commits**.
   Use the existing shared lock and pinned SDK for every restore/build/test.
   Restore only for missing assets, using the existing private feed.
2. Run `prevalidation-plan` from the rebuilt benchmark DLL. Save the exact
   matrix/bounds, environment fingerprint, and context-summary map hash.
   Build CoreClrSample and pin the exact DLL resolved by `SampleLocator`;
   live execution does not invoke `dotnet run`.
3. Create a new empty owner-only private directory whose leaf is the `pv-...`
   suite ID. Keep input artifacts outside it in a separate immutable control
   directory; all input files must be single-link regular files.
4. Prepare the attribution map with exactly one charged `evidence` root: that
   private directory. Preserve the exact package/recovery directory names,
   4096/4096/32 instrument caps, `/dev/null` and `/dev/urandom` exclusions, and
   the exact libcoreclr double-mapper proof. Declare narrow read-only dependency
   roots for runtime, tool/sample binaries, frozen repository inputs, and
   prerequisite/control artifacts. They must not overlap the private output
   root. Do not declare a generic writable/native-temp exemption.
5. Generate/review prerequisite bounded component evidence with actual
   runner/monitor commits and this attribution hash. Preserve its false
   campaign-feasibility field. Pin the inherited bounded encoding artifact and
   the new context-summary field-map hash separately.
6. Resolve the closed manifest's source commits, host/cgroup/filesystem/clock
   facts, fixture hash, runtime/tool/sample identities, managed dependency
   identities, native identities (including `libe_sqlite3.so` and `libcoreclr.so`),
   and input-artifact hashes. Include every DLL, `.deps.json`, and
   `.runtimeconfig.json` under the tool/sample output directories. The actual
   executing TestSupport assembly, entry DLL, runtime executable, and sample
   resolution must match. Runtime/GC/profiler/loader environment settings are
   fingerprinted without publishing their raw values.
7. Preserve an immutable `durable-prevalidation-adoption/1` record and an
   independently authored `durable-prevalidation-implementation-acceptance/1`
   record. The latter binds commits, binary inventory hash, component evidence,
   attribution/encoding, the 2506 derivation, and review of call paths,
   two-level accounting, cleanup, and negative admission. Bind the historical
   report by its unchanged file hash. No generated placeholder acceptance is
   supplied here.
8. The parent supplies an immutable `durable-prevalidation-authorization/1`
   receipt binding this exact manifest hash, suite ID, addendum hash,
   acceptance hash, historical report hash, eight entries, one attempt, and
   single-suite scope. Validate it with `prevalidation-validate`.
9. Only after that approval, the parent may invoke `prevalidation-run` once.
   Preserve the entire private tree, including failed/partial output, then
   use `prevalidation-inspect` and independent review of the raw observations.
   A correction after any probe needs a newly reviewed prospective revision
   and explicit authorization, not just a new ID or an edited receipt.

Use the parent's non-interactive transport with standard handles connected to
pipes or `/dev/null`. An interactive terminal or an undeclared regular log file
is not a runtime exclusion. The coordinator's own admission sweep checks these
handles before any entry; failure after the suite claim produces a partial
report with all eight entries `not-run`. The resolved input copies, including
the manifest and authorization, remain inside the charged suite and are checked
again during sealed-result inspection.

For component validation from the implementation worktree:

```bash
LOCK=/home/pedrotravi/.copilot/session-state/f10a11bb-805c-4e83-8621-74c5363b7de5/files/ci-validation.lock
DOTNET=/home/pedrotravi/.dotnet/dotnet
MSBUILD=/home/pedrotravi/.dotnet/sdk/10.0.201/MSBuild.dll
flock --close "$LOCK" "$DOTNET" exec "$MSBUILD" \
  benchmarks/DiagnosedBenchmarks/DiagnosedBenchmarks.csproj \
  -t:Build -p:Configuration=Release -verbosity:quiet -nologo
flock --close "$LOCK" "$DOTNET" exec "$MSBUILD" \
  tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -t:Build,VSTest -p:Configuration=Release -p:VSTestNoBuild=true \
  -p:VSTestTestCaseFilter=FullyQualifiedName~DurableCounterSpike \
  -p:VSTestLogger=trx -verbosity:quiet -nologo
```

The component suite exercises frozen plans, drift/authorization rejection,
stage separation, tiny immutable fixtures, tiny two-level inventories,
maximum-width encoding, scripted owned-shell exit/cancellation, shared
source/recovery mechanics, virtual-time full-120-second record/byte accounting,
bounded authority framing and a scripted shell using the actual control route,
coordinator/harness role aggregation, source-death/recovery ownership transitions,
signal-all cleanup with cancelled/shared deadlines, signal/identity/wait errors,
real tiny owned stubs, PID reuse/exit races, persistence-failure ownership
retention, and refusal to freeze unconfirmed evidence. It does **not**
execute this eight-entry suite, the 35-entry campaign, or a full-size hidden
rehearsal.

## Remaining acceptance boundaries

Real-worker observations, host/build-specific descriptor feasibility, independent
implementation acceptance, published code/binary identities, and the resolved
execution authorization are external prerequisites or future evidence, not
claims made by component tests. The 2506 construction bound and enforcement of
caps do not prove instantaneous native maxima. F1/F2/F4/F5-specific states still
need the existing component evidence plus reviewed file/ownership derivation.

There is deliberately no automatic evidence-to-scored-campaign promotion.
Independent acceptance of complete evidence and its provenance, under the
unchanged scored contract, remains a separate gate. No backend ranking, A/B
ratios, tuning, DC6 approval, production API change, or campaign authorization
is produced here.
