# Representative live ClrMD compatibility (advisory)

Issue #931 prepares **four** representative live/layout checks against actual
.NET 8/9/10 targets on **Linux x64**, not all-reader parity. A separately
authorized corrected batch passed all twelve facts against actual
**8.0.31/9.0.20/10.0.7** targets. The [sanitized local evidence report](live-clrmd-compatibility-evidence.md)
records the exact tested commit, topology and identities, including two earlier
failed revisions that remain failed. See also the
[compatibility matrix](runtime-version-compat-matrix.md). This is not hosted CI
or universal runtime coverage.

## Fixture and assertions

`MultiVersionSample --live-compatibility` is an explicitly owned deterministic
fixture, not a production-target modification. Default allocation and GC modes
are unchanged. It retains exactly 32 `RetainedMarker` objects (128 KiB payload),
one `PendingAsync` task awaiting a rooted signal, and one parked dedicated thread
inside non-inlined `ClosedGenericHold<int>`. It prints readiness only after
creating these identities and exits after 180 seconds (plus at most five seconds
of join). No event stream, dump or trace is retained.

`CrossVersionLiveSidecarTests` uses the existing `ClrMdDumpInspector` live path,
`ClrMdThreadSnapshotInspector`, async walker through the heap artifact, and
`ClrMdMethodInstantiationEnricher.Resolve`. The generic test feeds the known
parked frame's IP and token/MVID identity into the same resolver used for opt-in
CPU enrichment. This tests the resolver, **not statistical EventPipe sampling**.
`System.__Canon`, open definitions and unknown arguments cannot satisfy the
required `System.Int32` assertion.

Each of four facts independently checks the actual target major through diagnostic
IPC, corroborated by the target's mapped CoreCLR module, not the image tag,
framework moniker, requested major or inspector runtime. Assertions
include live origin and named fixture types/frames, exact retained count,
pending async state/awaiter, and concrete closed method argument. Successful
attach or nonempty JSON alone cannot pass.

### Version and generic identity evidence

The original batch against a self-reported .NET8.0.31 target produced live heap
and thread snapshots whose raw ClrMD version was `0.0`. Static inspection of the
pinned ClrMD assembly (MVID `7cc9d766-16f3-46bd-967e-0180180ffd7d`) shows:
`LinuxLiveDataReader.GetModuleInfo` creates `ElfModuleInfo`, whose version getter
returns an empty `Version` when ELF/version lookup fails. The Unix lookup searches
a writable ELF load segment for a runtime version marker. Thus `0.0` is an
**unknown metadata sentinel**, not evidence that the target runs CLR major zero.
Retained snapshots do not establish which lookup subcondition failed.

Tests now use the existing Core `ProcessInfoReflection` bridge to diagnostic IPC
process-info metadata (the pinned client exposes that method internally).
Unavailable metadata fails; there is no fallback to a configured expectation.
Each fact requires response PID1, Linux/x64, the actual fixture command line
from IPC and `/proc/1/cmdline`, a stable process start time, and exactly one mapped
`libcoreclr.so` path whose version agrees with the IPC product version. The
runner binds that PID namespace to the owned image/container and target-local
DAC. Missing, zero, conflicting or uncorroborated IPC versions fail. The original
raw ClrMD version remains separately recorded and is never overwritten.

The retained generic frame's raw method name includes
`ClosedGenericHold[[System.Int32, System.Private.CoreLib]]`. That is valid ClrMD
metadata, not necessarily a product defect. Tests locate the fixture MethodDef
in the mounted sample using metadata-only PE reading, then select exactly one
frame with that token/MVID and the existing normalized Int32 signature.
Shared-canon, another concrete argument, wrong identity and ambiguous frames
are rejected. The real production enricher still must return the concrete
`System.Int32` method argument and exact closed signature; raw stack text alone
does not establish enrichment. That resolver was not reached in the first batch;
the successful corrected batch invoked it and passed both the concrete-argument
and exact-signature assertions on each runtime.

## One topology, least privileges

The host runner owns two containers per runtime, sequentially:

- Target: runtime image, UID/GID `0:0`, all capabilities dropped, no network,
  read-only root filesystem, 256 MiB memory and 128-process limit.
- Inspector: SDK **10.0.201** image, same UID/GID, all capabilities dropped except
  `SYS_PTRACE`, `--pid container:<exact-owned-target-id>`, no network, read-only
  root filesystem, 1 GiB memory and 128-process limit.
- Both mount the same diagnostic directory with `TMPDIR=/diagnostics` and the
  same read-only sample path. The inspector also mounts compiled test outputs
  read-only. The target-local runtime/DAC is copied from the owned target before
  startup and mounted read-only at its original versioned path in the inspector.
  This avoids a DAC download and preserves sample MVID/path resolution.
- After both diagnostic roles have been stopped, removed and verified absent,
  an optional finite cleanup role uses the already-resolved SDK image ID, UID0,
  network-none, cap-drop-ALL and a read-only root. It mounts **only** the exact
  canonical diagnostic scratch directory read-write, with recursive bind mounts
  disabled. `find -P -xdev -depth` removes contents without following symlinks or
  nested filesystems; the host removes the empty directory. It has no ptrace
  capability or shared target PID namespace and is separately reaped.
- No privileged containers, host PID namespace, Docker socket mounts, broad
  seccomp disabling, host Yama/sysctl changes, or production-target application changes.
  Docker's normal seccomp profile plus the inspector capability must permit
  the attach; an unavailable prerequisite must not be called a pass.

