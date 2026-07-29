# Agent response mapper prototype

This document describes the advisory prototype added for issue [#736](https://github.com/pedrosakuma/dotnet-diagnostics/issues/736). It builds on the scenario-evaluation spike in [`docs/diagnostic-scenario-evaluation-spike.md`](./diagnostic-scenario-evaluation-spike.md) and stays inside the existing test/evaluation assembly.

## Scope

`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AgentResponseInterpreter.cs` maps a free-text diagnostic response for one of the existing scenarios (`culture-lookup`, `sync-over-async`, `lock-storm`, `gc-storm`) into:

- a reused `StructuredInterpretation` contract;
- concrete evidence citations back to the committed JSON evidence fixture;
- explicit uncertainty classification (`Hedged`, `Assertive`, `Mixed`, or `NoneDetected`).

The prototype is **advisory only**. No existing replay/live test consumes its output, and no CI gate depends on the mapper.

## Input contract

The mapper accepts either:

- `Interpret(string scenarioId, string freeTextResponse)`, which resolves the default committed fixture for that scenario (currently preferring `*.windows.evidence.json` when present); or
- `Interpret(AgentResponseMappingRequest request)`, where `evidenceFixturePath` can pin a specific fixture.

`AgentResponseMappingRequest` serializes as:

```json
{
  "scenarioId": "sync-over-async",
  "freeTextResponse": "This is sync-over-async: CPU remains low while the ThreadPool queue keeps growing.",
  "evidenceFixturePath": "sync-over-async.windows.evidence.json"
}
```

## Output contract

`AgentResponseInterpretation` contains:

- `interpretation`: a reused `StructuredInterpretation` populated from heuristic matches;
- `evidenceCitations`: fixture paths such as `metrics[name=threadpool-queue-length]` or `relations[relation=thread-owner-overlap,ownerWaitReason=Thread.Sleep]`, plus matched terms and the supported scenario evidence ids;
- `uncertainty`: matched hedge/assertive markers and the resulting disposition;
- `notes`: explicit prototype caveats.

## Citation heuristic

The mapper does **not** do semantic understanding. It combines:

1. aliases derived from the scenario manifest's expected evidence (`signal`, `metric`, stack-frame terms, relation names);
2. aliases derived from the concrete fixture values (bucket keys, frame display names, owner wait reasons);
3. a small scenario-specific synonym table for phrases that humans are likely to use (`"threadpool queue"`, `"large object heap"`, `"sleeping owner"`, and similar).

A citation is emitted when the free-text response contains one of those aliases, or enough normalized keywords from one alias, as a substring match.

## Uncertainty heuristic

The uncertainty classifier uses explicit marker lists only:

- hedged: `likely`, `appears to`, `may be`, `suggests`, and related phrases;
- assertive: `clearly`, `confirmed`, `root cause is`, `is caused by`, and related phrases.

If both classes appear, the result is `Mixed`. If neither appears, the result is `NoneDetected`.

## Known limitations

- The mapper operates on one committed fixture at a time; it does not compare against a live capture or reason across platform variants.
- Substring/keyword matching can miss paraphrases and can over-match generic phrases.
- The reused `StructuredInterpretation` fields beyond evidence citations are best-effort heuristic fills, not a validated rubric.
- This is groundwork for the future agent-level rubric discussed in #645/#646, not a production evaluator.
