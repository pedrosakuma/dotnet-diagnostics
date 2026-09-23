# DC5 real-worker prevalidation: first attempt stopped during fixture setup

## Outcome

The single authorized suite `pv-765e56c-single-20260923` was invoked on
2026-09-23 at 19:21:28.621 UTC and exited with code 3 at 19:21:30.271 UTC.
Admission had succeeded, but **PV-O1-A became incomplete during synthetic
history-fixture construction, before any workload harness was launched**.
The remaining seven entries were not run. No retry or replacement suite was
started.

The evidence is **partial and unsealed**, not readiness evidence. No SQLite
or append-first workload measurement, backend comparison, campaign admission,
or production approval follows from this attempt.

| Entry | Outcome | Attempts |
| --- | --- | --- |
| PV-O1-A | Incomplete during fixture preparation; no workload harness launched | 1 |
| PV-O1-B | Not run: suite stopped | 0 |
| PV-LIVE-E | Not run: suite stopped | 0 |
| PV-LIVE-A | Not run: suite stopped | 0 |
| PV-LIVE-B | Not run: suite stopped | 0 |
| PV-F3-A | Not run: suite stopped | 0 |
| PV-F3-B | Not run: suite stopped | 0 |
| PV-GEOMETRY | Not run: suite stopped | 0 |

## What the preserved evidence establishes

- Dedicated admission returned exit 0 for the resolved manifest, eight-entry
  order and 2,506-identity construction bound. Its output explicitly states
  `campaignAdmissionGranted: false`.
- The suite's coordinator-admission observations were complete.
- Exactly one attempt receipt exists. The first context contains 63 synthetic
  history slots with four 512-byte files each, or 129,024 fixture bytes.
  These are inventory fixtures, not capture packages.
- During fixture creation, periodic observations 2, 3 and 4 have
  `c: false` and error counts `er: 1`, `2`, and `1`. Their alarm fields are
  null. The later `fixtures-complete` and `owned-cleanup-quiescent`
  observations are individually complete, but do not erase the monitor's
  earlier incomplete state.
- The eight entry observations report at most 266 current-context identities,
  179,212 observed suite bytes and a maximum completion gap of 174.9775 ms.
  These are observations, not instantaneous upper bounds or successful
  full-entry coverage. No resource-threshold alarm was recorded.
- No harness request, ownership journal, source-worker descriptor or worker
  result was created. The attempt stopped before the real workload.
- `cleanup.json` records `quiescent: true`, no unconfirmed identities and no
  errors. The coordinator had exited before the preservation snapshot.
- The aggregate failure is
  `Cleanup-PrevalidationFinalMonitoringIncomplete`. Here the prefix does
  **not** establish failed process termination: the cleanup-stage check
  rejects the monitor's sticky incomplete state.
- `prevalidation-inspect` returned exit 0 with
  `partial-unsealed-not-readiness-evidence`, eight outcomes, zero sealed
  artifacts and `campaignAdmissionGranted: false`. A successful inspection
  means the partial classification was recognized, not that the experiment
  passed.

## Root-cause limit

The compact observation stream preserves error counts, but not the underlying
error codes for these incomplete sweeps. The aggregate cleanup-stage failure
also does not retain the original fixture-stage error. Consequently, this
attempt establishes an observation failure during fixture construction, **not
its exact cause**.

Descriptor churn during concurrent fixture creation is a hypothesis, not a
confirmed diagnosis. No repeat was used to investigate it. A correction must
first preserve bounded causal error evidence and exercise the relevant
fixture/observer lifecycle with component tests. It must not silently ignore
incomplete observations, raise limits, or classify this attempt as successful.
Any further real suite requires a prospective reviewed change and fresh
explicit authorization; the consumed suite cannot be reset.

## Provenance and preservation

Implementation: `765e56c905e975892740de52daf5bdab91218608`, draft #1026,
stacked on the adopted addendum #1025. The historical blocked-readiness report
is unchanged.

Independent implementation, correction and artifact reviews were recorded
with their actual scopes. The coordinator then recorded the acceptance and
issued the separate manifest-bound authorization under the maintainer's
conditional approval. This does not represent a reviewer-issued signature.
The prerequisite component evidence retains
`descriptorOnlyCampaignFeasibilityEstablished: false`.

Some prerequisite negative-control booleans are generator observations, not
independently reconstructible from empty raw monitor logs. Their implementation
and admission checks were reviewed; empty logs are not represented as proof of
those controls.

Raw artifacts remain private. The separate preservation inventory contains
268 files. Its hash is SHA-256 over UTF-8 records sorted by ordinal relative
path, each formatted as `path<TAB>decimal byte length<TAB>file SHA-256<LF>`.
This inventory is a post-exit preservation snapshot, **not a protocol seal**.
No private absolute paths or binary payloads are published here.

| Identity | SHA-256 |
| --- | --- |
| Resolved manifest | `df8e628f5d4a2e5da681fa60d483a1d81122c3c1d8fbb93369342e1d3a670ef5` |
| Binary inventory serialization | `521c5be237584431e33a6819d3f483dfe3f6c6c8b951ee5a21031c742e473273` |
| Prerequisite component evidence | `f7329ba1578a7ed974c7fac43cab2ecd51ff5f983c1289fa4560c63a01ec28e4` |
| Partial report | `0546a79e8a57c3dd7415a2466c94d444fe532f461b135ff8e5f653e142cb940c` |
| Coordinator admission observations | `2e4635fded7ae4c97972090fee711b9bd14f88c5c4467b2c055e8ad0669239dc` |
| First-entry observations | `efd6f4f5f4171beb95efe775c4b02e273b523aa8b95b648150ba3e4a81e62753` |
| Cleanup record | `7e32da7f19627757a2d22e5718f422d77ff1f8a6fc915050b2f54812a96914dd` |
| Partial preservation inventory | `8ceaa801c61d7700e65d4c9e73c797b37be90c6cccc6d332813df0b518a1c650` |
