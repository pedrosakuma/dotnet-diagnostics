# Durable capture monitored runner

This document describes the experimental runner for the adopted revision-4
durable-capture comparison protocol. It is a benchmark/test-support surface,
not an MCP tool or a `dotnet-diagnostics` CLI feature, and it does not grant
production approval.

The runner is hosted by `DiagnosedBenchmarks`:

```bash
RUNNER=benchmarks/DiagnosedBenchmarks/bin/Release/net10.0/DiagnosedBenchmarks.dll
DOTNET=/home/pedrotravi/.dotnet/dotnet

$DOTNET "$RUNNER" durable-capture-spike monitored-describe
$DOTNET "$RUNNER" durable-capture-spike monitored-plan \
  --repository-root "$PWD" \
  --protocol docs/design/durable-capture-monitored-protocol.json \
  --output /absolute/readiness/plan.json
$DOTNET "$RUNNER" durable-capture-spike monitored-host-facts \
  --artifact-root /absolute/private-evidence-root
$DOTNET "$RUNNER" durable-capture-spike monitored-runtime-proof \
  --output /absolute/readiness/dotnet-runtime-memory-proof.json
$DOTNET "$RUNNER" durable-capture-spike monitored-component-evidence \
  --runner-commit <reviewed-runner-commit> \
  --monitor-commit <reviewed-monitor-commit> \
  --attribution /absolute/readiness/attribution-map.json \
  --output /absolute/readiness/monitor-component-evidence.json
$DOTNET "$RUNNER" durable-capture-spike monitored-validate \
  --repository-root "$PWD" \
  --manifest /absolute/readiness/run-manifest.json
```

`monitored-run` is intentionally separate from planning and validation. Only
the parent campaign coordinator may invoke it after independent runner review
and after creating the immutable authorization receipt:

```bash
$DOTNET "$RUNNER" durable-capture-spike monitored-run \
  --repository-root "$PWD" \
  --manifest /absolute/readiness/run-manifest.json
```

`monitored-worker` is an internal subprocess route. It revalidates the
revision-4 manifest and authorization, requires exact immutable campaign,
attempt, and worker-descriptor files, and rejects paths that do not match the
harness-owned execution roots. It is not a bypass around `monitored-run`.
Revision-3 `describe` and `validate-manifest` remain execution-closed.

The scripted component lifecycle seam substitutes small shell children for
the diagnostic worker and omits monitoring the unrelated VSTest harness.
Production execution always includes harness monitoring. These component
results establish handshake, termination, recovery handoff, and cancellation
behavior, not production-configured harness descriptor feasibility.

## Readiness artifact preparation

Prepare artifacts in this order. Every identity must be a resolved value;
placeholder commit IDs, hashes, paths, host facts, or readiness flags are not
accepted.

Source-commit fields are syntactically checked, not resolved through Git by the
validator. The coordinator must resolve the reviewed commits, rebuild those
sources, and bind the resulting binary hashes. Unit-test artifacts deliberately
use synthetic identities and must never be published as readiness evidence.

1. Build the reviewed runner, `CoreClrSample`, and native dependencies that
   will be used by the campaign.
2. Create a unique evidence root with mode `0700`. The campaign directory named
   by `campaignId` must not exist. At most four declared readiness files may be
   stored below the evidence root before execution; no unrelated files are
   allowed there.
3. Write a `durable-monitored-evidence-encoding/2` artifact with JSON Lines,
   UTF-8 without BOM, the
   `durable-monitored-sweep-summary/1` schema and its exact compact-field-map
   SHA-256, a maximum of 1,024 bytes per newline-framed monitor record, 2,048
   summaries per execution, 8 MiB shared by monitor summaries and worker
   stdout, and 8 MiB for worker stderr. The runner additionally enforces 64
   boundary summaries and 64 worker control records per execution.
4. Run `monitored-runtime-proof` with the exact runtime that will execute the
   campaign. Add its `dotnet-runtime-memory-proof/1` result to the attribution
   map and add the same `libcoreclr.so` path and SHA-256 identity to the
   manifest's native binaries.
