# DC5 unified sampling: O1 coverage and live-startup failure

The first revision-7 suite completed A and B O1 coverage, then stopped in
`PV-LIVE-E` during target startup. It is partial/unsealed, not all-eight
readiness, a scored comparison, or a backend decision.

## Actual evidence

Implementation: `2725f3318267729182c7fef6450f0ebe81b94497` (#1033).
Suite: `pv-unified-v7-01`. Manifest SHA-256:
`ed48703584e1de03083293ea0f8dd781681d6eeb3586eb9291a5712e47245595`.
Run invocation: 2026-09-24 12:46:13.752-12:47:01.410 UTC, exit 3.

Both O1 entries recorded `coverage-observed` and sampled admissibility,
without hard errors or alarms. A offered 49,982 and committed 49,981;
B offered and committed 49,990. Both satisfy the unchanged 95% achieved-offer
floor against 50,000 scheduled offers. Their sampling losses remain explicit;
neither entry represents complete descriptor observation or suite readiness.

The first failure is raw coordinator summary line 52 in `PV-LIVE-E`:

```text
boundary: live-target-startup
failure: DescriptorObservationUnavailable
z: [2,1,13,145]
```

That context means target role, first native pin, errno 13 (`EACCES`), FD 145.
It is not an ENOENT sampling loss. Target observations total 8,824 candidates,
8,745 classified observations and 79 failed/unclassified residual candidates.
Three harness pin losses are separate and do not excuse target EACCES.

The target was registered before readiness. No target-termination handshake
appears in the retained control log. Target output, HTTP responses, startup
exception, exit status and exact kill timing were not retained. Historical
causation cannot therefore be established from this context alone.
Owned cleanup was quiescent with no errors, but cannot erase the earlier
failure. The five later probes were not run.

## Controlled defects and correction

Two loopback requests against the pinned sample binary returned 404 for `/`
and 200 for `/weatherforecast`. The worker incorrectly used `/` for readiness,
whose helper requires a successful status. This is a verified startup defect,
not grounds for increasing the existing ten-second startup deadline.

Startup failure also exposed a lifecycle gap: registration precedes readiness,
but the old failure cleanup could kill the child before execution entered the
normal termination-announcement try/finally. The correction installs the
cleanup hook before startup and uses the existing authority kill operation
for both startup failure and normal disposal. Under the sweep gate, it
validates the exact live owner, marks intentional termination, signals,
confirms exit and removes the registry entry before acknowledgment. The
later termination event accepts only that confirmed handoff.

Handoff, exit confirmation and output drains share the existing five-second
disposal deadline. Failure still signals the owned child and remains explicit.
Unexpected death, identity mismatch and EACCES remain hard failures; no errno
classification or policy constant changed.

A separate tiny owned native child demonstrated first-pin EACCES while still
alive with dumpability disabled, followed by successful access when restored.
This is not attribution of such a change to the historical sample. It proves
why EACCES alone cannot be retroactively classified as intentional death.
The historical timing and absent handshake are consistent with the startup
cleanup defect, but do not prove the causal link.

## Validation and retention

Final component validation passed 535 DurableCounterSpike cases and 23
existing cancellation/readiness cases with SDK 10.0.201. Ten new cases cover
actual startup/normal cleanup entry points, live-owner handoff, in-flight
sweep exclusion, abrupt-death rejection and shared deadlines. Independent
review found no significant issue.

Local artifacts reside under session
`f10a11bb-805c-4e83-8621-74c5363b7de5/files/newlive-descriptor-component/`,
including `report.md`, `actual-failure.json`, controlled HTTP/native proofs,
source/binary-bound validation receipts and the quiescent preservation copy.
The original clean source, 78 inventoried binary entries, 818 suite files
and 107 control files were verified unchanged. The copy is not a seal.

No real suite was rerun to produce this component proof. The corrected source
requires new committed-build identities, reviewed inputs and separate parent
authorization before a prospective attempt. The unchanged revision-7 contract,
workloads, source floor, resource caps and exact final inventories remain
mandatory.
