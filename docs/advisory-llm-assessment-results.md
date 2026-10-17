# Development LLM assessment: 2026-09-21

Refs #997, #921, and #919. This is a **partial automated advisory assessment**,
not human calibration, an accuracy estimate, a truth set, or a CI quality gate.
The method is described in [advisory-llm-assessment.md](advisory-llm-assessment.md).

## Coverage and execution accounting

Three of eight planned comparisons completed. **All three are authored edited
replays; none of the five historical live-model-origin packets completed its
comparison.** The successful subset is therefore not representative live
validation.

| Case | Historical packet origin | Final assessment status |
| --- | --- | --- |
| case-01 | Live model, sync-01 | Unassessed: the retained A response contains `observationId` inside a hypothesis object and fails the declared schema. |
| case-02 | Live model, sync-02 | Unassessed: the same hypothesis-schema violation. |
| case-03 | Live model, healthy-01 | A recovered without another inference; B hit the CLI output-byte limit. |
| case-04 | Live model, gc-01 | A recovered without another inference; B hit the CLI output-byte limit. |
| case-05 | Live model, lock-01 | A hit the CLI output-byte limit; no bounded raw response was retained and B was not called. |
| case-06 | Edited replay, ThreadPool reason unknown | Comparison completed using the retained A response. |
| case-07 | Edited replay, capture loss | Comparison completed; A abstained and produced no root-cause hypothesis. |
| case-08 | Edited replay, combined/contradictory measurements | Both phases completed in the original execution and were reused without calls. |

An initial execution failed in the isolation preflight because CLI 1.0.86
returned an empty plugin array while the legacy parser expected an object.
Those eight preflight failures remain preserved separately.

After that transport correction, there were eight A and one B model CLI
invocations. Six A responses used a Markdown JSON fence; one exceeded the byte
limit; one completed both phases. The continuation removed only the exact outer
fence from retained responses. Four of those six passed the unchanged inner
schema/citation checks; two did not. It made four new B calls, of which two
completed and two hit the transport output limit.

The total was **13 model CLI invocations: eight A and five B**. No A response
was regenerated, no completed B response was rerun, no hypothesis or citation
was repaired, and no byte cap was raised. The transport-limit messages do not
establish whether final answer content or other CLI output consumed the cap.
Token usage, monetary cost, and immutable provider model versions are unknown.

## What the completed comparisons said

Phase A used `claude-sonnet-5` without the earlier interpretation. Phase B used
`gpt-5.6-sol` in a fresh context, with opaque, counterbalanced candidates.
The following are the comparator's judgments, not independently established
truth:

| Replay | Main result |
| --- | --- |
| ThreadPool reason unknown | Low CPU plus queue/worker levels permit a waiting/blocking hypothesis, but do not establish a cause. The comparator challenged calling counts "large" or "elevated" without a baseline, and rejected the new reanalysis's startup-burst/ramp-up conjecture as unsupported. |
| Capture loss | An empty retained counter array supports abstaining, not a diagnosis of health or a root cause. Both interpretations recognized the data limitation; the new A response returned zero hypotheses. |
| Combined measurements | Queue length and monitor contention do not establish simultaneity or a causal relationship when temporal correlation and wait reasons are unavailable. Both interpretations remained appropriately cautious about a primary cause. |

Across the six earlier claims in these three replay packets, the comparator
marked four supported and two partially supported. It marked one claim
overconfident. Across four new hypotheses, it marked three partially supported
and one unsupported, with cautious wording judged appropriate. The loss replay
contributed no new hypothesis.

These counts must **not** be used to rank the candidates: the earlier claims
include observations and evidence limitations, whereas the new claims being
graded are hypotheses. The five missing live-origin comparisons also prevent
any general success-rate or accuracy conclusion.

## Limits of the judgments themselves

- The comparator treated relative queue/worker labels more strictly in one
  replay than another despite missing baselines in both. Its consistency has
  not been established.
- It could not verify the earlier phrase "edited evidence" from the blinded
  evidence. The controller knows that this packet was edited, so that criticism
  is a visibility limitation, not proof that the earlier statement was false.
- A suggested next step mentioned disabling harness truncation. That capability
  was not established or exercised; this report does not recommend removing
  resource bounds.
- Phase B judged certainty from wording and shared epistemic posture, and
  uncertainty/abstention from prose. Noncomparable structured confidence and
  abstention flags were withheld symmetrically; full A declarations remain
  sealed.
- Retained evidence was selected earlier; edited examples are not a substitute
  for live coverage. Different models are not statistically independent.

## Provenance and remaining work

The frozen human v1 rubric/protocol and original eight packets were not changed.
No human review was created, no sealed held-out material was opened, and no
new diagnostic target capture occurred.

- Corrected execution revision:
  `b73e2e1b52ba94baeeab168f9dfdc8cf65d7db19`.
- Continuation revision:
  `5779016610bb4e0f1eba13f865560c90e161426b`.
- Original protocol fingerprint:
  `fdc40ae4fdb7eaf90de36bc60bfbbf6aecb6e0c0dd3185dc0abdebe2e39f5943`.
- Continuation plan fingerprint:
  `17902259284a618743bd7436e8a5d673bd82b396236e3feb2056d0b7369e4507`.
- Preserved source summary SHA-256:
  `b6ba260e7ae286bfb3c26c2f885d5f13e75b5c90f9cfd8265e0afcb9db5a34c8`.
- Continuation summary SHA-256:
  `ff7391cd91a8ba936e7e8e7909c373801b35d7b11edd624d3ce13ff43014fe69`.

Raw artifacts and the controller's immutable input/response hashes are retained
outside the repository. The explicit continuation command returned nonzero
after preserving its partial summary, as designed. This result leaves #997's
live-origin assessment coverage incomplete. Further transport-budget or
schema-adherence work requires a separately declared follow-up, not rewriting
this pilot's failures or repeating it until all cases pass.
