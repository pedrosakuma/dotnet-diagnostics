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
provenance. The predeclared smoke uses temperature `0`, at most 4 provider calls/turns, 3 diagnostic
tool calls, 45 seconds of harness wall time, 10 seconds of aggregate capture, 10,000 input tokens,
1,200 output tokens, USD 0.25 of provider-reported spend, 128 KiB per model response, and 384 KiB of
retained tool results. Its outer test timeout is 180 seconds. If the provider omits token or cost
usage, the report marks those values unavailable; the hard turn, tool, wall-time, capture, response,
and artifact caps still apply.

Copilot CLI 1.0.83 does not expose provider token/cost usage in the accepted response or a hard
output-token option. `--max-ai-credits 1` per model turn is defense in depth only: the CLI documents it as a soft
post-call limit and it is not counted as a harness hard budget. For this transport, token/cost fields
remain unavailable and the hard bounds are model turns, one decision per CLI process, tool calls,
wall time, capture time, response bytes, and retained artifact bytes.

### Local smoke status

The first predeclared local attempt used `gpt-5.4-mini`. Workload activation passed, but the CLI
process exited with code 1 before returning a model decision; no diagnostic tool was called.
The report records transport failure, no assessment, and independently observed target cleanup.
Because CLI stderr was intentionally not retained, the exact launch failure is not established.
The invocation has subsequently been corrected to use the documented hyphenated UUID session-ID
format rather than a compact GUID. This correction is not evidence that the smoke succeeds.
The failed evaluator-private report is retained; real-model acceptance remains incomplete.

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