5. Write a `durable-monitored-attribution-map/2` artifact with exactly four
   charged roots named `evidence`, `history`, `workspace`, and `outputs`. The
   last three must be the matching leaves below
   `<evidenceRoot>/<campaignId>`. Package names are exactly `package` and
   `package-staging`; recovery names are exactly `recovery` and
   `recovery-staging`. Read-only dependency roots must be explicit existing
   absolute directories outside the evidence root. Literal runtime-only
   descriptor targets are exactly `/dev/null` and `/dev/urandom`. The one
   additional proof is limited to the exact deleted .NET double-mapper
   descriptor backed by tmpfs and executable mappings of both that backing
   identity and the pinned `libcoreclr.so`; no generic memfd or tmpfs
   exemption is allowed.
6. Generate `durable-monitored-component-evidence/3` with the reviewed runner
   and monitor commit IDs and the completed attribution map. The command
   monitors a real published managed sample, real descriptors and mappings,
   periodic polling, root discovery, exact PID/start-time kill handoff, and
   separate fail-closed linked-writable and open-unlinked negative controls.
   It persists an actual maximum-width summary and field-map hash, drives a
   short source-then-recovery pair through one linked deadline and one shared
   output budget, exercises shared exhaustion and cancellation, and inventories
   representative A/B create, drain, pre-seal, publish, immutable reopen, and
   first-query stages. It separately enforces the 4,096-identity hard cap, the
   derived 571-identity campaign geometry, the 32 descriptor-only cap, the
   2,048-record cap, and the 1,024-byte newline-framed record cap. It makes the
   result and its raw component-artifact tree read-only; the result records
   that tree's bounded file count and inventory hash.
7. Verify the separately owned successor protocol at
   `docs/design/durable-capture-monitored-protocol.json` still has SHA-256
   `e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71`.
   The protocol is immutable; readiness preparation must not edit it. Resolved
   artifact hashes and source commits belong in the run manifest.
8. Generate the exact 35-entry plan with `monitored-plan`.
9. Run `monitored-host-facts` against the actual private evidence root, then
   create the resolved run manifest. It pins the plan and successor hash; the
   unchanged fixture and oracle hashes; protocol, pipeline, adapter, runner,
   and monitor commits; runtime, tool, sample, and native binary hashes; current
   Linux/kernel/CPU/cgroup/filesystem facts; the clock conversion; all
   readiness artifact paths and hashes; the private roots; and the future
   authorization-receipt path.
10. Hash the completed manifest and create the authorization receipt. The
   receipt must bind that manifest hash, protocol hash, campaign ID, runner
   commit, monitor commit, independent reviewer, and all three affirmative
   execution flags. Make the receipt read-only before validation.
11. Run `monitored-validate`. Validation performs no capture. A successful
    result means only that the local artifacts satisfy the executable gate; it
    does not substitute for the independent review or authorize another
    manifest.

The current component generator deliberately records
`descriptorOnlyCampaignFeasibilityEstablished=false`. The permitted tiny
component probes enforce the 32-identity limit and report representative
observations, but they do not establish that the real O1 and live workers fit
that limit. `monitored-validate` therefore returns
`MonitorComponentEvidenceInsufficient` for generated artifacts instead of
turning a configured cap into a readiness claim. There is no manifest flag or
caller-provided `ready=true` override. Resolving this blocker requires
independently reviewed real-worker evidence and a corresponding reviewed
artifact-generation change before any campaign run.

The manifest records `MonitoredHostFactsReader` values from the execution host.
On cgroup v2, the reader resolves the process's `0::` membership through the
actual cgroup2 mount root and mount point from `/proc/self/mountinfo`. It reads
the membership directory and every ancestor up to that mount point; controller
files are not assumed to exist at `/sys/fs/cgroup`. The recorded facts include
each level's `memory.max`, `memory.current`, `cpu.max`, and
`cpuset.cpus.effective`, including explicit missing values. Effective available
memory is the minimum of host `MemAvailable` and every observable finite
ancestor limit minus that ancestor's current usage.

