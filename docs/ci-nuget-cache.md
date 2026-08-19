# NuGet cache ROI on hosted CI runners

Investigation for [issue #856](https://github.com/pedrosakuma/dotnet-diagnostics/issues/856):
does the `~/.nuget/packages` cache in `.github/workflows/ci.yml` /
`kind-integration.yml` actually pay for itself, and is the current policy
(a manual `actions/cache` step keyed on `Directory.Packages.props` + every
`.csproj`) the best option?

## Method

Median observed timings (from real CI runs, quoted in the issue) showed the
cache step itself costing ~12s on Ubuntu / ~39s on Windows, with `dotnet
restore` adding another ~5s / ~16s. That alone doesn't prove the cache is
harmful — an uncached restore could be slower still — so we ran a dedicated
A/B measurement.

A temporary, `workflow_dispatch`-only workflow (`nuget-cache-experiment.yml`,
merged briefly via #871 and removed once data collection finished) ran the
build matrix (`ubuntu-latest` / `windows-latest`) under five cache policies,
recording GitHub Actions' own per-step `started_at`/`completed_at`
timestamps (via the Jobs REST API) for the setup step, the cache
restore/save steps, and `dotnet restore`:

- **`no-cache`** — no cache step at all; always a full download.
- **`cache-cold`** — the existing manual `actions/cache` step, salted with
  `github.run_id` so the exact key always misses (forces a full download +
  save every run).
- **`cache-warm`** — the existing manual `actions/cache` step with the
  production-style stable key; seeded once, then hit on every subsequent run.
- **`cache-partial`** — exact key salted (always misses), but `restore-keys`
  falls back to the stable warm-cache prefix, approximating what happens
  after a `Directory.Packages.props` bump when most packages are unchanged.
- **`setup-dotnet-cache`** — `actions/setup-dotnet`'s built-in `cache: true`
  + `cache-dependency-path` (no separate `actions/cache` step), seeded once
  then hit on every subsequent run.

3 runs per cell (6 job data points per cell, one per OS) were collected
across 16 total workflow dispatches on 2026-08-18. Concurrent dispatches of
the same cache key race on the save step (`actions/cache`/`setup-dotnet`
both dedupe by exact key), so the `setup-dotnet-cache` warm-hit runs were
re-collected sequentially — the initial concurrent batch was discarded once
the log showed "Dotnet cache is not found" on all three, tracing back to a
save race rather than a real miss.

## Results

Two metrics are reported for every scenario, and every number below is
**one, and only one**, of these two — never mixed within a row or column:

- **Critical-path setup** — `Setup .NET` step + cache restore step (if any)
  + `dotnet restore`, i.e. wall-clock time from job start until the build can
  begin. This is what a human waiting on the job actually experiences.
- **Total incl. cache-save** — critical-path setup **plus** the async
  cache-save step that runs during the job's post-step cleanup. It doesn't
  delay the build, but it does run on the same billed runner, so it's the
  number that matters for runner-minute cost.

Both are medians of 3 runs per cell (6 job data points per cell), computed
per-run then medianed (not built by summing separately-medianed
sub-components, which is why a row's two numbers don't always look like a
simple few-second delta — cache-save duration varies independently of
restore duration).

### Manual `actions/cache` (current/previous policy)

| Scenario | ubuntu critical-path | ubuntu total incl. save | windows critical-path | windows total incl. save |
|---|---:|---:|---:|---:|
| No cache at all                       | 16s | 16s | 77s | 77s |
| Forced miss (cold)                    | 17s | 22s | 85s | 95s |
| Partial hit (`restore-keys` fallback) | 19s | 23s | 50s | 59s |
| Warm hit                              | 16s | 17s | 62s | 63s |

### `setup-dotnet` built-in cache (`cache: true`)

Cleanly measured for the warm-hit case (seeded once, then hit on 3
subsequent sequential runs — see the save-race note above). The miss/cold
and partial-hit cases were not cleanly re-measured for this mechanism (the
`restore-keys`-style prefix fallback doesn't exist for it, and its own
cache-miss path is architecturally the same "no prior cache, fall through to
a plain restore" as `no-cache` above, so no additional data point is
expected to add signal there).

| Scenario  | ubuntu critical-path | ubuntu total incl. save | windows critical-path | windows total incl. save |
|---|---:|---:|---:|---:|
| Warm hit  | **14s** | **14s** | **50s** | **51s** |

Notes:

- On an exact cache hit, `setup-dotnet`'s built-in mechanism **skips the
  save step entirely** (critical-path and total-incl-save are identical or
  ~1s apart), while the manual `actions/cache` step still pays a small
  (~1s) no-op save even on an exact match.
- The `Setup .NET 10` step itself (SDK/runtime download, unrelated to NuGet)
  varies 8–48s depending on runner scheduling — this is orthogonal noise
  present in every scenario, not something either caching policy affects.
  It is included in "critical-path setup" above because it genuinely blocks
  the build in both policies equally.
- Windows benefits the most in absolute terms (its cache tarball
  extraction and the runner's disk I/O are the dominant cost); Ubuntu's
  win is smaller but still consistent and never negative.
- Download volume: the cached `~/.nuget/packages` tree is ~500MB compressed
  either way (`actions/cache` and `setup-dotnet`'s cache use the same
  underlying `@actions/cache` toolkit); the *savings* come entirely from
  skipping the corresponding `dotnet restore` package downloads, not from a
  smaller cache artifact.

## Decision

**Switch both `ci.yml` and `kind-integration.yml` from a manual
`actions/cache` step to `actions/setup-dotnet`'s built-in
`cache: true` / `cache-dependency-path`.** For the steady-state case that
dominates real CI traffic — a warm hit, i.e. no `Directory.Packages.props`/
`*.csproj` change since the last run — it was faster on both metrics and
both OSes (critical-path 14s vs 16s ubuntu, 50s vs 62s windows; total incl.
save 14s vs 17s ubuntu, 51s vs 63s windows) and removes a whole step (one
less action to download, one less place for the key policy to drift between
workflows); a warm hit is the overwhelmingly common case since
`Directory.Packages.props`/project files change far less often than other
source files. Its miss path is architecturally identical to `no-cache` above
(a plain `dotnet restore`, no fallback to fall back to), and the manual
cache's own forced-miss numbers (17s/22s ubuntu, 85s/95s windows) show that
case is only marginally different from — never dramatically worse than —
paying nothing at all (16s/16s ubuntu, 77s/77s windows), so dropping the
`restore-keys` fallback is a safe trade.

- **Cache invalidation** is preserved exactly: `cache-dependency-path` hashes
  the same file set as before (`**/Directory.Packages.props`,
  `**/*.csproj`), so any package add / version bump still busts the cache.
- **CI and Kind now use an identical policy** (both call `setup-dotnet` with
  the same `cache-dependency-path`); no divergence is justified by the data.
- The manual step's `restore-keys` partial-hit fallback (reusing a stale
  cache after a dependency bump) was *not* a clear win — on Ubuntu its
  critical-path time was slightly *slower* than no cache at all (19s vs
  16s), likely because extracting a large-but-stale tarball costs more than
  the incremental packages it saves downloading; on Windows it was faster
  (50s vs 77s). `setup-dotnet`'s built-in cache has no such fallback, and
  the mixed Ubuntu/Windows result above means nothing is clearly lost by
  dropping it.
- Raw measurement commentary and the full run list are recorded in the
  final comment on
  [issue #856](https://github.com/pedrosakuma/dotnet-diagnostics/issues/856).
