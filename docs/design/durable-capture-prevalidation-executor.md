# DC5 bounded prevalidation executor

This is the implementation companion to the frozen
[real-worker addendum](durable-capture-real-worker-prevalidation.md), not an
authorization or a report of real-workload execution. Component tests do not
establish actual-worker feasibility. The addendum, revision-4 JSON, and
historical blocked-readiness report are not rewritten by this implementation.

The separately versioned [prospective sampled-loss protocol](durable-capture-sampled-loss-protocol.md)
adds opt-in manifests and reviewed sealed-readiness admission. The strict
defaults and historical semantics documented below remain unchanged.

The [revision-7 unified active sampling protocol](durable-capture-unified-active-sampling-protocol.md)
adds a separately hash/schema-bound root-loss population for current mutable
activity. Its artifact guide lists the fresh metadata routes and receipts.
All quiescent inventories and legacy inspection remain strict.

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
`ci`, `cr`, `rh`, `ec`, and `nc`, with a separate manifest-pinned field-map hash;
its maximum-width component fixture is **1,017 bytes including LF** (all numeric
fields at maximum width, 64-byte boundary, alarm, and error tokens, and the
four-integer native context). Without `nc` it remains 983 bytes. `ec` is
the first error code of that sweep, not a threshold alarm. Complete sweeps
omit it; `er` continues to count all sweep errors. Incomplete sweeps caused
solely by unclassified identities use `UnclassifiedChargedResource`, with the
existing `uc` count. Additional error codes are not retained; their count
remains explicit. All file observations remain
non-atomic. Prevalidation gap checks include sweep time throughout the entry,
including startup and cleanup; poll target is 100 ms and the completion-gap
limit remains 1,000 ms.

### Bounded causal evidence and compatibility

Root observations do not inherit descriptor-loss admission, including under
[revision 6](durable-capture-observed-unlinked-protocol.md). The current root
walker first collects pathname strings, then opens each file and reads native
metadata. `PathObservationFileNotFoundException` means a subsequent file-open
observation failed; the summary retains neither that pathname nor the exception
message. A stage label is the last acknowledged stage, not a stack trace.
An existing file after cleanup cannot identify a previously missing leaf.

An anchored directory descriptor plus `openat` can preserve access across a
rename of an already-pinned ancestor. It does not prevent a leaf from being
unlinked between enumeration and open, and atomic replacement can resolve the
same name to a different native identity. Pinning a leaf beforehand preserves
the inode, but a later zero-link root observation still fails the existing
root rules. Neither approach alone proves a lossless enumeration.

The bounded `RootObservation` component tests distinguish these interleavings,
including one transaction through the actual B derived-index implementation,
whose DELETE journal disappears at commit. They are not historical filename
identification or workload-feasibility evidence. The root callback is an
internal component-only seam, not a manifest option or runtime exemption.
Current sweep/kill coordination does not serialize native index transactions
against root enumeration. Excluding journals, monitor output, or publication
files would omit charged resources. Allowing root-path losses or suspending
mutations throughout enumeration would require an explicit material design
decision; these tests implement neither and change no final inventory gate.

This correction responds to the preserved
[first-attempt failure](../evidence/dc5/prevalidation-765e56c.md). It does not
change that report, infer its missing cause, authorize another attempt, or
change the frozen addendum/protocol.

- `durable-prevalidation-sweep-summary/2` introduced the null-ignored `ec`
  field to the prevalidation extension. Codes use the existing 1–64-character
  ASCII token alphabet (letters, digits, `-_.:`), never paths or exception
  messages. An invalid internally generated code fails closed as
  `PrevalidationErrorCodeEncodingLimit`, rather than truncating a message.
  The original scored field map and 854-byte encoding remain unchanged.
- `durable-prevalidation-sweep-summary/3` adds null-ignored `nc`, retaining
  only the first descriptor-operation failure per sweep, independently of
  `ec` (which may name an earlier root error). Its fixed numeric tuple is
  `[role, operation, error, fd]`: role 0/1/2 means harness/diagnostic/target;
  operation 1/3 means first/second native `open(O_PATH | O_CLOEXEC)` and error
  is the immediately captured errno, 1–4095; operation 2/4 means first/second
  managed `/proc/.../fdinfo` flags read and error is the **signed Int32
  HResult**, not an inferred errno. The descriptor is a nonnegative Int32,
  not a PID, pathname, identity inventory or exception message. The maximum
  extension is 34 bytes (`,"nc":[2,4,-2147483648,2147483647]`), leaving seven
  bytes of the 1,024-byte budget. Complete/errorless records cannot carry it;
  malformed tuple lengths, operations, roles and native errno ranges fail
  encoding and inspection. Later descriptor contexts are not retained;
  their errors still contribute to `er`. No completion rule changes.
