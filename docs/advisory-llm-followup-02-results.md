# Advisory semantic follow-up 02: four primary comparisons

After two successful synthetic real-CLI calls, the separately frozen second
follow-up completed **four of five primary comparisons and both preselected
order controls**. It used seven new corpus calls: one A and six B. No transport
failure occurred. The remaining case failed the frozen semantic-extraction
contract, not the transport.

These are automated development judgments against retained evidence, not
diagnostic ground truth, human review, an accuracy estimate, or a CI gate.
The [original three-of-eight result](advisory-llm-assessment-results.md) and
[first failed follow-up](advisory-llm-followup-results.md) remain separate,
unchanged experiments.

## Execution and coverage

The two-call synthetic smoke sent only a request for `{"probe":"ok"}` through
the existing `CopilotCliAdvisoryTransport`. Both `claude-sonnet-5` and
`gpt-5.6-sol` returned that exact object on CLI 1.0.87. This exercised the actual
transport without corpus data; it did not establish semantic quality.

The subsequent corpus plan retained the first follow-up's rubric, measurement
glossary, templates, source bindings, model settings, order controls and limits.
Only the plan identifier/fingerprint changed; execution used the corrected
transport and global-abort implementation. The budget remained eight new calls,
two per case, 120 seconds per call, 960 seconds inference plus 300 seconds outer
overhead. Byte, artifact and semantic bounds were not raised.

| Slot | Phase A | Primary B | Reversed-order control |
| --- | --- | --- | --- |
| case-01 | Exact retained text; legacy ID schema noncompliant | Completed | Completed |
| case-02 | Exact retained text; legacy ID schema noncompliant | Completed | Not planned |
| case-03 | Previously recovered, normalized response | Completed | Completed |
| case-04 | Previously recovered, normalized response | Completed | Not planned |
| case-05 | Fresh response returned and retained; required `alternatives` field missing | Skipped | Not planned |

The fresh case-05 JSON contains `observations`, `hypotheses`, `uncertainty`,
`abstained`, and `nextDiagnosticQuestion`, but no `alternatives` property.
The recorded blocker is `Semantic field '/alternatives' is not an array.`
Missing was not silently converted to an empty array. No response was repaired
or regenerated, and the unused eighth call was not spent on a retry.

All four retained A responses were reused without another model invocation.
Cases 01-02 remain noncompliant with their original ID schema; safe text
extraction did not rename source IDs or turn the historical calls into successes.
Case 05 has a successful transport status but unsuccessful extraction. The run
therefore records `technicallyComplete=false` and exits nonzero after writing
artifacts, despite preserving six valid B judgments.

## What the comparator found

The following summarizes the judge's reasoning, not independently established
causes or proof that an original claim is false.

| Cases | Evidence-grounding concern |
| --- | --- |
| 01-02 | Queue levels, low observed CPU and a last-interval zero completion increment do not establish persistent starvation, blocked workers, request timeouts, or startup/idle behavior. Both the original and reanalysis candidates introduced mechanisms not distinguished by the retained counters. |
| 01 | Numerical proximity between thread and timer counts does not establish ThreadPool growth or timers causing worker creation. |
| 03 | Low runtime counters do not demonstrate service responsiveness or absence of latency. The evidence lacks request-level observations. A last-interval contention increment is not a six-second total. |
| 04 | Raw allocation and GC increments cannot be treated as per-second rates or capture-wide totals without the omitted interval metadata. The projection does not establish that GC/fragmentation caused tail-latency spikes. |

Across the four primary comparisons, the judge labeled the **11 original
claims** as eight partially supported and three unsupported; seven were marked
overconfident. It labeled the **eight retained reanalysis hypotheses** partially
supported; two were marked overconfident. These are descriptive counts only:
original claims include observations and absence-of-evidence statements, whereas
the graded reanalysis items are hypotheses. Different claim composition prevents
treating these fractions as a model ranking or accuracy comparison.

Useful next steps generally sought persistence, time-aligned measurements,
request symptoms, baselines or discriminating thread/GC evidence. Some
recommendations still assumed unavailable workload controls or unspecified
collection capabilities. A requested diagnostic operation must be checked
against the actual runtime/backend and permissions; changing a client is not
necessarily changing the collection mechanism.

## Order-control results

The same evidence, rubric and candidate content were evaluated with candidate
order reversed. Results were mapped back to source claim identities.

