# Representative live ClrMD compatibility (advisory)

Issue #931 prepares **four** representative live/layout checks against actual
.NET 8/9/10 targets on **Linux x64**, not all-reader parity. No successful
execution evidence has been recorded yet; see the
[compatibility matrix](runtime-version-compat-matrix.md).

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

Each of four facts independently checks the actual CLR major from a live
snapshot, not the image tag, framework moniker or inspector runtime. Assertions
include live origin and named fixture types/frames, exact retained count,
pending async state/awaiter, and concrete closed method argument. Successful
attach or nonempty JSON alone cannot pass.

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
- No privileged containers, host PID namespace, Docker socket mounts, broad
  seccomp disabling, host Yama/sysctl changes, or target application changes.
  Docker's normal seccomp profile plus the inspector capability must permit
  the attach; an unavailable prerequisite must not be called a pass.

The runner never restores or builds inside a container. Host builds use only the
already configured/authenticated private NuGet feed. It never creates credential
files or passes credentials to images. Image provenance records content IDs and
digests even though provisioning uses servicing tags.

## Build and deferred acceptance

Use SDK 10.0.201, the configured private feed, Linux x64 Docker and locally
available `mcr.microsoft.com/dotnet/sdk:10.0.201` plus
`mcr.microsoft.com/dotnet/runtime:{8,9,10}.0` images. Public NuGet is not a
fallback. The manual workflow uses a pre-provisioned self-hosted
`dotnet-diagnostics-private-feed` runner; that label requires Docker, Python,
pinned SDK and the existing authenticated private feed. No runner provisioning
or credential change is performed by this lane. It is **not a required check**.

Build the Core test project and all MultiVersionSample TFMs in Release using the
normal private configuration. Pure checks (no target starts) are:

```bash
python3 -m unittest discover -s scripts/tests -p test_live_clrmd_compat.py
dotnet test tests/DotnetDiagnostics.Core.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~MethodIdentityHandoffTests|FullyQualifiedName~AsyncStateMachineFrameFolderTests'
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
  including a 60-second prerequisite deadline. Cleanup disarms capture alarms
  and uses separate bounded Docker calls (at most 140 seconds per slot).
  Both container main processes also have finite 180-second lifetimes.
- Exact generated names plus an ownership label recover ambiguous Docker-create
  timeouts. Cleanup verifies the label then removes only that container ID;
  it never prunes or stops unrelated containers. Cleanup failure stops later
  slots and remains a failure. Interruption records abort, not pass.
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

Workflow artifacts expire after 14 days. Before claiming compatibility, retain
the bounded result table and artifacts in the agreed durable evidence location
and link them from the matrix. Missing runner/image/ptrace prerequisites are
actionable missing evidence, not validation.
