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

Optional provenance fields are `DOTNET_DIAGNOSTICS_AGENT_PROVIDER`,
`DOTNET_DIAGNOSTICS_AGENT_MODEL_VERSION`, and (for the generic HTTP transport)
`DOTNET_DIAGNOSTICS_AGENT_TRANSPORT_VERSION`. The endpoint must be HTTPS and may not contain user
information. No repository, GitHub, or unrelated credential is discovered or reused.

Alternatively, `CopilotCliAgentTransport` invokes an installed GitHub Copilot CLI as a local process.
Only the harness and target process are local: inference still uses GitHub Copilot's cloud service,
so this mode is **not offline**. Authentication remains entirely inside the CLI. The harness does not
read a token, turn one into an HTTP credential, or accept a Copilot token as a new API key.

The CLI transport carries a JSON decision protocol rather than native HTTP function calling. Each
model turn is a new non-resumed CLI session in a fresh empty directory outside the repository. The
complete bounded conversation and diagnostic schemas are supplied again, and the CLI must return
exactly one strictly validated `tool` or `final` decision. The evaluator's existing broker remains
the only code that executes diagnostics.

Isolation is fail-closed:

- `--available-tools blinded-harness-no-cli-tools` makes only a deliberately nonexistent sentinel
  available; no CLI shell, file, GitHub, browser, agent, or MCP tool is available to the model;
- built-in MCP, custom instructions, ask-user, remote control/export, auto-update, `BASH_ENV`, and
  temporary-directory access are disabled;
- the CLI runs with no allow-all/autopilot option and with a fresh UUID, never resume/continue;
- a dedicated `COPILOT_HOME` must be empty before each invocation, isolating configuration and
  session state, not the CLI's normal authentication sources;
- a model-free `copilot plugins list --json` preflight rejects every enabled non-builtin resource;
- token/key/secret environment variables, custom-provider variables, and telemetry content capture
  are removed from the child environment;
- stdout and stderr are streamed under byte limits, cancellation kills the owned process tree, and
  any CLI tool-activity event fails the turn; only the validated decision—not CLI event/session
  metadata—is retained.

The dedicated empty-home requirement prevents profile hooks and plugins from entering the run.
It does **not** promise authentication isolation: the CLI may use its operating-system credential
store or its normal GitHub CLI OAuth fallback outside `COPILOT_HOME`. Selecting this transport for
an explicitly enabled smoke authorizes those normal CLI authentication sources. The harness never
extracts, copies, logs, or repurposes their tokens as HTTP credentials. If normal CLI authentication
is unavailable, the run remains blocked; do not copy credential files into the isolated home.
The transport deletes only CLI-created session/log/state files in its dedicated invocation storage
after process exit. Existing normal authentication stores are not modified or removed.

The installed CLI help defines `--available-tools` as making only the named tools available. The
adapter tests deterministically verify the exact non-shell argument vector and reject tool-execution
events. Routine metadata such as `session.tools_updated` is not tool execution. During local
transport review, minimal prompts unexpectedly reached a real model while investigating normal CLI
authentication. Those calls were outside the predeclared diagnostic smoke and are a protocol
deviation, not diagnostic acceptance evidence. Do not claim real-model acceptance until the reviewed
smoke below succeeds without CLI tool execution.

The sole predeclared smoke is
`BlindedAgentRealSmokeTests.RealModelSmoke_RunsOnePredeclaredSyncOverAsyncInvestigation`. It runs only
when `DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE=1`. This prevents accidental spend and repeated exploratory
model calls. Scripted transport tests are persisted/labeled as `scripted`; they are harness evidence,
not real-agent acceptance evidence.

Run it only after an evaluator approves a specific endpoint, model, and dedicated key:

```bash
SESSION_STORAGE_ROOT='/path/to/evaluator-private-session-storage'
DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE=1 \
DOTNET_DIAGNOSTICS_AGENT_ENDPOINT='https://approved-provider.example/v1/chat/completions' \
DOTNET_DIAGNOSTICS_AGENT_MODEL='approved-model' \
DOTNET_DIAGNOSTICS_AGENT_API_KEY='<dedicated-key>' \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release --no-build \
  --filter FullyQualifiedName~RealModelSmoke_RunsOnePredeclaredSyncOverAsyncInvestigation
```

For the Copilot CLI alternative, use the CLI's existing approved normal authentication and create
two empty directories outside the repository. If login is needed, perform it through the CLI's
normal documented flow, not by copying credentials into these directories:

```bash
SESSION_STORAGE_ROOT='/path/to/evaluator-private-session-storage'
COPILOT_BIN="$(command -v copilot)"
mkdir -p "$SESSION_STORAGE_ROOT/copilot-harness-home" \
  "$SESSION_STORAGE_ROOT/copilot-harness-work"
```

Then, after review authorizes the single paid smoke:

```bash
DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE=1 \
DOTNET_DIAGNOSTICS_AGENT_TRANSPORT='copilot-cli' \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH="$COPILOT_BIN" \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME="$SESSION_STORAGE_ROOT/copilot-harness-home" \
DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT="$SESSION_STORAGE_ROOT/copilot-harness-work" \
DOTNET_DIAGNOSTICS_AGENT_MODEL='approved-copilot-model' \
dotnet test tests/DotnetDiagnostics.ScenarioEvaluation.Tests/ -c Release --no-build \
  --filter FullyQualifiedName~RealModelSmoke_RunsOnePredeclaredSyncOverAsyncInvestigation
```

