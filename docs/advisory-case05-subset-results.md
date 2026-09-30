# Case-05 comparison-subset result

The separately declared `advisory-case05-comparison-subset-01` completed its
single B comparison using the retained A response. It made **one B call and
zero A calls**, with no retry, fallback, response repair or new capture.

This fills the remaining live-origin comparison gap across the development
iterations. It does not rewrite any earlier experiment: follow-up 02 remains
four of five primary comparisons, and its case-05 response still fails the
original extraction contract because `alternatives` is absent.

## Selection and execution

The [explicit comparison-field policy](advisory-comparison-subset.md) selects
only existing hypothesis text/citations, uncertainty and next question.
The three retained hypotheses were selected unchanged. Unconsumed fields were
recorded as `observations=Array`, `alternatives=Missing`, `abstained=False`;
none of these markers or values entered the comparison prompt.

The [predeclaration](https://github.com/pedrosakuma/dotnet-diagnostics/issues/997#issuecomment-5767509146)
bound the source, selection, prompt, code, assembly, controller and one-call
budget before execution. B used `gpt-5.6-sol`, CLI 1.0.87, a fresh isolated
context, original-first order, and the unchanged follow-up rubric/glossary.
No prospective counter-evidence or rubric changes entered this comparison.
There was no additional order control for this case.

File-only preparation had a PowerShell manifest-serialization failure before
any model invocation. The partial selection was retained. A later unexecuted
manifest was archived after independent review corrected dispatch/usage
accounting; selection and prompt content did not change. These preparation
corrections neither consumed inference calls nor regenerated A.

## Comparator judgments

These labels describe one automated judge's interpretation of retained
evidence, not diagnostic ground truth or model accuracy.

| Candidate | Claim support labels | Certainty labels | Uncertainty / next step |
| --- | --- | --- | --- |
| Original, three claims | Three partially supported | Three overconfident | Both partially useful |
| Retained reanalysis, three hypotheses | Two partially supported, one unsupported | Three appropriate | Both useful |

The judge accepted the observed blocking pattern: a recorded lock owner whose
stack shows sleeping, 19 listed waiters, and corresponding blocked stacks.
It distinguished those observations from unsupported throughput collapse,
absence of CPU saturation, sleep duration, persistent contention and workload
causation. It also noted that a claimed address was absent from the supplied
projection and that recorded worker flags did not support identifying threads
as ThreadPool workers.

The reanalysis was not treated as authoritative. Its proposed interpretation
of owner `-1` and waiter count `1000` as unresolved/saturated collector values
was labeled unsupported because those semantics were not supplied. Tentative
wording can be appropriately cautious without making a hypothesis supported.
Repeated, aligned snapshots were judged useful for investigating persistence,
not sufficient by themselves to establish workload causation.

The judge's own reasoning also needs qualification. Recorded ownership and a
sleeping stack are positive evidence for a sleeping owner, even though a
roughly one-second walk is not an atomic observation and does not establish
source-level scope, duration or causal impact. Rejecting those stronger
conclusions must not erase the observed blocking pattern. No independent
ground-truth adjudication or second ordering was performed here.

## Coverage and action boundaries

Across distinct development experiments, all eight original packets now have
at least one primary comparison: five live-origin cases and three authored
edited replays. This is not one eight-of-eight frozen run and does not resolve
historical schema/transport failures. Cumulative corpus invocations are now
**26: 10 A + 16 B**, plus the two separate synthetic smoke calls.

The practical direction remains prospective evidence semantics and
capability-aware recommendations, not changing production thresholds based
on categorical judge labels. No collector, target deployment or diagnostic
rule changed for this continuation. Human v1 remains untouched and unreviewed;
no held-out input was opened. PR #998 remains draft.

## Provenance

| Identity | Value |
| --- | --- |
| Execution code | `05ef6d607304b258e6a39e4b7398a679b8d28bcc` |
| Assembly SHA-256 | `804be30a6f7747d58ec660f5c255d71d21eccab4297b6cac44ab4aa58f329e5a` |
| Controller SHA-256 | `c47c92163e4d8c3d4673561906f506a774a3cecc95eca8ba4e4a3415dcfc5927` |
| Manifest SHA-256 | `2b98a089ae37b5e2577b4fab1873b2b33b3fe4e7ef3a781bd7199a0c34effac4` |
| Selection SHA-256 | `fb4d72bd4169e5798bd43441bbec9744593f4cce907ac6b5dfdbf5cd76558e87` |
| B prompt SHA-256 | `5b0d540ac44ab79ad30dde542b6a381f3c7830e7896bf663d57e1c92f594cd19` |
| Retained A SHA-256 | `3f3dfa4d38accff0785401a10b45ed07d60f18bf1e681fed4e29ba0d69165a5f` |
| Attempt ledger SHA-256 | `137ee35ee3183cebe85abd30f99cd99c231c103ef6cbd28e0c3b86ba034259ce` |
| Raw B SHA-256 | `230dce9dc6b7731dfe075252bce3cc45cba7e80358954bba186d3799448e9452` |
| Result SHA-256 | `7b7afdbfe77158a07b4e9910a65ea100ef595b023478c5b5c09b794df9b20dea` |
| Started UTC | `2026-09-21T21:05:53.0017474Z` |
| Completed UTC | `2026-09-21T21:06:25.8458894Z` |

The manifest binds the unchanged source protocol, follow-up plan, summary and
case artifacts. The result records one attempted transport call and one
confirmed returned invocation. Actual token usage, dollar cost and immutable
provider model build remain unknown, not zero.