- `durable-prevalidation-coverage/2` keeps `failureCode` as the primary
  failure, adds its `failureStage`, and adds nullable
  `secondaryFailures: { firstCode, firstStage, count }`.
  `durable-prevalidation-report/2` also exposes primary `failureCode` and
  the same nullable secondary structure for admission/suite finalization.
  The first failed entry's primary code is also the report's primary.
  Entry cleanup/disposal/evidence-finalization failures cannot overwrite it.
  Admission and suite monitor disposal are handled separately from their
  primary failures.
- Stage names are a closed ASCII vocabulary (at most 21 bytes), separate
  from the 64-byte code: admission, admission disposal, fixture preparation,
  harness, entry cleanup, monitor disposal, evidence finalization, suite
  finalization/disposal/freezing, worker, coverage, entry, and suite stop.
  The serialized names use hyphens as defined by `PrevalidationFailureCodes`.
  Thus the same `IOException` in cleanup and disposal is distinguishable,
  and a full-width valid code is never replaced merely to fit a stage prefix.
  Missing, unknown, or mismatched primary stages fail result inspection.
- Secondary retention keeps **only the first secondary code**, plus a
  saturating positive 32-bit count of all secondary failure detections.
  `count - 1` is the number whose codes are omitted; at `int.MaxValue` the
  count is a lower bound. Repeated detection of sticky incompleteness counts
  as another detection, not proof of another underlying cause or a failed
  process termination. No array, path dump, or new evidence file is added.
- `durable-prevalidation-evidence-validation/2` exposes the report's
  primary/secondary evidence and stages and the first failed probe's coverage, including
  its separate secondary evidence. A partial classification remains
  `partial-unsealed-not-readiness-evidence`, never readiness or approval.
- The refreshed context field-map hash rejects old manifests for **every new
  admission/execution**. Inspection alone accepts the original hash with
  `/1` report/coverage schemas and the previous `ec`-only hash with unchanged
  `/2` report/coverage schemas. Legacy missing causality stays unknown; the
  inspector does not reinterpret an old cleanup label as the original cause.
  Mixing old/new field-map and result versions is rejected. The manifest
  envelope stays `/1`: its existing pinned field-map member identifies the
  extension, and its hash continues to bind authorization.

All limits remain unchanged: 1,024 bytes including LF, 2,048 summaries per
entry, the shared 8 MiB stdout budget, and 2,048-byte control frames. The
maximum-width control reply is tested with the new full-width error token.
There are no new suite files; the 2,506-identity derivation remains unchanged.
Successful later sweeps, quiescent cleanup, and null threshold alarms cannot
erase earlier monitor incompleteness. No descriptor-loss or observation-gap
condition is ignored or reclassified.

The controlled fixture component test creates **one four-file, 512-byte
slot**, not a shortened O1 or live workload. A component-only callback holds
each actual fixture writer handle open while a separate task runs the real
descriptor pin/coherence observer and root monitor. It checks retained-history
observations of 512, 1,024, 1,536 and 2,048 bytes, then the final inventory.
It does not register unrelated VSTest-host descriptors or claim the complete
real coordinator descriptor population was tested. Separate deterministic
tests persist a missing-root cause, restore complete individual sweeps, and
verify the original sticky failure still gates the entry. These establish
causal evidence retention and controlled overlap, **not the cause of the
historical fixture failure**. New component-only callbacks additionally
release the real fixture writer while the observer is between enumeration
and first pin, or between first and second pin, and block the next fixture
until the failure is captured. Both yield native `ENOENT` in controlled
components. A full self/coordinator sweep likewise retains the failure when
a concurrent owned 512-byte output writer closes between pins; the summary
writer and unrelated host descriptors remain monitored, without exemptions.
Direct tests cover closure at both flags reads, native `dup2` reuse, unlinked
files, injected `EACCES`/`EIO`, pin disposal, and invalid causal contexts.
The controlled native results do not reconstruct either historical attempt's
missing errno. See the [component investigation](../evidence/dc5/descriptor-observer-component.md)
for the retained negative evidence and remaining feasibility block.