`DOTNET_DIAGNOSTICS_AGENT_REPORT_PATH` optionally selects the evaluator-private report path.
`DOTNET_DIAGNOSTICS_AGENT_PROVIDER` and `DOTNET_DIAGNOSTICS_AGENT_MODEL_VERSION` optionally improve
provenance. The Copilot transport obtains its version directly from the configured executable with
`--no-auto-update --version`; a caller-supplied version cannot override it. The predeclared smoke
uses temperature `0`, at most 4 provider calls/turns, 3 diagnostic
tool calls, 45 seconds of harness wall time, 10 seconds of aggregate capture, 10,000 input tokens,
1,200 output tokens, USD 0.25 of provider-reported spend, 128 KiB per model response, and 384 KiB of
retained tool results. Its outer test timeout is 180 seconds. If the provider omits token or cost
usage, the report marks those values unavailable; the hard turn, tool, wall-time, capture, response,
and artifact caps still apply.

Copilot CLI 1.0.83 does not expose provider token/cost usage in the accepted response or a hard
output-token option. The installed CLI requires `--max-ai-credits` to be at least 30, so the transport
uses `--max-ai-credits 30` per model turn as defense in depth only. The CLI documents this as a soft
post-call limit; it is not a harness hard budget and does not imply that a turn consumes 30 credits.
For this transport, token/cost fields remain unavailable and the hard bounds are model turns, one
decision per CLI process, tool calls, wall time, capture time, response bytes, and retained artifact
bytes.

The CLI is explicitly invoked with `--effort low` to reduce reasoning overhead; this does not
guarantee a token count or change any hard response-byte, wall-time, or diagnostic budget.

### Local smoke status

The first two predeclared local attempts used `gpt-5.4-mini`. Workload activation passed, but the CLI
process exited with code 1 before returning a model decision; no diagnostic tool was called. Both
reports record transport failure, no assessment, and independently observed target cleanup. The
second attempt included the correction from a compact GUID to the documented hyphenated UUID
session-ID format, so that change did not explain the failure.

A single transport-only probe then reproduced the isolated invocation and safely classified the
actual CLI error: the configured `--max-ai-credits 1` was rejected because this CLI requires at least
30. The adapter now uses the minimum accepted value and retains only allowlisted error
classifications in reports rather than arbitrary stderr. The probe is launch-debugging evidence, not
diagnostic acceptance evidence. The two failed evaluator-private reports remain preserved.

After launch succeeds, parser failures retain the adapter-generated protocol error text but not raw
CLI event streams. This distinguishes invalid or missing structured decisions from process launch,
authentication, and model-availability failures without persisting arbitrary stderr.

Post-fix attempt 03 completed one genuine brokered counters collection, then its second CLI turn
failed before a final diagnosis; it ran before parser failures retained their safe classification.
Attempt 04 again completed one genuine counters collection and then safely reported that the second
turn emitted no structured decision. The protocol reminder was therefore repeated after the
conversation payload so the strict output boundary is the final model-visible instruction; this
changes no diagnostic hint, tool permission, or acceptance rule. Attempt 05 still emitted no
structured decision on its first turn, so it called no diagnostic tool. All three attempts activated
and independently cleaned up their real targets. None of those three attempts produced an accepted
final diagnosis or assessment.

Instrumented attempt 06 transparently forwarded the same CLI arguments and streams while retaining
only assistant text in a separate private debugging artifact after removing event metadata. It
completed one real thread snapshot, then exceeded the unchanged response-byte limit. The observed
assistant text also exposed a protocol conflict: the inner conversation requested a bare diagnosis
object, while the outer CLI protocol required `{"action":"final","diagnosis":{...}}`. The final
transport reminder now explicitly reconciles those schemas. Parsing still rejects bare diagnoses;
it reads only `assistant.message.data.content`, never decisions embedded in event metadata, and
rejects nonempty native CLI tool requests. The prompt fingerprint includes the final reminder.
Low reasoning effort is now explicit to reduce CLI output overhead. These changes do not establish
a successful smoke by themselves; attempt 06 and its verified target cleanup remain recorded as a failure.

### Successful bounded smoke and interpretation limits

Uninstrumented attempt 07 passed on 2026-09-12 using code commit
`6e84c07cb847e1b097444acbfa8ebaf1adeae62a`, the direct installed CLI, and `gpt-5.4-mini` with low
reasoning effort. In 25.8 seconds, the model chose one six-second counters capture and returned a
structured diagnosis on its second turn: three claims, evidence locations, explicit uncertainty,
and next steps. Activation, collection, execution, citation resolution, and independently observed
target cleanup all passed. Invocation directories were empty afterward. No hard budget was increased,
no CLI tool executed, and token/cost usage remained explicitly unavailable.

The evaluator-private report is retained as `copilot-920-smoke-07/report.json`, run ID
`90a296f1e493432396e77703e4dfb228`, SHA-256
`f95a09d6a222291ee2c8ebc8a258f78fb90aac64767aa7ece820b11e1243a8ff`.
Attempts 01-06 remain failures; the transport-only probe and the earlier review deviation are not
counted as successful diagnostic runs. One passing development smoke does not establish reliability.

This satisfies the real-agent harness execution criterion, **not diagnostic correctness**. Some
citations resolve but name different counters from the associated claim; for example, a claim about
GC pause time cites a generation-size counter. Broad absence claims also require evidence-quality
review. The current assessor only resolves locations, so its `passed` outcome cannot approve those
interpretations. Preserve these examples for the separate human-calibrated advisory pilot in #921.

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
