# DC5 corrected live startup: sealed incomplete request coverage

The first suite after the readiness/handoff correction reached a successful
live worker and clean monitoring, but its request population failed the frozen
coverage requirement. The suite is **sealed stopped-incomplete**, not all-eight
readiness. Sealing evidence does not make failed coverage admissible.

## Actual result

Implementation: `129c45b256a86bba5243708f6ed357598a3ed2d9` (#1034).
Suite: `pv-unified-v7-02`. Manifest SHA-256:
`9b594ddebe5c38e9b72aaf75b8919c0f90456410d7d48d7ceef31dd6c5ba2463`.
Invocation: 2026-09-24 13:28:29.213-13:29:51.608 UTC, exit 3.
Inspection found 822 sealed artifacts and primary `RequiredCoverageMissing`,
stage `coverage`, in ordinal 3 (`PV-LIVE-E`).

A and B O1 coverage passed their unchanged source gates. The live worker
and harness returned pass, with sampled-admissible monitoring, no hard
observation error or alarm, and all ten required live/entry boundary names
present. The target started, collection ran for 34.1028222 seconds, and the
shipping collector reported 33 source keys. Startup/termination completed
without the previous attempt's descriptor failure.

The actual measured request population was **601 scheduled, completed and
successful requests**, within an 880-request episode. Frozen coverage requires
600 measured scheduled requests. This mismatch was the failed predicate;
the historical 601 is not trimmed, rewritten, or retrospectively accepted.
Five later probes were not run.

## Scheduling correction

The frozen workload specifies 20 scheduled requests/second and the half-open
measurement window `[12s,42s)`, without explicitly choosing dispatch time over
the nominal schedule clock. The generator advanced a 50-ms nominal schedule
but classified cohort membership using jittered actual dispatch time. A
boundary delay could therefore move a warmup request into the measured cohort.

The correction consistently uses the nominal scheduled slot for both
scheduling and completion membership: slots 240 through 839 are the 600
measured requests; slots 0 through 879 form the 880-slot episode. Actual
elapsed time still controls dispatch and the 44-second cutoff. Early timer
wakes are rechecked. Unserved slots after the actual cutoff are not invented,
replayed or mislabeled as concurrency skips.

Worker and campaign checks require exact 600/880 schedule populations and
finite, ordered actual elapsed-time evidence. Counts of 599 or 601 remain
invalid. Schedule completeness is separate from request success: concurrency
skips remain in the denominator, and the existing success-rate screening
remains independently mandatory.

The two-in-flight and 1,000-task limits, endpoint, rate, windows, source floor
and frozen protocol files remain unchanged. No observed metric is coerced to
a passing value. Independent review accepted the nominal-slot clarification
and implementation without a blocking finding.

## Validation and preservation

Validation passed 564 cases: 534 existing component cases and 30 new
deterministic cases. One existing EventPipe test was explicitly excluded;
this is not represented as an unfiltered full-suite pass. The solution built
using the pinned SDK 10.0.201.

Local evidence is under session
`f10a11bb-805c-4e83-8621-74c5363b7de5/files/request-schedule-component/`,
including `evidence-report.json`, proof, logs/TRX and preservation snapshot.
The original source remains clean at the executed commit; 3,097 file hashes
and 83 manifest/binary bindings were verified unchanged.

No real prevalidation repeat or scored campaign produced this correction.
A fresh committed build, reviewed artifact bindings and separate authorization
are required for the next prospective attempt. No all-eight readiness,
backend comparison, recommendation or product adoption is claimed.
