# Case study — "The CPU is pegged, so optimise the loop" (MCP, blind to source)

> **The one-line lesson:** a hot path at 95% CPU *looks* like "our code is doing
> too much work — parallelise it / add cores". A CPU sample plus **one call-tree
> drill** proves the cost is not in our loop at all: it is a **culture-aware
> string hash** hidden inside a `Dictionary` lookup. The fix is a single word.
> None of it is visible by reading the endpoint source.

This is the companion to [`sync-over-async.md`](./sync-over-async.md), but it is
deliberately a **different kind of bug and a different kind of transcript**:

- **Different bug class.** Sync-over-async is a *structural* smell — an LLM (or a
  reviewer) that can read the source spots `.GetAwaiter().GetResult()` instantly.
  This one is **invisible to static analysis**: the handler is a plain dictionary
  lookup in a loop, and the *slow* version and the *fast* version have
  **byte-for-byte identical** hot-loop source. The entire difference is a comparer
  chosen once, far away, at dictionary construction. You cannot read your way to
  this diagnosis — you have to *measure the running process*.
- **Different driver.** The whole investigation below was driven by **me, the
  LLM, through the MCP server** (`--stdio`), with **no access to the source tree**.
  I only had the tools and a PID. The transcript is the real tool I/O and the real
  reasoning — including the wrong turn the first sample tempts you into. It is
  non-deterministic by nature (sample counts vary run to run); the *shape* of the
  evidence is what reproduces.

---

## 0. The ticket

> *"Our feature-flag service is pegging a core at ~95% CPU under load. The lookup
> is a trivial `Dictionary.TryGetValue` in a loop — we already cache everything in
> memory, there's no I/O, no allocation. We think we've just outgrown a single
> box: can we parallelise the lookup across cores, or bump the instance size?"*

Two tempting wrong answers are baked into that ticket:

1. **"It's CPU-bound, so throw cores at it."** True that it's CPU-bound — and
   completely the wrong conclusion, as we'll see.
2. **"The loop is trivial, so the cost must be framework/threadpool overhead."**
   The first CPU sample actively *reinforces* this, and it's a trap.

I am blind to the source. All I have is the MCP server over stdio and a process
list. Here is the actual loop.

## Reproduce the workload

```bash
# terminal 1 — the target
ASPNETCORE_URLS=http://127.0.0.1:18210 \
  dotnet samples/BadCodeSample/bin/Release/net10.0/BadCodeSample.dll

# terminal 2 — sustained CPU load on the culture-aware lookup
for i in $(seq 1 800); do
  curl -s "http://127.0.0.1:18210/culture-lookup?iterations=3000000" >/dev/null &
  sleep 0.06
done
```

The MCP client used for the transcript below is a ~60-line stdio JSON-RPC driver
(`initialize` → `notifications/initialized` → `tools/call`); any MCP client works.
Tool results arrive in `result.content[].text` as JSON envelopes.

---

## 1. Step 0 — triage observes CPU saturation (and hands me the next tool)

`inspect_process(view="triage")` collects counters for a few seconds and
separates observed signals from bounded hypotheses. This is where a blind agent starts.

```jsonc
// inspect_process(view="triage", processId=<pid>)
{
  "summary": "Triage: critical (Critical); hypotheses: cpu.compute-demand (high), threadpool.backlog (moderate) | top: cpu-usage=95.36%(critical), threadpool-queue-length=296items(critical), time-in-gc=0%(normal)",
  "hints": [
    { "nextTool": "collect_sample",
      "reason": "Capture on-CPU samples and inspect exclusive hot frames before assigning a cause.",
      "suggestedArguments": { "kind": "cpu", "processId": <pid>, "durationSeconds": 10, "topN": 25 } }
  ],
  "data": { "triage": {
    "modelVersion": 2,
    "assessment": "critical",
    "observedSignals": [
      { "name": "cpu.utilization", "level": "critical",
        "summary": "CPU utilization was 95.4%.",
        "evidence": [{ "name": "cpu-usage", "value": 95.36, "comparison": ">=", "threshold": 90, "unit": "%", "rationale": "CPU crossed the critical threshold." }] },
      { "name": "threadpool.queue", "level": "critical",
        "summary": "The ThreadPool queue contained 296 work items.",
        "evidence": [{ "name": "threadpool-queue-length", "value": 296, "comparison": ">=", "threshold": 200, "unit": "items", "rationale": "Queue crossed the critical threshold." }] }
    ],
    "hypotheses": [
      { "name": "cpu.compute-demand", "confidence": "high",
        "summary": "The process spent a large share of the window doing compute work.",
        "supportingEvidence": [{ "name": "cpu-usage", "value": 95.36, "comparison": ">=", "threshold": 90, "rationale": "CPU crossed the critical threshold used to assign high confidence." }],
        "contradictingEvidence": [],
        "nextStep": "Capture on-CPU samples and inspect exclusive hot frames before assigning a cause." },
      { "name": "threadpool.backlog", "confidence": "moderate",
        "summary": "Work was queued faster than the ThreadPool completed it; counters do not prove starvation.",
        "supportingEvidence": [{ "name": "threadpool-queue-length", "value": 296, "comparison": ">=", "threshold": 50, "rationale": "Large queue supports a backlog hypothesis." }],
        "contradictingEvidence": [],
        "nextStep": "Collect ThreadPool events and blocking stacks to distinguish sustained starvation, blocking, and transient demand." }
    ],
    "verdict": "cpu-bound", "severity": "Critical",
    "evidence": { "cpuUsage": 95.36, "timeInGc": 0, "allocRate": 56504,
                  "threadPoolQueueLength": 296, "gen2GcCount": 0 }
  } }
}
```

