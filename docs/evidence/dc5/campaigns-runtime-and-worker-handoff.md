# DC5: two inconclusive campaigns, runtime isolation and worker exit handoff

Both revision-7 prevalidation suites below covered all eight probes. Neither
subsequent 35-case campaign completed. Both campaigns are sealed and
**inconclusive**; no backend was selected and no performance recommendation
or production decision follows. This report preserves the earlier attempts,
including [the preceding geometry failure](prevalidation-schedule-682c185.md).

## Actual attempts

All four executions used source
`aa3c33b551177b7148cc4eb20e4760a74bc5b195` (#1036), not the worker-exit
correction accompanying this report.

| Attempt | Actual retained result | Sealed artifacts |
| --- | --- | ---: |
| `pv-unified-v7-04` | All eight coverage probes succeeded with the installed runtime | 2,438 |
| `campaign-unified-v7-01` | Four pass; Q2/B stopped with `IncompleteMonitoring` / `UnclassifiedReadOnlyDescriptor`; 30 not-run | 72 |
| `pv-unified-v7-05` | All eight coverage probes succeeded with the private runtime; independent review parsed 872 raw monitor records | 2,438 |
| `campaign-unified-v7-02` | Six pass; N1/A stopped with `IncompleteMonitoring` / `DescriptorEnumerationUnauthorizedAccessException`; 28 not-run | 95 |

The first campaign's readiness came from actual independent artifact review,
not an earlier source-only review. The second received a **new** independent
review of actual pv05 evidence, exact campaign02 inputs and normalized
binding. Its review basis is `runtime-campaign-readiness-review.json`, hashed
below. Old readiness was not transferred. Neither review applies to the
changed source or a future campaign.

## Read-only attribution and the private runtime

A controlled proof found the installed CoreCLR library had `nlink=2` and
reproduced the `UnclassifiedReadOnlyDescriptor` / `HardLinkRejected` error
shape. The installed host executable also lay outside the declared
dependency roots. This establishes input eligibility defects, **not the
identity of campaign01's historical failing FD**. That FD/path was not
retained and remains unknown.

The correction was a separate private runtime containing 335 byte-identical
files, totaling 109,656,640 bytes: host, host/fxr, Core and ASP.NET 10.0.12,
with no SDK. Every destination has an independent inode and `nlink=1`.
Owner-private read-only modes and inventory/hash checks protect the declared
inputs against accidental changes; they are **not immutability against the
owner or root**. The installed runtime was not changed. A child-scoped PATH
prefix selects the private host for name-based sample launches; no global
environment or DOTNET_ROOT override was added. PATH is bound separately
because the protocol's selected-environment hash does not include it.

No hard-link or unknown-read-only exception was added. The forensic observer
hook and tests in the separate read-only-attribution worktree are not included
in this change. Runtime isolation did not identify the historical FD.

## Worker exit defect and bounded correction

Campaign02 retained a successful pre-worker-termination sweep followed by a
periodic `DescriptorEnumerationUnauthorizedAccessException`. Its worker
result says pass, but monitoring failure correctly makes the execution
inconclusive. The worker's failure-time proc-stat, underlying errno/path and
actual exit timestamp were not retained. A result-file mtime is not a
synchronized exit timestamp. The exact historical kernel interleaving is
**not proved**.

The implementation nevertheless had a reproducible lifecycle defect:
marking intentional termination and then releasing the worker allowed an
in-flight or subsequent descriptor sweep during native teardown. The old
remote prepare-termination operation drained a sweep but released its gate
too early. `baseline-race-02.trx` deterministically fails the extracted old
ordering because release happens during an active sweep. The first proof
attempt had a cleanup broken-pipe error masking the assertion; both attempts
remain retained, and only the corrected second attempt is the regression
basis.

The shared lifetime operation now verifies a live registered exact
PID/start-time owner, holds the existing sweep gate through release and
confirmed exit, removes that exact owner, and releases the gate in `finally`.
It is wired through all four control paths: local campaign worker release,
remote prevalidation worker release, coordinator exit-control authority and
harness exit. The coordinator acknowledges exit while holding the gate and
confirms removal before serving the following status fence.

There is no new detached task, timeout, ownership exclusion, permission-loss
allowlist or hard-EACCES exception. Cancellation, release failure, unexpected
death, identity mismatch and result/exit-code disagreement remain failures.
Existing deadlines, polling-gap alarms, workloads, resource limits, protocol
hashes and exact final inventories are unchanged.

## Component evidence and review limits

The actual pre-publication durable component run passed **586/586**, including
12 new cases; the real EventPipe case
`BaselineLiveCaptureUsesShippingCollectorAndDoesNotInventRawTickCounts` was
explicitly excluded. The deterministic fixed-ordering regression passes.
Authority-wire checks passed 5/5. `worker-exit-component/investigation.json`
records the changed-file hashes, earlier attempts, exact test receipts and
causal limitations.

The independent `worker-handoff-review` code-review agent (Claude Sonnet 5)
reviewed the seven actual implementation/test/design-document files and
returned: "No significant issues found in the reviewed changes." This is
source review only, not review of this subsequently added report, fresh
artifacts, future readiness or performance approval.

## Persistent identities

Paths below are relative evidence labels, not public downloads. Local raw
artifacts remain outside Git; no home paths, user identifiers, bulk logs,
secrets or raw memory are copied here. SHA-256 binds the exact retained bytes.

| Artifact / source | Exact SHA-256 or Git commit |
| --- | --- |
| Executed source | `aa3c33b551177b7148cc4eb20e4760a74bc5b195` |
| Frozen revision-4 protocol | `e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71` |
| Frozen revision-7 protocol | `23919c85adaf688db41a286cbe2bc90fb4687b3b3d3222d8372c276b40d03149` |
| Revision-7 context map | `0b590628a234fbe0c50baa1ce5b3c64b8b8fd9e86dd3fffcdcc34c91f92c155b` |
| Frozen prevalidation addendum | `b825cb4a15b66d6b1ac4a245ffffb22e192f5bcb5625e65087b1cf0b51374bb5` |
| pv04 manifest | `c80775631e4ecd2effcf6bd6e86e5eb5e93b0458d6ca86e42f0ca811b1d7bbcd` |
| pv04 seal | `3dad6212bd3bcfe073297cb6c4dd48dc58c95a70225a04b45d8ecc64ff8f5cc9` |
| campaign01 manifest | `fcd0b737007cc4d93a11ddd10923df89ce714f4d17cf0fdf33a1bdc6b7d56c43` |
| campaign01 seal | `bf55218fe84df151eb4231bdbd892f01f9171aa855c3ddb9e2940cd8ffc5034c` |
| Read-only controlled report | `4301c56dabbcb85730406673e8c6374ea12c543bf4815ebb4242f8304354ae98` |
| Runtime isolation report | `1822ee6353d05a5306dbcf445bfb28417c10a053354e990f038dedc76a8e9c4f` |
| Runtime copy inventory | `31c5e046bdabb01b477a4113e0e1960b1658375ef87d30527fb6c858d0c1dcb5` |
| pv05 manifest | `c7526a7a50c8d8b77f0b1118c00d4524c219236d942e2fdabe014af128c2c00c` |
| pv05 seal | `cf866e42298d36dd40703276a90aa4426850b17e699a4c1181f949a19e7cd7eb` |
| New campaign02 independent review basis | `50f3c637bc8c9a383e7c2be291e52dd75de5a5b28f084283f095885d7134beb3` |
| campaign02 manifest | `afc10b8741e1f0738135b2ff74a4a9ddbd9dc0342b0a2618c61f0f9bad91f9c4` |
| campaign02 seal | `82b00cf04d097ac7db8b1583545379814bfbb19b09819dd18e2e1acb8b91f0de` |
| Worker-exit investigation | `c07300ab0eef2387e99934b6dab9ea52791e24b88b367d3e2b3771a7f17e4a00` |
| Reviewed source patch | `3db974fb93d23e7c93961df9f1afb6f4178ce965686f2d5a5d1d1702bd7e43c0` |
| Old-ordering regression, second attempt | `945003df2c0bf2ccccf520529d3e71d815258141c6b4a32d7ebe1963f8316ded` |
| Pre-publication 586-case TRX | `26d86cf2b9592582bb109a5acd3e6308e3b9a8d213e7e6b835e6a94bb02f59ba` |

## Next authorization boundary

The handoff correction has not run an eight-probe suite or a 35-case campaign.
Publication must precede its final source-bound build. A clean committed
build with SDK 10.0.201, exact tool/support `+HEAD` metadata, new component
proof (feasibility still false), fresh inputs and independent artifact review
are required before parent acceptance and one-suite authorization. Only a
new successful all-eight sealed suite and its independent, exact prospective
campaign review can establish new readiness. Do not resume either consumed
campaign, transfer pv05 readiness or overwrite earlier evidence. Ref #1003
and #999; neither is complete. No merge or product decision is authorized.