Validation compares stable host, mount, cgroup-membership, controller-limit,
CPU, and filesystem facts while allowing usage and free-space observations to
change. Immediately before campaign-directory creation, `monitored-run`
re-reads and revalidates all facts. The campaign-start receipt records this
fresh admission snapshot, including host memory totals, per-level cgroup
usage, and artifact-filesystem available bytes. The evidence root must have at
least 8 GiB free and effective host/cgroup memory must be at least 2 GiB at
both validation and admission.

## Execution lifecycle

The plan expands to exactly 35 executions: P0, P1/E, alternating A/B order for
Q1 through F5, then live blocks E-A-B, B-E-A, and A-B-E. Each execution has one
attempt and a 120-second maximum; the campaign has a 4,200-second maximum.
Persistent read-only campaign and attempt receipts prevent accidental reruns.
Every planned ordinal receives a pass, candidate failure, inconclusive, or
not-run outcome before the campaign is sealed.

Candidate A is the accepted direct SQLite adapter. Candidate B is the accepted
append-first log with a derived SQLite index. Only this experimental bootstrap
composes them. P0 uses the shared bounded pipeline. Q1/Q2 use the frozen
fixtures and independent oracle. N1 is paced at 100 offers/s, B1 and M1 hold
the writer until all 4,096 offers finish, O1 attempts 5,000 offers/s, and C1
stops after 1,000 offers paced at 1,000/s. F1 uses each backend's real
storage-full injection. F2 and F3 terminate the exact writer PID/start-time at
the declared adapter barrier. F4 terminates it after index/manifest work and
before seal. F5 creates a real second pipeline against the one-active-capture
gate.

F2-F4 recovery starts only after the old writer's death is confirmed and a
post-kill root inventory completes. Recovery uses new capture and artifact
IDs, preserves the source tree byte-for-byte, and requires acknowledged rows
to be a subset of recovered rows, recovered rows to be a subset of offered
rows, complete 64-row batches, no duplicates, and the case-specific barrier
result. F3's held post-commit/pre-ack barrier proves that both 64-row batches
were durably committed, so its recovery requirement is exactly 128 rows.
Source and recovery apparent lengths are checked together.

Each durable success uses one shared 10-second window for pipeline,
pre-seal, manifest, seal, exact inventory, publication, and immutability.
The pre-reopen boundary sweep completes before the separate scored timer
starts. That timer includes immutable manifest, seal, and member validation,
fresh reader opening, and complete materialization and encoded-size validation
of the first typed summary query. It stops before the post-reopen boundary
sweep. Both boundary sweeps remain inside the same 120-second execution budget;
the timer is not reset or extended, and periodic-monitor interference during
the measured interval is included without subtraction. Logical-byte scans and
semantic oracle queries occur after the reopen timer.

Each live execution starts a fresh published `CoreClrSample.dll` and a fresh
diagnostic worker. The load is `/cpu-burn?ms=10` at 20 requests/s, at most two
in flight, no redirects, and a one-second request timeout. Readiness is limited
to 10 seconds, warmup is 10 seconds, EventPipe collection covers seconds
10-44, and candidate A/B admit valid source ticks from that complete 34-second
collection. Candidate E invokes the unmodified shipping
`EventPipeCounterCollector`; raw tick and admission counts unavailable from
its returned aggregate are recorded as unavailable, not invented. All scored
request counts and latency samples use the same requests scheduled in
`[12s,42s)`; separately named whole-episode counters cover the rest. The Core
counter extractor supplies kind, interval, display-scale, and value semantics.
EventPipe relative timestamps use checked, midpoint-away-from-zero conversion
to 100 ns ticks; malformed payloads, invalid pipeline records, rejected new
keys, other admission rejections, and timestamp-conversion failures remain
separate visible evidence.

## Monitoring and evidence

The harness runs the same Linux monitor for E, A, and B at a 100 ms target
interval. Required synchronous observations occur before package admission,
after drain, around pre-seal finalization, after manifest/seal publication,
around recovery, and around ordinary reopen. Worker and target shutdowns use
PID plus `/proc/<pid>/stat` start time; monitor references are released before
intentional termination and disappearance is confirmed before removal.
Candidate E intentionally has no durable package; the monitor remains in the
harness so the instrumentation asymmetry is confined to the monitored
implementations rather than silently subtracted from their results.

