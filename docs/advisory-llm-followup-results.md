# Advisory semantic follow-up: transport-blocked development run

The frozen follow-up ran on 2026-09-21 and completed **zero of five primary
comparisons and zero of two order controls**. Five model CLI invocations
occurred: one fresh Phase A and four Phase B attempts. Every invocation failed
on the same unrecognized CLI event, `session.usage_checkpoint`.

This is a transport compatibility failure, not a semantic assessment result.
It supplies no new verdict about the original diagnoses, target captures, or
diagnostic rules. The historical
[three-of-eight result](advisory-llm-assessment-results.md) remains unchanged:
all three completed comparisons were edited replays; none were live-origin.

## Frozen experiment

The [follow-up protocol](advisory-llm-followup.md) separated technical schema
compliance from safely bound semantic text extraction. It reused the exact
retained A responses for cases 01-04, allowed one fresh evidence-only A for
case 05, and preselected reversed-order controls for cases 01 and 03.

The plan was published before any new corpus calls in
[issue #997](https://github.com/pedrosakuma/dotnet-diagnostics/issues/997#issuecomment-5764531426).
Its limits were eight new calls total, two per case, 120 seconds per call,
960 seconds inference plus 300 seconds outer overhead. A used
`claude-sonnet-5`; B used `gpt-5.6-sol`; both bound GitHub Copilot CLI 1.0.87.
Immutable provider model versions, actual token usage, and dollar cost remain
unknown.

The assistant payload limit remained 65,536 bytes. Separate finite transport
caps covered the assistant envelope, cumulative framing including delimiters,
insertion-time line, and stderr. No cap was changed during this run.

## Coverage and preserved statuses

| Slot | Retained A or new A | Primary B | Order control |
| --- | --- | --- | --- |
| case-01 | Exact retained text usable; legacy ID schema noncompliant | Unsupported CLI event | Skipped after primary failure |
| case-02 | Exact retained text usable; legacy ID schema noncompliant | Unsupported CLI event | Not planned |
| case-03 | Retained normalized response schema-compliant | Unsupported CLI event | Skipped after primary failure |
| case-04 | Retained normalized response schema-compliant | Unsupported CLI event | Not planned |
| case-05 | One fresh A attempted; transport failed, no returned payload | Skipped after A failure | Not planned |

The four retained views were sealed before comparison calls. Cases 01-02
continued to carry their original wrong-ID schema failure; no hypothesis text,
source IDs, or citations were repaired. Cases 03-04 retained the historical
`invalidResponse` source status even though their already-recovered normalized
payloads were schema-compliant. These are separate records, not a retrospective
conversion of failed source calls into successful ones.

The attempted sequence was fresh A05 followed by B01, B02, B03, and B04. The
dependent B05 and both order controls were skipped. No new Phase-B judgments
or fresh A payload were returned to the assessment harness. No assertion is
made about provider-side generation that the harness did not retain.

The summary records `technicallyComplete=false` and
`abortedForGlobalPrerequisite=false`. The latter exposes a second harness
defect: this shared CLI protocol incompatibility was classified as a local
failure, allowing the four subsequent B attempts instead of stopping after the
first failure. The explicit workflow exited nonzero after preserving artifacts,
as designed. It was not rerun.

## Transport finding and limits

The earlier transport correction established that CLI JSON stdout contains
session events, not just the assistant answer. However, its source-derived
event inventory was incomplete: the actual 1.0.87 run emitted
`session.usage_checkpoint`, which the fail-closed reader rejected.

Thus synthetic event fixtures and static review were insufficient to establish
compatibility with the full real event stream. This run does not establish
successful end-to-end response handling merely because it did not report
another byte-cap failure.

The post-run correction recognizes this event as bounded framing before or
after an answer. CLI 1.0.87's bundled schema and session-limit option-update
producer identify its payload as durable aggregate accounting (`totalNanoAiu`),
not a tool request or diagnostic action. The earlier inventory missed this
option-update path. All byte bounds and tool-activity checks remain in force.
Unknown CLI event types now abort the remaining experiment as a global
transport prerequisite failure. Malformed model response JSON remains a
case-local failure, rather than being conflated with CLI protocol drift.

These corrections were not used to rerun the frozen corpus. They do not by
themselves establish full real-CLI compatibility or semantic coverage.

The failed run remains immutable. There is no automatic retry, fallback model,
additional A generation, or use of the unspent three-call allowance as a new
experiment. Any later assessment needs its own explicitly declared continuation
scope; the existing plan is exhausted operationally, not successfully completed.

## Provenance

| Item | Value |
| --- | --- |
| Plan ID | `advisory-semantic-followup-development-01` |
| Code | `513991a6a729c1e06091c2d9072c09f22a4ad11f` |
| Assessment assembly SHA-256 | `203fc48f594b75920bfb76f0736e0b3fc5071bf5450d600393c324ac1c566a30` |
| Plan fingerprint | `40793d785a6c5211d7744adbc2780beb569c57acbacf8a0edb2edb5d36cb7ebf` |
| Plan file SHA-256 | `a816abfcf407459e42390130ac10c096b838f4ab5ee1d58612a1a72590c11256` |
| Follow-up summary SHA-256 | `19fbf2df5cbc4778b0e3746adf4bfd2ec3ba70d69a63def742e23ee42f06cc18` |
| Source continuation summary SHA-256 | `ff7391cd91a8ba936e7e8e7909c373801b35d7b11edd624d3ce13ff43014fe69` |
| Rubric SHA-256 | `32897554d4696846b376605c5177d44a3f85472bb2fb47f8591d38fd384fcf6b` |
| Measurement glossary SHA-256 | `d8609d64d055ac350a448763ac15a748a8f48c3824b9b5a6a273eecbb5cb403e` |
| A template fingerprint | `682ed93dd7c5f1f43b33a95eefa6390bd23aa7690904ecbe429da7ab2316984e` |
| B template fingerprint | `d5d2875dc376df29d2a2539d9aed0267e7d02cc036fcebc41b4de80b049b9498` |
| Started UTC | `2026-09-21T17:17:16.8606265Z` |
| Completed UTC | `2026-09-21T17:20:01.0958343Z` |

The five source wrapper files and source continuation summary retained their
declared hashes after execution. Earlier pilot/continuation failures, frozen
human v1, and the three completed replay assessments were not changed. No
held-out material was opened and no diagnostic target was captured.
