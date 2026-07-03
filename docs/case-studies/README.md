# Case studies — the diagnostic loop, narrated end-to-end

The [investigation playbooks](../investigation-playbooks.md) list the tool calls
for a symptom; the [bad-code scenarios](../bad-code-scenarios.md) map each
`samples/BadCodeSample/` endpoint to the tools that pinpoint it. **Case studies
are different**: each one tells the *story* of a single investigation from the
first misleading symptom to the fix, showing the real captures at every step and
— crucially — the **wrong hypothesis the evidence forced us to drop**.

They exist to make one point: on the non-obvious failures, the value is not "run
tool X to see problem X". It is that the tools **refute the intuitive-but-wrong
diagnosis** before you spend a sprint optimising the wrong thing.

| Case study | Reported symptom | The tempting wrong answer | What it actually was |
|---|---|---|---|
| [`sync-over-async.md`](./sync-over-async.md) | "Requests time out under load" | "We're CPU-bound — scale out / add cores" | ThreadPool starvation from sync-over-async blocking |
| [`culture-lookup.md`](./culture-lookup.md) | "A trivial lookup pegs the CPU at 95%" | "We're CPU-bound — parallelise the loop / add cores" | Culture-aware `Dictionary` comparer paying ICU string hashing on every lookup |

The two are deliberately complementary. **`sync-over-async`** is a *structural*
smell that an LLM (or reviewer) reading the source catches instantly, and is
driven via the deterministic **CLI**. **`culture-lookup`** is the opposite: a bug
**invisible to static analysis** (the hot loop is byte-identical to its fast
twin — only a comparer chosen elsewhere differs), diagnosed by an LLM driving the
**MCP server blind to the source**. If you're evaluating what the runtime tools add
*over* reading code, start with `culture-lookup`.

> **Reproducibility.** Every capture below was taken this session against
> `samples/BadCodeSample` (Release) on .NET 10 (Linux), driving load with plain
> `curl` — `sync-over-async` via `dotnet-diagnostics-cli`, `culture-lookup` via the
> **MCP server over `--stdio`**. JSON is trimmed for readability (`// …` marks
> elisions). CPU-sample counts are non-deterministic run to run; the *shape* of the
> evidence is what reproduces. The exact commands are inline so you can re-run them.