**What a blind agent reads here:**

- `cpuUsage = 95.36%`, `severity = Critical`, with explicit `cpu.compute-demand` evidence.
- `timeInGc = 0`, `allocRate ≈ 0.06 MB/s`, `gen2GcCount = 0` → **not** a GC / allocation
  problem. So it's not "LINQ is boxing" or "we're churning garbage".
- The hypothesis supports the ticket's premise ("it's spending the window on CPU") without
  claiming why. The **naive next move** is exactly the ticket's ask: *scale cores.* Hold that
  thought — triage tells you **that** it's burning CPU, never **where**. The `hints` block
  already points at the tool that answers *where*: `collect_sample`.

## 2. Step 1 — the CPU sample, and the trap it sets

Follow the hint. `collect_sample(kind="cpu")` returns the top hotspots **inline**.

```jsonc
// collect_sample(kind="cpu", processId=<pid>, durationSeconds=8, topN=15)
{
  "summary": "Captured 82086 samples over 8s — showing top 3 of 15 hotspot(s). Top method: System.Threading.Thread.StartCallback() (80638 inclusive / 0 exclusive). Drill into the full call tree with query_snapshot(handle=\"…\", view=\"call-tree\").",
  "data": { "cpu": { "totalSamples": 82086, "topHotspots": [
    { "frame": { "method": "System.Threading.Thread.StartCallback()" },
      "inclusiveSamples": 80638, "exclusiveSamples": 0 },
    { "frame": { "method": "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()" },
      "inclusiveSamples": 71950, "exclusiveSamples": 0 },
    { "frame": { "method": "System.Threading.ThreadPoolWorkQueue.Dispatch()" },
      "inclusiveSamples": 71950, "exclusiveSamples": 0 }
  ] } },
  "handle": "X8F5GDQYD3MSCG9WKQ0G"
}
```

**This is the trap, and it is worth dwelling on.** The top three frames are
`StartCallback`, `WorkerThreadStart`, `Dispatch` — pure ThreadPool plumbing. A
careless reading (human or LLM) concludes one of two wrong things:

- *"See, it's all runtime/threadpool overhead — the framework is the problem,"* or
- *"The hot method is `Thread.StartCallback` — there's nothing I can do."*

Both are wrong, and the tell is in the numbers: every one of these frames has
**`exclusiveSamples: 0`**. They are **inclusive** totals — time spent *somewhere
below them in the call tree*, not in them. `Dispatch` isn't burning CPU; something
it *called* is. The summary even says so: *"Drill into the full call tree."* A
top-N-by-inclusive list is a map of *where the stack goes*, not *where the CPU
goes*. To find the CPU I need the frame with high **exclusive** time.

## 3. Step 2 — the call-tree drill (the reveal)

The sample handed me a `handle`. `query_snapshot(view="call-tree")` walks the
merged caller→callee tree built from those same samples, so I can follow the
inclusive time down to the leaf that actually spends it.