Sweeps combine declared roots with regular-file descriptors from verified
harness, worker, and target identities. Device/inode identity deduplicates
aliases. Writable files outside roots and observable open-unlinked files are
charged; observed but unreadable or unclassified descriptors make monitoring
incomplete. Each Linux descriptor is first pinned with `O_PATH`; target,
flags, and device/inode identity are sampled twice and must describe the same
object before path, mutability, dependency, or runtime-only classification is
used. The only writable runtime-memory exclusion is the positively proven
.NET double-mapper identity described in the attribution map. Missing or
unreadable declared roots, symbolic links, hard links, path overflow, identity
overflow, descriptor-only overflow, summary overflow, a storage/RSS threshold,
or an active-stage all-observation gap over 1,000 ms aborts the current worker
and stops the remaining campaign. Periodic-to-periodic gap evidence is reported
separately and is not relabeled from boundary delays. The expected
disappearance of a killed worker's descriptor table is not itself proof of
completeness; the pre-kill observation, released observer references, exact
process death, and post-kill inventory are all required. Sweeps are explicitly
non-atomic observations, not true native peak guarantees. A storage alarm is a
candidate failure only when the same attributed alarm is present in the final
quiescent inventory; otherwise it is inconclusive.

The derived and enforced simultaneous-identity geometry is:

```text
64 retained packages * 4 files
+ 35 executions * 7 common execution-evidence files
+ 6 recovery executions * 4 additional recovery-evidence files
+ 2 campaign control files
+ 4 pre-existing readiness artifacts
+ 8 active source/recovery files
+ 32 charged descriptor-only files
= 571 identities
```

The rooted portion is a derived 539 identities; the separately enforced
descriptor-only portion is 32. These are not relabeled as observed peaks.
Representative component probes report their actual rooted and
descriptor-only maxima and inventory both candidates through transient
SQLite WAL/SHM state and immutable final packages; the observed maximum final
package count is four files. The monitor hard ceiling remains 4,096 identities
per sweep and 4,096 UTF-8 bytes per observed path. Normal campaign summaries
contain no path lists; bounded identity evidence is enabled only for component
feasibility probes.

One 120-second source/recovery token has a derived maximum of 1,201 periodic
plus 64 boundary records, or 1,265 summaries total. A separate short component
probe observes an actual source/recovery pair sharing one budget and linked
deadline and records its summary/control counts and bytes; negative probes
exhaust that shared budget and cancel both sides. The exact summary ceiling is
`2,048 * 1,024 = 2,097,152` bytes because each 1,024-byte record cap already
includes its newline. It shares the existing 8 MiB stdout budget with at most
64 independently newline-bounded worker control records.

The shared-budget probe's control records are explicitly encoded fixtures,
not worker-emitted observations. Its reported control counts/bytes are measured
fixture encodings, and the maximum-control-size field is the maximum over a
declared fixture population, not a universal observed campaign maximum.

`durable-monitored-sweep-summary/1` keeps
`observedSweepBytes` and `maximumObservedSweepBytes` explicit and uses pinned
compact keys for the other semantic fields. The complete compact-to-semantic
mapping and its SHA-256 are part of the encoding artifact. Component evidence
serializes a real summary with maximum numeric widths and 64-byte boundary and
alarm tokens, persists those exact newline-framed bytes and hash, and validates
them independently from the separate 2,048-record writer-saturation proof.
Boundary and alarm tokens accept only ASCII letters, digits, and `-_.:`;
JSON-escaped or non-ASCII input is rejected rather than invalidating the bound.
Monitor CPU uses Linux per-thread CPU time for the monitor code path;
allocation is measured on that executing managed thread. These metrics are
reported separately and are never subtracted or used to reweight scores.

After all 35 outcomes, the harness enumerates, hashes, and makes read-only every
campaign artifact before writing the campaign seal, then makes the complete
campaign directory tree read-only. The seal is not a production approval: any
missing execution, incomplete observation, invalid injection, source failure,
or unresolved alarm prevents a recommendation from being derived from a
convenient subset.
