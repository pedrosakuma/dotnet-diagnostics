# Blinded diagnostic-agent evaluation harness

Issue #920 adds an advisory layer beside the deterministic scenario collector. The deterministic
manifest/replay tests remain the evidence-quality gate. The new layer starts the same four bounded
workloads but gives a model only:

- the reported symptom;
- a neutral authorized identity, `target-1`;
- schemas for three existing diagnostic operations: `collect_events` (`counters` or `gc`),
  `collect_sample` (`cpu`), and `collect_thread_snapshot`.

The model chooses the operation order. The harness does not translate a scenario ID into an expected
collector sequence. Workload activation, controller URLs, process arguments, manifests, expected
answers, fixture paths, source, decompilation, shell/filesystem access, and credentials are not
available to the model. Tool arguments are allowlisted, target-bound, and rejected when unknown.
Only read-only operations are exposed. The ptrace boundary for a thread snapshot must be authorized
by the evaluator before the run; the agent cannot approve anything. Approval records distinguish
requested, decided, attempted, and executed actions.

## Provider boundary

`OpenAiCompatibleAgentTransport` is a narrow HTTPS chat-completions adapter with structured function
calling. It is not a general agent platform and has no shell or filesystem tools. A real run requires
all three explicitly configured values:

- `DOTNET_DIAGNOSTICS_AGENT_ENDPOINT`
- `DOTNET_DIAGNOSTICS_AGENT_API_KEY`
- `DOTNET_DIAGNOSTICS_AGENT_MODEL`

Optional provenance fields are `DOTNET_DIAGNOSTICS_AGENT_PROVIDER` and
`DOTNET_DIAGNOSTICS_AGENT_MODEL_VERSION`. The endpoint must be HTTPS and may not contain user
information. No repository, GitHub, or unrelated credential is discovered or reused.

The sole predeclared smoke is
`BlindedAgentRealSmokeTests.RealModelSmoke_RunsOnePredeclaredSyncOverAsyncInvestigation`. It runs only
when `DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE=1`. This prevents accidental spend and repeated exploratory
model calls. Scripted transport tests are persisted/labeled as `scripted`; they are harness evidence,
not real-agent acceptance evidence.

Run it only after an evaluator approves a specific endpoint, model, and dedicated key:

```bash
DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE=1 \
DOTNET_DIAGNOSTICS_AGENT_ENDPOINT='https://approved-provider.example/v1/chat/completions' \
DOTNET_DIAGNOSTICS_AGENT_MODEL='approved-model' \
DOTNET_DIAGNOSTICS_AGENT_API_KEY='<dedicated-key>' \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release --no-build \
  --filter FullyQualifiedName~RealModelSmoke_RunsOnePredeclaredSyncOverAsyncInvestigation
```

`DOTNET_DIAGNOSTICS_AGENT_REPORT_PATH` optionally selects the evaluator-private report path.
`DOTNET_DIAGNOSTICS_AGENT_PROVIDER` and `DOTNET_DIAGNOSTICS_AGENT_MODEL_VERSION` optionally improve
provenance. The predeclared smoke uses temperature `0`, at most 4 provider calls/turns, 3 diagnostic
tool calls, 45 seconds of harness wall time, 10 seconds of aggregate capture, 10,000 input tokens,
1,200 output tokens, USD 0.25 of provider-reported spend, 128 KiB per model response, and 384 KiB of
retained tool results. Its outer test timeout is 180 seconds. If the provider omits token or cost
usage, the report marks those values unavailable; the hard turn, tool, wall-time, capture, response,
and artifact caps still apply.

## Bounds and evidence

The default envelope limits wall time, model turns, tool calls, provider-reported input/output tokens,
individual model responses, total capture seconds, and retained tool-result bytes. Tool projections
also cap counters, hotspots, threads, frames, and locks. Every truncation is disclosed in the tool
result and therefore remains part of model-quality assessment. Cancellation and failure dispose the
evaluator-owned workload driver and process tree.

The evaluator-private report retains original provider responses, tool-call/result IDs, exact
`tool-result://<call-id>#/<JSON-pointer>` evidence locations, SHA-256 result hashes, claims,
observed/inferred/unknown posture, uncertainty, and next steps. It separately reports prerequisites,
activation, agent execution/transport/collection, verified target cleanup, and citation assessment. Missing configuration,
transport failures, invalid output, budget exhaustion, and unresolved citations are never represented
as successful diagnoses.

Provenance records model/provider/configuration, endpoint origin (never credentials or query strings),
prompt/tool-policy hashes, product identity, private workload identity/configuration, runtime, OS,
architecture, topology, capture policy, and unavailable values explicitly. Diagnostic quality remains
advisory; the current assessment only verifies that citations resolve to retained evidence.

## Retention and redaction

Reports are written only to the caller-selected evaluator-private path. CI uploads, if configured
later, should retain them for 30 days. For repeatability, private provenance intentionally retains the
manifest workload configuration, including controller route paths. Those routes, the scenario identity,
and the rest of the private workload configuration never appear in model-visible messages or persisted
model transcripts. No persisted or model-visible artifact contains API keys, authorization headers,
model-request credentials, or other secrets. Process command lines are not retained. Before sharing a
report outside its evaluation boundary, remove private workload provenance and treat all target-derived
symbols and counter data as potentially sensitive operational telemetry.
