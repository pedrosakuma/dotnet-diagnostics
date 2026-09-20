# Local live ClrMD compatibility evidence — 2026-09-20

This is the sanitized, durable report for issue
[#931](https://github.com/pedrosakuma/dotnet-diagnostics/issues/931).
It records **one successful local Linux x64 sidecar batch** on the exact runtime
versions below, not universal runtime/OS parity or hosted CI coverage.
The [runner contract](live-clrmd-compatibility.md) describes reproduction,
ownership, bounds and failure handling.

## Tested source and result

**Tested code commit:** [`cf6122fc13306661097640471b7fdec45bb9a274`](https://github.com/pedrosakuma/dotnet-diagnostics/commit/cf6122fc13306661097640471b7fdec45bb9a274).
The later documentation-only publication commit is not a newly live-tested
binary. Any hosted build of that publication head is separate evidence.

The batch ran **2026-09-20 01:46:54–01:47:46 UTC**, in order 8, 9, 10, with one
slot per runtime and no retry/replacement. Runner exit0; **12 passed, zero failed,
zero skipped, zero unexecuted**. Independent discovery and complete TRX agreed
on exactly the four required facts in every slot.

| Actual target runtime | `LiveHeap_RetainsNamedPopulation` | `LiveThreads_FindNamedGenericFrame` | `LiveAsync_FindsPendingFixture` | `LiveGenerics_ResolvesConcreteInt32` |
|---|---|---|---|---|
| **8.0.31** | Passed | Passed | Passed | Passed |
| **9.0.20** | Passed | Passed | Passed | Passed |
| **10.0.7** | Passed | Passed | Passed | Passed |

The facts belong to `DotnetDiagnostics.Core.Tests.CrossVersionLiveSidecarTests`.
They used actual running targets, not dump files, mocked collectors or
nonempty-output-only assertions.

## Meaningful observed identities

Every fact obtained diagnostic-IPC process metadata for the owned target PID1,
corroborated Linux/x64 and the fixture command line through IPC and procfs,
checked process start identity, and matched the IPC version to the single mapped
`libcoreclr.so` version. The runtime values in the table are observations, not
requested TFMs, environment-variable expectations or image-tag assumptions.

On each runtime:

- Live heap inspection found **exactly 32 `RetainedMarker` objects** in the
  mounted `MultiVersionSample` module.
- Live thread inspection selected the known `CompatibilityFixture` frame by
  the fixture MethodDef token/MVID and normalized closed-Int32 signature.
- Live async analysis found **`CompatibilityFixture+<PendingAsync>d__2`** in
  **state0**, with a `ConfiguredTaskAwaitable+ConfiguredTaskAwaiter`.
- The real **`ClrMdMethodInstantiationEnricher.Resolve`** returned one identity
  with matching sample token/MVID, generic arity1, no type-level arguments,
  method arguments **`["System.Int32"]`**, and exact closed signature
  **`CompatibilityFixture.ClosedGenericHold<System.Int32>`**. The single-result,
  MVID/token/raw-name, concrete-argument and exact-signature assertions all ran
  and passed; this was not inferred solely from a raw stack frame.

| Runtime | Actual sample module MVID | MethodDef token |
|---|---|---|
| 8.0.31 | `a7b123f7-c6d3-41c7-b5a5-78c95025eebc` | `0x06000003` |
| 9.0.20 | `f2fdea15-3366-4437-80d7-4346450213bc` | `0x06000003` |
| 10.0.7 | `c62fcd4b-3b3b-4c73-8da8-8356a238434d` | `0x06000003` |

**Raw ClrMD runtime version remained `0.0` on all three targets.** It is retained
as unknown metadata, separately from the corroborated IPC runtime version; it
was not overwritten or accepted as proof of the expected major. The test
architecture uses an existing Core IPC bridge, not a new production fallback.

## Exact environment and privilege scope

- Local Docker server **29.6.2**, Linux/amd64.
- Container-observed kernel **`6.18.33.2-microsoft-standard-WSL2`**.
- Build and inspector SDK **10.0.201**; post-commit targeted builds used
  `--no-restore`. No package/dependency upgrade occurred.
- ClrMD package **4.1.745802**; loaded assembly
  **`Microsoft.Diagnostics.Runtime, Version=4.1.14.45802`**,
  MVID **`7cc9d766-16f3-46bd-967e-0180180ffd7d`**.
- Target and inspector used matching **UID/GID0**, network-none and read-only
  root filesystems; the diagnostic directory was shared.
- Inspector used **`--pid container:<owned-target-id>`**, not host PID.
  Target capabilities were all dropped. Inspector capabilities were all dropped
  before adding only **SYS_PTRACE**. Actual effective-capability observations
  were target `0` and inspector `0x80000`.
- No privileged container, host Docker-socket mount, broad seccomp disabling or
  host Yama/sysctl change. Only explicitly owned deterministic fixtures ran.
- The exact target-local runtime/DAC was copied before target startup and
  mounted read-only at its versioned path in the inspector.

All nine owned roles (three targets, three inspectors, three cleanup helpers)
were removed and their exact-ID absence verified. Owner-label enumeration
returned zero remaining containers. Each cleanup helper exited0 and used only
its canonical scratch bind, with recursive binds disabled, UID0, network-none,
cap-drop-ALL, read-only root and no-new-privileges. All three scratch and all
three runtime-copy directories were absent afterward.

## Content identities

The image IDs and recorded repository digests agreed; no image pull or
servicing-tag refresh occurred for the successful batch.

| Image | Content ID / digest |
|---|---|
| SDK 10.0.201 | `sha256:127d7d4d601ae26b8e04c54efb37e9ce8766931bded0ee59fcd799afd21d6850` |
| Runtime 8.0.31 | `sha256:37466ea190f696105c1c3ae67c15e32d4e199face9a0b2ad5b9a37c464db8f30` |
| Runtime 9.0.20 | `sha256:8922cef0719da00335c6e3356007362b7015de9d2b15fb6e9d790f2e6e729c9a` |
| Runtime 10.0.7 | `sha256:8fb7ff015fcf0ebc6e90105bd6db06875954e6dc3d374b9dbb34c732867d13e4` |

| Artifact | SHA256 |
|---|---|
| Target8 `libmscordaccore.so` | `eae73c83fcfa82a6958e4d61e840fd1db72cee27450d1d9cf67426db864bca21` |
| Target9 `libmscordaccore.so` | `956580d7bda37a0b8a53d6f5a67e6b6bc7497104b88809172c1b51bcfa416246` |
| Target10 `libmscordaccore.so` | `8d29da8bd96f98177de199f1afc86ab4d3c7b3f4f0c64420acaca888e0a02bb1` |
| Loaded ClrMD binary | `52d5f113180bb0f70b1a9b10608aef93b187364ac689885d2d3c734102d850a2` |
| Built Core test assembly | `85d58b178ca1c0cfcfe72975df75ede50439c588ec7e7e3c60a5221610ac02a1` |
| Built Core assembly | `a9e2c2fea5089f54ca903287ea56c7f2482e143f166d3663546ee44494786caa` |
| Sample net8.0 assembly | `01e6f79b3fc2b4eee09f9c637b13867aeb425d6c5d1c09e23354bc9735cc4c1c` |
| Sample net9.0 assembly | `37ae2b145cbc72ffb425c4fbb0fd12b977701cf8b5fdf9573b360e28fb8d86d9` |
| Sample net10.0 assembly | `3a9676efd29e03e879729923b6696274c0570348f6a28adf2f15d1e9d2a71d17` |
| Runtime8 TRX | `f82f91cf96c1e6b24147316127eee1ae93da5fa9a5efe77562e73cc603a4b699` |
| Runtime9 TRX | `d51fee0aa8b6c91fcd3bd98a8c9456806b5f7ae875563b97d3dbcf7f5240b9fc` |
| Runtime10 TRX | `4dab555bf8741b98f279b67544bca8c5abad8b2226cb6926536a1a3a5e152f75` |

The controlled local evidence set includes full TRX/logs, discovery inventories,
actual drilldown results, exit/watchdog states, container/image provenance and
cleanup verification. Raw private artifacts, host paths, object addresses and
environment contents are deliberately not copied into this report.

For the recorded acceptance, a separately reviewed local command guard froze
**1177 source hashes and 180 output hashes**, image IDs and target DAC hashes.
It checked identity agreement before diagnostic creates/starts/execs and
restricted cleanup to verified owned roles and canonical scratch paths. The
successful guard differed from the reviewed guard only in output/manifest
bindings. Postflight hashes agreed. This additional guard was part of this local
acceptance protocol, not a claim that the shipped runner enforces every such
external provenance constraint by itself.

## Failed attempts remain failed

Each row is a separately authorized source revision, not a retry of unchanged
code. Prior failure artifacts and hashes were preserved.

| Tested revision | Passed / failed / unexecuted | What happened |
|---|---|---|
| `2d5db9860d2a61bc18923245089ea7866134c606` | **0 / 4 / 8** | .NET8 facts failed on raw ClrMD `0.0`; filesystem cleanup then escaped after per-slot/TRX output. The aggregate's empty slot list did not erase those four executions. |
| `7c3d498920efd3e2aa49f4e78d60ed670e340f87` | **9 / 3 / 0** | Runtime binding and cleanup worked. The actual enricher returned Int32 on all targets, but an assertion overload treated its explanatory sentence as a second expected item. Those facts stayed failed. |
| `cf6122fc13306661097640471b7fdec45bb9a274` | **12 / 0 / 0** | Explicit one-item collection plus named assertion reason, with pure positive/negative controls, allowed the intended assertion and subsequent exact-signature check to execute correctly. |

Corrections were confined to fixture/test evidence and owned cleanup, not
production collectors, runtime-layout fallbacks or dependency upgrades.
No case from a failed batch was reclassified as passed.

## Limits and delivery status

- This is **local advisory evidence**, not a hosted CI run. No operational
  live-compatibility workflow or matching self-hosted runner was provisioned;
  the proposed unprovisioned workflow was removed. Ordinary CI can exercise
  pure controls without executing these provisioned sidecar facts.
- One topology, one exact patch version per major, one successful batch:
  no repeated-run reliability, other kernel/architecture, non-root-UID,
  Windows cross-version or universal .NET8/9/10 claim.
- Only representative live heap/thread, one pending async shape and a concrete
  value-type generic resolver path are covered. No all-reader/all-drilldown,
  statistical CPU enrichment sampling, shared reference-type generic recovery,
  NativeAOT or method-parameter instrumentation claim.
- Existing EventPipe/dump cross-version tests and Windows net10 controls remain
  separate evidence; they are not relabeled as this live acceptance.
- A different environment must meet its own runtime/DAC, PID/socket/UID and
  ptrace prerequisites. Missing prerequisites or skips do not count as coverage.
- Required-check promotion, hosted integration and issue closure remain separate
  decisions. This report does not authorize a workflow or infrastructure change.
