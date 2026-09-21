# Advisory evidence-only LLM assessment

Issue #997 defines a development-only automated advisory reanalysis. It is
separate from the frozen human-calibration v1 protocol. It does not import or
fabricate human reviews, establish ground truth, gate CI, or measure diagnostic
accuracy.

## Two isolated phases

Phase A receives only an allowlisted projection of retained counter or thread
snapshot measurements. It does not receive the previous claims, diagnosis,
uncertainty, next steps, assessment results, citation selection, source-model
identity, workload truth, case names, or authored answer hints. Result-local
identifiers are replaced with `evidence-01`, and retained pointers use
`tool-result://evidence-01#/...`.

Counter projections retain raw counter fields and explicit capture limitations.
Original free-text notes are excluded. A neutral fact may be added only through
an approved annotation bound to the exact source note JSON pointer and SHA-256
of the decoded UTF-8 note text, without a newline. The source text and binding
hash remain controller-only; only the neutral replacement enters model input.
Thread snapshot projections retain raw thread IDs, states, stacks, the runtime
ThreadPool-worker flag, and lock ownership/waiter graph. Product-derived
signals, recommendations, salience, inferred wait reasons, blocked heuristics,
and derived role/contended flags are excluded. Unknown shapes fail closed.

Phase A must produce bounded observations, hypotheses, alternatives, citations,
uncertainty, optional abstention, and one next diagnostic question. Its result
is written with create-new semantics and hashed before Phase B.
Both prompts disclose the enforced response limits before the single attempt.

Phase B runs in a fresh Copilot CLI session. It sees the same projected evidence
and two opaque, counterbalanced candidates. It judges every claim in both the
earlier interpretation and the new interpretation for evidence support and
certainty, then judges uncertainty/abstention and next-step usefulness.
Declared claim posture is retained for that judgment, but is not ground truth.
Structured confidence and abstention declarations are withheld symmetrically:
the earlier format did not record them, so null/non-null fields would identify
the sources. The complete Phase-A declarations remain in its sealed artifact.
Phase B therefore judges certainty from wording/posture and uncertainty or
abstention from prose, not the withheld structured declarations.
Original/new aliases remain in the controller-only result. Phase A is not an
oracle. Mechanical pointer existence is reported separately from semantic
judgment; a pointer excluded by projection is not relabeled as originally
invalid.

## Frozen pilot contract

The executable contract is schema version 2; its JSON schema is
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Calibration/advisory-llm-assessment-v2.protocol.schema.json`.
Before any model call, freeze one explicit manifest with:

- exactly eight development slots: five retained live-model packets and three
  authored edited replays; no failed Windows replacement and no held-out input;
- the frozen source protocol, rubric, and source-product commit from calibration
  v1, kept in controller provenance and never included in model prompts;
- absolute packet paths, packet file SHA-256 values, packet fingerprints, and
  approved note bindings;
- four original-first and four reanalysis-first candidate orders;
- Phase A `claude-sonnet-5`, Phase B `gpt-5.6-sol`, and immutable provider
  versions recorded as `unknown`;
- the detected Copilot CLI version;
- at most 16 calls, one call per phase per case, no retries or quality-based
  fallback; 120 seconds per call, 32 minutes total inference, and five minutes
  outer overhead;
- 64 KiB prompt, 64 KiB response, and 1 MiB per-case artifact limits.

The CLI's minimum `--max-ai-credits 30` is a transport cap, not a dollar or
token ceiling. Token counts and cost remain `null` when the CLI does not report
them.
Each packet must match the declared source protocol ID, protocol fingerprint,
rubric fingerprint, and product commit as well as its own file and packet
hashes. Missing or foreign source provenance is rejected.

## Local preparation and execution

Create a draft with all protocol fields and an empty `protocolFingerprint`.
Freeze it using the canonical .NET serializer, without invoking a model or
reading packet contents:

```bash
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION=freeze \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL=/absolute/protocol.draft.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_FROZEN_PROTOCOL=/absolute/frozen-protocol.json \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --filter FullyQualifiedName~ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol
```

The draft is strictly validated and remains unchanged. The frozen output must
not already exist; existing protocols are never overwritten or re-fingerprinted.

Preparation is file-only and does not invoke a model:

```bash
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION=prepare \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL=/absolute/frozen-protocol.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OUTPUT_DIRECTORY=/absolute/new-output \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --filter FullyQualifiedName~ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol
```

Real execution additionally requires an empty isolated Copilot home, an
outside-repository work root, the CLI path, and two explicit opt-ins:

```bash
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION=run \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_RUN=1 \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL=/absolute/frozen-protocol.json \
DOTNET_DIAGNOSTICS_ADVISORY_LLM_OUTPUT_DIRECTORY=/absolute/new-output \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH=/absolute/copilot \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME=/absolute/empty-home \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT=/absolute/outside-repository \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release \
  --filter FullyQualifiedName~ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol
```

Each CLI call gets a fresh UUID/session and working directory, no callable
tools, no built-in MCPs, no custom instructions, no `BASH_ENV`, no remote
execution/export, normal external authentication, and bounded cleanup.
Failures and bounded raw responses are retained. A Phase A timeout or invalid
response skips Phase B. A global isolation, authentication, model availability,
or CLI-configuration failure aborts remaining calls.

## Interpretation limits

Retained evidence was selected by an earlier investigation and is therefore
selection-biased. Public sample shapes may be recognizable. Different model
names or families are not statistically independent, and model agreement is
not accuracy. There is no human truth set in this assessment. Reanalysis of
historical retained signals is not a fresh investigation and cannot observe
signals that were never captured.