The runner never restores or builds inside a container. The recorded local builds
used the already configured/authenticated private NuGet feed. It never creates credential
files or passes credentials to images. Image provenance records content IDs and
digests even though provisioning uses servicing tags.

## Build and local acceptance

Use SDK 10.0.201, the environment's authorized configured feed (private for the
recorded local run), Linux x64 Docker and locally
available `mcr.microsoft.com/dotnet/sdk:10.0.201` plus
`mcr.microsoft.com/dotnet/runtime:{8,9,10}.0` images. Public NuGet is not a
fallback for environments where it is prohibited. This deliverable is the
**local advisory runner**, not an operational CI job. The earlier proposed
self-hosted workflow was removed because no matching runner was provisioned;
shipping it would only queue indefinitely. No runner, secret or repository
configuration change is performed here.

Build the Core test project and all MultiVersionSample TFMs in Release using the
normal private configuration. Pure checks (no target starts) are:

```bash
python3 -m unittest discover -s scripts/tests -p test_live_clrmd_compat.py
dotnet test tests/DotnetDiagnostics.Core.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~MethodIdentityHandoffTests|FullyQualifiedName~AsyncStateMachineFrameFolderTests|FullyQualifiedName~LiveCompatibilityEvidenceTests'
```

After explicit acceptance approval, run once with a **new** persistent directory:

```bash
python3 scripts/live-clrmd-compat.py --output artifacts/live-clrmd-compat-attempt-1
```

The four external-target facts skip explicitly in ordinary unprovisioned runs.
In provisioned slots the major environment variable enables them; bad values or
platforms fail. There is no successful no-op path.

The existing `CrossVersionTargetTests` remains separate EventPipe/dump coverage;
do not relabel its output live ClrMD evidence. Existing Windows net10
`DumpInspector_InspectLiveAsync_FindsPendingAsyncStateMachines`,
`ThreadSnapshot_InspectLive_EnumeratesManagedThreads` and
`CpuSampler_ResolvesMethodLevelClosedGenerics_OnlyWhenOptInEnabled` remain
unchanged controls. No new Windows cross-version or NativeAOT claim is made.

## Bounds, outcomes and evidence

- One slot per runtime in order 8, 9, 10; no pass-seeking retries.
- Cooperative capture cancellation at 25 seconds and test timeout 30 seconds;
  external test-command watchdog 140 seconds is authoritative if native attach
  does not honor cancellation. Discovery is bounded to 20 seconds.
- Each slot has a 240-second wall-clock alarm; global work deadline 900 seconds,
  including a 60-second prerequisite deadline. Cleanup replaces the capture
  alarm with its own 140-second wall-clock budget, including final metadata,
  stop/removal, verification and any 20-second scratch-cleanup helper.
  Both container main processes also have finite 180-second lifetimes.
- Exact generated names plus an ownership label recover ambiguous Docker-create
  timeouts. Cleanup verifies the label then removes only that container ID;
  it never prunes or stops unrelated containers. Unverified role removal leaves
  both runtime/scratch directories untouched. Collection outcome, container
  cleanup and filesystem cleanup are recorded separately. Every filesystem
  failure remains an explicit cleanup-failure **with the executed slot retained**
  in the aggregate manifest; it stops later slots. Interruption records abort,
  not pass. The original batch's aggregate omitted its executed slot after an
  escaping filesystem exception; the original TRX and per-slot result remain
  the authoritative four failed cases, not a zero-execution result.
- `run-scenario-attempt.py` supervises discovery/tests with no forensic helper:
  this reuses the generic bounded process-attempt contract, not frozen
  investigation experiments. `test_evidence.py` checks independent discovery,
  completed TRX and **all four exact required-Passed names**. Extra cases, skips,
  missing discovery/TRX, bad exits and aborts cannot count as coverage.
- Manifest outcomes separate pass, missing/unsupported prerequisites, assertion
  failure, timeout, abort and cleanup failure. Missing majors stay explicit.
  Permission failures during the live assertions remain failed evidence, never
  automatic skips.

Retained output includes commit/dirty status, kernel, Docker/image identities,
UID/capability/PID setup, target runtime and DAC hash, ClrMD binary hash and
assembly version, named drilldown output in TRX, discovery, logs, exits,
watchdogs and cleanup. Runtime copies/socket directories are removed after
cleanup and excluded from upload. Docker logs are capped at 1 MiB per container;
fixture population, threads, frames and tests are fixed. Dumps, nettraces,
package caches and environment/credential dumps are not evidence artifacts.

Before claiming compatibility, retain the bounded result table and artifacts in
the agreed durable evidence location and link them from the matrix. Missing
image/ptrace prerequisites are actionable missing evidence, not validation.

### Future workflow decision (not shipped or dispatched)

A minimal future integration could be manual/advisory on one hosted Linux x64
runner, using the repository's normal environment-configured restore/build
conventions, serial slots and failure artifact upload. It must select SDK10.0.201
and honor that environment's authorized feeds. It must not hardcode a developer
machine's private endpoint or credentials, invent a self-hosted label, bypass
local feed policy, or become a required check without a separate decision.
Successful local evidence does not itself authorize that delivery surface:
hosted integration and provisioning still require a separate decision.
No operational workflow is claimed here.
