# ClrMD Linux revalidation (#685)

The **ClrMD Linux revalidation** workflow is an opt-in evidence gate, not a
replacement for ordinary CI. It does not remove `[SkipOnLinuxCiFact]`, change
`ci-test-with-retry.sh`, close #147/#685/#879, or resurrect the retired preload
and strace reproduction workflows.

## Running it

- On a same-repository PR, add the `clrmd-revalidation` label. Opening, reopening,
  or pushing to a labeled PR starts a fresh sequence against the PR merge ref.
- After the workflow reaches the default branch, it can also be dispatched
  manually against a selected ref.
- One Ubuntu runner builds once, then runs **20 consecutive iterations**, each
  consisting of Core followed by the MCP integration suite. Core excludes only
  `CrashGuard_CapturesUnhandledException_FromBadCodeSample`, which deliberately
  crashes a child process and causes blame to emit an expected dump. That test
  remains in normal CI, not this zero-crash gate. MCP excludes the usual
  `KindIntegration` / `DockerIntegration` categories. Cross-version target
  runtimes are installed as in normal CI.
- The quarantine override is enabled. Each suite uses a fresh testhost and direct
  `dotnet test`, **never the retry wrapper**. The first test, timeout, discovery,
  or evidence-verification failure ends the sequence; no partial sequence counts
  as 20 clean runs.
- The job has a 360-minute ceiling (340 minutes for the sequence), each suite a
  15-minute ceiling, and each individual test a five-minute hang limit.
  There is no 20-way matrix and no cross-assembly parallel execution.
  Hosted timings (~8 minutes for unquarantined Core and ~4 minutes for MCP)
  imply ~240 minutes for 20 iterations before overhead; the original 220-minute
  sequence budget could not accommodate that workload.

## Required evidence

Each iteration retains separate TRX and console logs. Verification requires:

1. Successful command exit, a completed nonempty TRX, consistent counters,
   result/definition/entry identities, and all executed tests passing.
2. Every independently discovered test represented (including expanded theory
   cases), with a stable name/outcome inventory after iteration 01.
3. Every source-annotated quarantined Core test explicitly **Passed**, plus live
   and dump-origin DAC coverage. Unrelated platform skips remain allowed.
4. The MCP test named in #879 explicitly **Passed**, alongside the rest of its
   suite. This supplies per-assembly evidence; it does **not** establish that
   #879's concurrent aggregate-solution reproduction has the same root cause.
5. No host-abort/crash signature in the console and no blame dump, crash report,
   or interrupted-test sequence artifact.

Artifacts include discovery inventories, package versions, SDK/runtime and commit
information, and the completed-iteration ledger. TRX/log evidence is uploaded
even on failure; dumps are uploaded only on failure. A canceled or timed-out job
is not clean evidence even if some iterations passed.

Only after all 20 iterations and ordinary Linux CI are clean should a separate
change consider removing quarantine or retry masking. Review any remaining
failure on its own evidence; #909's EventPipe GC timing flake is not fixed by
this dependency update.

## Package verification for 4.1.745802

NuGet's package repository commit is
`ec63d005a9d35c079bf2357d57c02d7ff62eeb36`. GitHub's public commit/source API
returned 404 for that commit during validation, so ancestry from
[`microsoft/clrmd#1499`](https://github.com/microsoft/clrmd/pull/1499)
(`153fe216e0d33172105010ed360656147144adbf`) is **not established**.

Metadata-only inspection of the restored `lib/net10.0/Microsoft.Diagnostics.Runtime.dll`
instead confirms the shipped fix's behavior: `DacLibrary`'s constructor computes
`IsCDacFileName(dacPath) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`
at IL offsets `0x012f`–`0x0147`, then passes that boolean to
`RefCountedFreeLibrary` at `0x015e`–`0x0164`. There is no target/current-PID
restriction on suppressing DAC unload. This is the changed condition in #1499,
not an inference from the package publication date.

- Module MVID: `7cc9d766-16f3-46bd-967e-0180180ffd7d`
- DLL SHA-256: `52d5f113180bb0f70b1a9b10608aef93b187364ac689885d2d3c734102d850a2`

This binary evidence justifies revalidation, not a claim that every observed
native crash is resolved.

## CPU-diff assertion found during revalidation

[Run 34527622776](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/34527622776)
stopped in the first Core suite: 1,221 passed, four skipped, one assertion
failure in `Diff_CpuSample_DetectsRegression`, with no host crash. The verdict
assertion passed, but the returned top-25 added/changed rows did not contain the
required increasing generic method. That run did not retain the actual diff,
so it cannot distinguish a non-increasing generic share from ranking truncation.

The test saturated `/generics` in both windows, increasing iterations per request
from 20,000 to 200,000. CPU comparison measures **exclusive sample share**, not
iterations, request latency, or absolute CPU work. The same saturated workload
does not guarantee an increasing share; small relative changes can also rank
behind unrelated changes in the top-25 result. Five instrumented original local
runs passed, but reported generic shares around 2–3.5%, not a tenfold increase.
This is an invalid workload assumption, not evidence of a collector regression.

The test now contrasts the existing busy `/render` workload against `/generics`.
It requires an **added** generic method with positive exclusive samples and a
module MVID/metadata-token identity, retaining the original verdict, threshold,
top-25 bound, collection durations, and quarantine. It records the diff in test
output. The load helper also propagates HTTP failures and stops/awaits its driver
even if collection fails. Deterministic unit cases cover equal, increasing, and
decreasing sample shares despite larger absolute sample counts.

Ten consecutive local targeted invocations passed (nine cases each; the last
five restricted to two CPUs), plus all three other load-helper callers. These
are focused test-fix checks, **not** the required 20 full Core+MCP iterations.
Any hosted sequence after this change starts again at iteration 01.

## Local verifier checks

```bash
python3 -m unittest discover -s scripts/tests -p 'test_verify_clrmd_revalidation.py'
bash -n scripts/revalidate-clrmd-linux.sh
```

The runner itself is `bash scripts/revalidate-clrmd-linux.sh`, after a Release
build and runtime installation. It refuses to overwrite an existing
`TestResults/clrmd-revalidation` directory so stale results cannot be counted.