| Control | Claims compared | Support-label changes | Certainty-label changes | Distinct claims changed |
| --- | --- | --- | --- | --- |
| case-01 | 4 | 2 | 1 | 3 |
| case-03 | 5 | 0 | 1 | 1 |

For case-01, the original timeout/mechanism claim and the reanalysis timer-growth
claim changed from partially supported to unsupported. The original
absence-of-evidence claim changed from appropriate certainty to overconfident.
For case-03, the reanalysis contention claim changed from overconfident to
appropriate certainty, while support stayed partially supported.

Candidate-level uncertainty and next-step labels did not change in either
control. Four of nine claim rows nevertheless changed in at least one axis.
One pair per case cannot separate positional sensitivity from stochastic
variation; these observations do not establish a causal order effect. Neither
ordering is selected as the correct verdict, and disagreements are not voted
away.

## Interpretation and action boundaries

**Representation before collector changes.** The repository's counter model
retains interval/rate metadata, while the blinded gateway's selected projection
omits it along with first samples and a time series. The observed ambiguity
therefore motivates improving that evidence contract, not asserting that the
underlying EventPipe capture is broken. A future projection should expose the
available measurement semantics or mark them explicitly unavailable; it must
not fabricate timestamps, trends or rates.

**Claims should match the evidence retained.** Separate observed values from
tentative mechanisms and from claims about user-visible symptoms. A request
latency or responsiveness conclusion needs corresponding evidence. Missing
evidence can justify uncertainty, but not automatically health or a root cause.
The original diagnosis may have had other context; these judgments are
conditional on the supplied retained packet, not a reconstruction of every
fact available in the original investigation.

**The judge also needs scrutiny.** Its treatment of relative terms can be too
strict or inconsistent: a percentage has a reference scale, even without a
historical workload baseline. Distinguish numerical magnitude from workload
abnormality and causal impact. Compound claims containing correct numbers and
unsupported causal extensions should not be summarized as wholly false.
The order controls further show that individual categorical labels are not
stable enough here to be treated as authoritative truth.

**Do not disguise the missing case.** Case 05 now has a retained raw response,
unlike the prior transport failures. A separately declared file-only review can
determine whether its existing claim/uncertainty fields can be assessed without
the unused missing field. That is not permission to invent `alternatives: []`,
overwrite this run, regenerate A, or silently relax the frozen contract. No
such continuation was executed in this iteration.

The next changes should target evidence representation and capability-aware
recommendations before modifying diagnostic thresholds based on these judgments.
No production collector, target deployment or diagnostic rule changed in this
iteration. PR #998 remains draft while the missing case and these limitations
remain explicit.

## Provenance

| Item | Value |
| --- | --- |
| Code | `2201cdd5e89e9d18b6daf853eb504b25701ee344` |
| Assessment assembly SHA-256 | `fc68a5560f7a381a1338a993c778a7a1592b4a3cb163f1da6f97dd0991b9302d` |
| CLI | `GitHub Copilot CLI 1.0.87` |
| Smoke summary SHA-256 | `4b8536c117bd758756e13b3be4f7f13e475cde7769c9098dd4a0c1441ba3ce6e` |
| Plan ID | `advisory-semantic-followup-development-02` |
| Plan fingerprint | `cccd51fd6eb69b0eb4332492ef4a0ae7074068807d91cf8967655e249225f5bc` |
| Plan file SHA-256 | `d7c05cfd1edc98bec6aacb8a948d3395d7ac930329f084cba2e6cdaaed220c22` |
| Run summary SHA-256 | `e35a33c4219d131f0e3d34ddee3f9c47f7c8ae369b8c84a5b7b16dd1693dede7` |
| Case-05 raw A SHA-256 | `3f3dfa4d38accff0785401a10b45ed07d60f18bf1e681fed4e29ba0d69165a5f` |
| Started UTC | `2026-09-21T20:18:21.3154235Z` |
| Completed UTC | `2026-09-21T20:22:06.3176137Z` |
| Actual corpus calls | 7: 1 A + 6 B |

A used `claude-sonnet-5` and B used `gpt-5.6-sol`. Immutable provider model builds,
actual token usage and dollar cost remain unknown. Source packet/continuation
bindings, rubric and glossary hashes are preserved in the canonical plan. No
held-out input was opened; frozen human v1 remains unchanged and unreviewed.