Cleanup first signals **every verified eligible identity**, even if the
deadline was already cancelled or another signal failed. It then checks all
remaining identities under one shared remaining deadline, with no per-process
extension. Original absence, an exited/zombie original, or a reused PID means
the original cannot write; a reused PID is never signalled. Permission errors
and unknown identity state are not absence. Exit races are rechecked after
process-handle or exact-signal failure. Explicit errors and all unconfirmed
identities are retained in bounded `cleanup.json` evidence. The coordinator's
outer cleanup runs even if harness I/O/finally fails.

Cleanup evidence now uses `durable-prevalidation-cleanup/2`. Error strings
include the operation (`signal`, `confirm`, or `confirmation-wait`), a bounded
code, and for I/O failures the exact eight-hex-digit `HResult` (`hr-...`).
Permission failures also retain an immediate inner `IOException` HResult
(`iohr-...`) when present. No message, path, or exception chain is serialized.
At most **16 distinct errors of 128 ASCII bytes each** are inserted;
`additionalErrorCount` counts detections not retained after that cap, saturating
at `int.MaxValue` (then a lower bound). Repeated retained errors are deduplicated.
Already recorded errors are never cleared even if later observations establish
quiescence. Either retained errors or a nonzero overflow count still fails
the cleanup gate. The existing five-owned-identity limit is also enforced on
the cleanup input. These fields use the existing cleanup slot, with no change
to the 2,506-identity derivation or entry deadline. Legacy `/1` cleanup is
accepted only with legacy report inspection.

### Narrow proc-stat disappearance correction

The earlier component assertion reporting `confirm:IOException` did **not**
retain errno/HResult. Its particular cause remains unknown; later passing
tests do not establish that it was unrelated to these changes. Neither that
failure nor the historical prevalidation fixture failure is reclassified.

There is, however, a separately verified open-to-read disappearance mechanism:
an owned shell can exit and be reaped after its `/proc/<pid>/stat` file is
opened but before it is read. The deterministic component test opens that
file, releases and waits for that exact shell, then reads the still-open
handle. On the validation host it observed **.NET 10.0.12,
`IOException.HResult == 3`**, recorded in the test's TRX output.
The same test separately reopens the path after reap and verifies
`DirectoryNotFoundException.HResult == 0x80070003`. HResults are therefore
**not uniformly raw errno**; the raw-3 branch requires the exact
`System.IO.IOException` type, not a subclass. Typed negative tests reject a
derived IOException carrying 3.

This matches the checked [.NET 10.0.12 Unix I/O mapping source][unix-io-errors]:
`ESRCH` reaches `GetIOException`, which passes **raw errno** as the exception
HResult. It is not Windows `HRESULT_FROM_WIN32(3)` (`0x80070003`).
The checked [Linux procfs source][linux-proc-base] shows that
`proc_pid_make_inode` retains the task's `struct pid` reference in the inode;
`proc_single_show` returns `-ESRCH` when that referenced task is gone.

The cleanup reader now explicitly pins the opened stat file for its one
bounded read (at most 4,096 bytes, with a one-byte overflow sentinel). A
successful read still compares the captured Linux start time and recognizes
zombie/dead states. Opening a reused numeric PID sees the replacement's start
time and cannot authorize its termination. An already opened proc inode does
not switch to a replacement numeric PID, so read-time raw `ESRCH` establishes
that the referenced task is absent; no extra directory handle or pidfd is
needed for this **observation**. The existing exact PID/start-time signaling
checks are unchanged.

Only this fixed proc-stat operation recognizes mapped missing-file or
missing-directory exceptions (the existing absence behavior), or exact
`IOException` with raw `ESRCH`, during open/read as absence.
It does not retry or catch arbitrary I/O as success. Read-time missing-file
and missing-directory cases remain distinguished by their mapped types;
permission errors (including their inner errno), `EIO`, generic I/O,
Windows-style HResults on plain IOException, malformed stat data, and over-limit data fail
closed and remain evidence. This does **not** change descriptor observation
or tolerate any descriptor loss, monitoring gap, or incomplete sweep.
Deterministic typed-error tests cover both open/read phases, unchanged
start-time/zombie checks, retained errors after eventual quiescence, and the
insertion-time diagnostic cap. The normal live owned-shell path is also
checked without a race-reproduction loop.

[unix-io-errors]: https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs
[linux-proc-base]: https://github.com/torvalds/linux/blob/v6.8/fs/proc/base.c

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
