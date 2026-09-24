# DC5 campaign 03: separate producer and adapter limits

This is a component correction and historical evidence report for #1003 and
#999, not a backend selection, campaign success, or readiness approval.
The unchanged historical source is
`35cf8f3ef7599f5b75e137eb1b574686bd056ef1`. Its worktree, binaries,
consumed controls, outcomes and seals remain untouched.

## Exact historical evidence

Artifact paths below are relative to the retained session evidence directory.
Hashes identify the actual bytes, not reconstructed outcomes.

| Artifact | SHA-256 |
| --- | --- |
| Frozen monitored protocol v4 | `e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71` |
| Frozen unified active protocol v7 | `23919c85adaf688db41a286cbe2bc90fb4687b3b3d3222d8372c276b40d03149` |
| `pv-unified-v7-controls-06/manifest.json` | `6eb2dfab0fb487a4138ccaf447c27759a37994ee3f37e0a046b4c1ef3dd13646` |
| `pv-unified-v7-06/seal.json` | `4b619c05f4fbc6074e0057e1e7f5d2f41042c819c9f6d1c1e18fdc278c4c36a8` |
| `campaign-unified-v7-controls-03/manifest.json` | `990d73719b5950cf8875b4ff1ca18d2cd7b7fde3c09d91774ff28eea05e21929` |
| `campaign-unified-v7-03/comparison-35/campaign-seal.json` | `2b32643d5df812da1c9f600b24cc32fa66381cba2d13fbc0af2c4dfca5cbc68a` |

The actual pv06 run completed all eight entries. Independent
`handoff-campaign-readiness` review (GPT-6 Astra), recorded in
`worker-handoff-campaign-readiness-review.json`, rehashed 2,438 sealed
artifacts and reparsed 883 raw monitoring records. It found 27 sampled
descriptor losses and one active-root file-path loss, with no hard errors
or alarms. Lost bytes and types remain unknown, not zero. Strict-boundary
root losses were zero. Its acceptance was source-bound to the historical
commit and exact campaign03 candidate; it does not transfer to this fix.

Campaign03 preserved 35 enumerated outcomes: ten passed, ordinal 11
(M1/A) was `inconclusive-unknown` with `IncompleteMonitoring`, and the
remaining 24 were not run. Its seal contains 129 artifacts. The primary
workload failure was:

```text
SqliteProtocolLimitMismatch:Candidate A requires the exact frozen revision 3 pipeline and query limits.
```

Factory validation throws before adapter or pipeline construction.
`RunAsync` catches the configuration exception, emits `failed`, and returns
2 without the normal announced-exit handshake. The secondary monitoring
alarm is `UnexpectedProcessIdentityLoss` at `worker-exit-quiescent`;
incomplete monitoring takes outcome-code precedence. The historical
coordinator exited 3. This is not evidence of an unhandled exception or
native crash. No historical outcome is rewritten.

## Causal correction and controlled proof

M1 legitimately reduces producer-owned buffer reservations from 2,097,152
to 262,144 bytes. The old synthetic path also passed that producer override
to the adapter factory. A's exact-default guard correctly rejected it;
B accepted the reduced options, but neither backend owns the producer
reservation. Enforcement belongs to `DurableCounterGlobalBudget` and
`DurableCounterPipeline`.

All four factory creation sites now share a request builder using unchanged
P1/default adapter limits. Synthetic pipeline and global-budget construction
retain M1's reduced ceiling. F5's two writers and the durable live arm use
the same builder. Reopen already used frozen defaults and is unchanged.
A's frozen guard remains strict.

The controlled regression retained in `m1-configuration-component/report.json`
first extracted the old request construction without changing its behavior.
The same final tests then produced 37 passes and three failures: M1/A factory
construction, M1/A reservation, and M1/B non-default factory configuration.
After the correction, all 40 configuration/reservation cases passed. All
626 selected durable component cases passed, with actual EventPipe explicitly
excluded and no skips. These are component results, not a replay of the
historical campaign or evidence that a fresh campaign will pass.

| Controlled component TRX | SHA-256 |
| --- | --- |
| `pre-fix.trx` (37 passed, 3 failed) | `fa9d0b53fe1c26f43c0a3a360f97bdae791a4b7ce5887c0a78705f77dc7714d6` |
| `post-fix.trx` (40 passed) | `63f877d89126a84ff1ccef6676b5752761b7a36648bc42cce08bcf49f5400bb3` |
| `durable-components.trx` (626 passed) | `e653b6c3561120e7e882e2d12c5bf383a585d34b89fb4a9d90752448feed54a0` |

The reservation tests use 65 offers of 4,096 bytes: M1 admits 64 held
reservations for either adapter, while the default pipeline admits all 65.
Configuration-only tests cover the 35 frozen plan entries without running
their workloads. This controlled proof explains the configuration mismatch;
it does not retrofit missing historical observations.

M1's held writer, 4,096 immediate offers, five-second feeder deadline and
262,144-byte ceiling are unchanged. Batch limits remain 64 records /
262,144 bytes / 100 ms; queries remain 128 keys, 100 rows and 1 MiB results.
Protocol hashes, fixture hashes, workload thresholds, observation policy,
quotas, deadlines, hard alarms and outcome precedence are unchanged.

Independent source review `m1-configuration-review` (Claude Sonnet 5)
accepted the three source/test/design changes with no findings. That review
is not approval of this report or fresh prepared artifacts. After publication,
fresh committed-source builds, metadata and component receipts must bind the
new HEAD before pv07 preparation. Preparation alone authorizes no execution:
new exact-artifact review and parent authorization are required. No winner,
merge, issue closure or production decision follows from these results.