```jsonc
// query_snapshot(handle="X8F5GDQYD3MSCG9WKQ0G", view="call-tree", maxDepth=25)
// … threadpool → Kestrel → routing → EndpointMiddleware → the endpoint lambda …
{
  "frame": { "module": "BadCodeSample",
             "method": "Program+<>c__DisplayClass0_0.<<Main>$>b__12(System.Nullable`1<int32>)" },
  "inclusiveSamples": 76235, "exclusiveSamples": 434,
  "identity": { "source": { "file": ".../samples/BadCodeSample/Program.cs", "startLine": 256 } },
  "children": [
    { "frame": { "module": "System.Private.CoreLib",
        "method": "System.Globalization.CompareInfo.IcuGetHashCodeOfString(System.ReadOnlySpan`1<wchar>, System.Globalization.CompareOptions)" },
      "inclusiveSamples": 75801, "exclusiveSamples": 75801, "children": [] }
  ]
}
```

There it is. Following the inclusive spine down through the threadpool, Kestrel,
and routing, the tree lands on **my** endpoint lambda (`b__12`, `Program.cs:256`) —
and its one hot child is the leaf that finally has the exclusive time:

```
System.Globalization.CompareInfo.IcuGetHashCodeOfString   75801 exclusive / 82086 total  ≈ 89% of all CPU
```

**89% of the entire process's CPU is spent inside an ICU string-hash routine.**
`Icu` = the International Components for Unicode library — this is *culture-aware*
string hashing. My endpoint doesn't call `CompareInfo`, doesn't mention culture,
doesn't hash anything explicitly. It calls `Dictionary.TryGetValue`. So **why is a
dictionary lookup calling into ICU?**

That question — which only the *runtime evidence* could raise — is the whole
diagnosis. A `Dictionary<string, …>` hashes its keys with whatever
`IEqualityComparer<string>` it was constructed with. If that comparer is
**culture-sensitive**, every single `TryGetValue` pays a full Unicode
collation-aware hash through ICU instead of a cheap ordinal one.

## 3b. Postscript — with the signal-grouping layer (2026-07)

Everything above happened before the signal-grouping ("vector") layer
(#514/#523) existed — the drill from Step 1's inclusive trap to Step 2's
`b__12 → IcuGetHashCodeOfString` leaf was manual reasoning over raw JSON. It's
worth showing what the *same* `collect_sample(kind="cpu")` call returns
**today**, because it collapses most of that manual work into one inline
field.

This capture is a fresh, real run of the exact production code path
(`EventPipeCpuSampler` → `CpuSampleSignals.Detect`, the same call
`collect_sample` makes) against a live BadCodeSample process driving the
`/culture-lookup` workload — not a fabricated example:

```jsonc
// collect_sample(kind="cpu", processId=<pid>, durationSeconds=8, topN=15)
{
  "summary": "Captured … samples over 8s — showing top N of 15 hotspot(s). …",
  "data": { "cpu": { "totalSamples": 62088, "topHotspots": [ /* … threadpool plumbing, exclusiveSamples: 0, same trap as Step 1 … */ ] } },
  "signals": [
    {
      "signal": "cpu.self-time.concentration",
      "summary": "CPU self-time is concentrated: 57.4% in System.Globalization.CompareInfo.IcuGetHashCodeOfString(...).",
      "salience": 0.574,
      "buckets": [
        { "key": "System.Globalization.CompareInfo.IcuGetHashCodeOfString(...)", "magnitude": 57.4, "unit": "%" }
      ],
      "nextAction": { "nextTool": "query_snapshot", "reason": "Rank methods by self-time (exclusive) and walk the call tree to the owning frame.", "suggestedArguments": { "view": "top-methods", "rankBy": "exclusive" } }
    }
  ],
  "handle": "…"
}
```

The `signals[]` array names `IcuGetHashCodeOfString` as the self-time leader
**in the same response** that hands back the (still inclusive-only, still
trap-laden) `topHotspots` list — no need to eyeball `exclusiveSamples: 0`
across three plumbing frames, and no need for a separate `query_snapshot`
round-trip just to learn *which* frame to drill into. Steps 1–2 above still
apply for the *call-tree path* (which endpoint/line called it), but the
"where is the CPU actually going" question that used to take a full
`topHotspots` read plus a `call-tree` query is now answered inline.

One nuance worth being explicit about: this inline signal only has the
sampler's single, uncapped self-time leader to work with, so
`cpu.self-time.concentration` is the only signal that can fire inline. A
second, richer signal — `cpu.self-time.by-namespace` — needs the full
per-method self-time ranking, which is only built when the call tree is
materialized (the `signals://cpu-sample/{handle}` MCP Resource, or
equivalently `query_snapshot(view="top-methods", rankBy="exclusive")`).
Pulling that same capture's Resource gives:

```jsonc
// signals://cpu-sample/{handle}
[
  { "signal": "cpu.self-time.concentration", "summary": "CPU self-time is concentrated: 57.4% in …IcuGetHashCodeOfString(...); top 5 frames account for 92.1%.", "salience": 0.574, "buckets": [ /* top 5 methods */ ] },
  {
    "signal": "cpu.self-time.by-namespace",
    "summary": "CPU self-time concentrates in namespace System.Globalization (57.4%).",
    "salience": 0.574,
    "buckets": [
      { "key": "System.Globalization", "magnitude": 57.4, "unit": "%" },
      { "key": "System.Threading", "magnitude": 29.0, "unit": "%" },
      { "key": "(global)", "magnitude": 11.4, "unit": "%" },
      { "key": "Microsoft.Extensions.Logging.Console", "magnitude": 2.1, "unit": "%" }
    ]
  }
]
```

`System.Globalization` at 57.4% is the same neutral, diagnosis-agnostic
observation this whole case study spent two sections manually reconstructing
— it doesn't say "culture-aware string comparison bug," it says "here's where
the weight is," and leaves the conclusion to whoever's reading it. That's the
point of the vector layer: it doesn't shortcut the *investigation* (you still
need Step 2's call-tree drill and Step 3's "why does a dictionary lookup call
ICU?" reasoning to land on root cause), it shortcuts the *triage* — which
frame/namespace is worth looking at in the first place. See
[`docs/tool-reference.md`](../tool-reference.md#signal-grouping-layer)
for the general signal-grouping contract.

## 4. Root cause — the code I couldn't see

Only *now*, diagnosis in hand, do we look at the source. The endpoint
([`Program.cs:253`](../../samples/BadCodeSample/Program.cs)) is exactly as
innocent as the ticket claimed:

```csharp
app.MapGet("/culture-lookup", (int? iterations) =>
{
    var loops = Math.Clamp(iterations ?? 2_000_000, 1, 50_000_000);
    var hits  = RunFlagLookups(flagKeys, cultureAwareFlags, loops);   // ← line 256
    return Results.Ok(new { loops, hits, comparer = "InvariantCultureIgnoreCase" });
});

static long RunFlagLookups(string[] keys, IReadOnlyDictionary<string, bool> flags, int loops)
{
    var hits = 0L;
    for (var i = 0; i < loops; i++)
    {
        var key = keys[i % keys.Length];
        if (flags.TryGetValue(key, out var enabled) && enabled)   // ← the "trivial" lookup
            hits++;
    }
    return hits;
}
```

Nothing here is wrong. `RunFlagLookups` is a plain loop over `TryGetValue`. **The
bug is a single comparer chosen 20 lines earlier**, when the dictionary was built:

```csharp
var cultureAwareFlags = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase); // ← slow
var ordinalFlags      = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);          // ← fast
```

`RunFlagLookups(flagKeys, cultureAwareFlags, …)` and
`RunFlagLookups(flagKeys, ordinalFlags, …)` compile to **identical IL in the hot
loop** — the difference lives entirely in the comparer captured by the dictionary.
This is why static analysis can't catch it: there is no bad line to point at. The
loop is fine. The call site is fine. Only the *runtime dispatch* into ICU, made
visible by the CPU sample, reveals the cost.

`InvariantCultureIgnoreCase` is a **classic** accidental choice — people reach for
it as "case-insensitive string key" without realising it drags in the entire
Unicode collation machinery. For machine-generated keys like `Feature.Flag.0042.Enabled`
there is no linguistic meaning to honour; ordinal is not just faster, it's *more
correct*.

## 5. The fix

One word. `InvariantCultureIgnoreCase` → `OrdinalIgnoreCase`. The sample ships the
corrected endpoint as `/culture-lookup-fixed` for a live A/B:

```diff
-var cultureAwareFlags = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
+var ordinalFlags      = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
```
```diff
-app.MapGet("/culture-lookup", (int? iterations) =>
+app.MapGet("/culture-lookup-fixed", (int? iterations) =>
 {
     var loops = Math.Clamp(iterations ?? 2_000_000, 1, 50_000_000);
-    var hits  = RunFlagLookups(flagKeys, cultureAwareFlags, loops);
+    var hits  = RunFlagLookups(flagKeys, ordinalFlags, loops);
-    return Results.Ok(new { loops, hits, comparer = "InvariantCultureIgnoreCase" });
+    return Results.Ok(new { loops, hits, comparer = "OrdinalIgnoreCase" });
 });
```

`OrdinalIgnoreCase` hashes bytes directly — no ICU, no collation table, no
culture. For a single request the wall-clock difference is already stark
(`iterations=2000000`): **culture-aware ≈ 1.24 s vs ordinal ≈ 0.10 s, ~12× faster**.

## 6. Verify the fix with the same tools

Same load shape, pointed at `/culture-lookup-fixed`, same
`collect_sample` + `query_snapshot(view="call-tree")` drill — then compare the
**exclusive leaves** (the real CPU spenders):

```
// Broken  (/culture-lookup)          exclusive / total
System.Globalization.CompareInfo.IcuGetHashCodeOfString   75801 / 82086   ≈ 89%   ← all the CPU
Program…b__12 (the endpoint)                434                            ≈  0.5%

// Fixed  (/culture-lookup-fixed)     exclusive / total
System.Threading.LowLevelLifoSemaphore.WaitForSignal      26216 / 86410   ≈ 30%   ← threads IDLE, waiting for work
Program…b__13 (the endpoint)              12560            ≈ 15%           ← the ordinal lookup itself
(no CompareInfo / ICU frame anywhere in the tree)
```

Read the fixed column carefully — it's the proof:

- **The ICU frame is gone entirely.** `IcuGetHashCodeOfString` does not appear in
  the fixed call tree at all. The 89%-of-CPU leaf simply ceased to exist.
- **The dominant frame is now an idle-wait** (`LowLevelLifoSemaphore.WaitForSignal`,
  ~30%): under the *same* 800-request load, the ordinal lookups finish so fast the
  ThreadPool workers spend most of their time **parked waiting for the next
  request**. That inversion — from "89% burning ICU" to "30% idle" — is the fix,
  quantified.
- The actual lookup (`b__13`) is now a mere ~15% and it's ordinal hashing, exactly
  where the work *should* be.

| Signal | Broken (`/culture-lookup`) | Fixed (`/culture-lookup-fixed`) |
|---|---|---|
| `inspect_process(view="triage")` | `cpu.compute-demand` **(high)**, cpu ≈ 95% | no CPU hypothesis; threads idle |
| Hot exclusive leaf | `CompareInfo.IcuGetHashCodeOfString` ≈ **89%** | **no ICU frame**; top is an idle semaphore wait |
| Endpoint lambda exclusive | ~0.5% (all cost was below it, in ICU) | ~15% (the lookup itself) |
| Single-request wall time (`iterations=2M`) | ≈ 1.24 s | ≈ 0.10 s (**~12×**) |

---

## Takeaways (why this is the *MCP* case, and the *non-obvious* case)

- **Static analysis could not have found this.** The hot loop is byte-identical
  between the slow and fast versions; the only difference is a comparer object
  chosen elsewhere. There is no bad line to lint. You had to **run it and sample
  it**. This is the category of bug the MCP flow exists for.
- **"CPU-bound" is not "our code is slow".** Triage confirmed 95% CPU, which
  *sounds* like "optimise the loop / add cores". The CPU was real; the *location*
  was a framework hash the code never named. Scaling cores would have paid 12× the
  cloud bill to run the same ICU hash on more threads.
- **Inclusive ≠ exclusive — and the top of the list lies.** The first sample's top
  frames were all `exclusiveSamples: 0` threadpool plumbing. Stopping there gives
  the seductively wrong "it's just runtime overhead" answer. **The call-tree drill
  down to the high-*exclusive* leaf is the whole trick.**
- **The tools chained themselves for a blind agent.** `triage`'s hint named
  `collect_sample`; `collect_sample`'s summary named
  `query_snapshot(view="call-tree")`; the call tree carried the resolved
  `Program.cs:256` source line straight to the culprit. I diagnosed a process I
  could not read the source of, because each envelope pointed at the next move.
- **Verification is symmetric.** Re-running the identical sample+drill on the fixed
  endpoint and watching the 89% ICU leaf **vanish** — replaced by idle-wait — is
  how you prove a CPU fix with evidence instead of a stopwatch and hope.

See also: [`sync-over-async.md`](./sync-over-async.md) (the structural counterpart),
[`investigation-playbooks.md`](../investigation-playbooks.md), and
[`bad-code-scenarios.md`](../bad-code-scenarios.md).
