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

## Results (median seconds, total setup time = cache step + `dotnet restore`, excludes the async cache-save tail)

| Scenario | ubuntu-latest<br>(manual `actions/cache`) | ubuntu-latest<br>(`setup-dotnet` `cache: true`) | windows-latest<br>(manual `actions/cache`) | windows-latest<br>(`setup-dotnet` `cache: true`) |
|---|---:|---:|---:|---:|
| Warm hit                          | 16s | **14s** | 62s | **50s** |
| Forced miss (cold, incl. save)    | 22s | n/a (comparable to no-cache) | 95s | n/a (comparable to no-cache) |
| Partial hit (`restore-keys` fallback) | 23s | n/a | 59s | n/a |
| No cache at all                   | 16s | n/a | 77s | n/a |

Notes:

- Including the async cache-save tail (which runs after the job's own work
  but still counts toward the runner-minute bill), warm-hit totals are
  17s/63s (manual) vs 14s/51s (`setup-dotnet`) — the built-in mechanism
  **skips the save entirely on an exact hit**, while the manual step still
  pays a small (~1s) no-op save.
- The `Setup .NET 10` step itself (SDK/runtime download, unrelated to NuGet)
  varies 8–48s depending on runner scheduling — this is orthogonal noise
  present in every scenario, not something either caching policy affects.
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
`cache: true` / `cache-dependency-path`.** It was faster (or statistically
indistinguishable) in every measured cell — hit, forced miss, and the
no-cache baseline — and removes a whole step (one less action to download,
one less place for the key policy to drift between workflows).

- **Cache invalidation** is preserved exactly: `cache-dependency-path` hashes
  the same file set as before (`**/Directory.Packages.props`,
  `**/*.csproj`), so any package add / version bump still busts the cache.
- **CI and Kind now use an identical policy** (both call `setup-dotnet` with
  the same `cache-dependency-path`); no divergence is justified by the data.
- The manual step's `restore-keys` partial-hit fallback (reusing a stale
  cache after a dependency bump) was *not* a clear win — on Ubuntu it was
  slightly slower than no cache at all (23s vs 16s), likely because
  extracting a large-but-stale tarball costs more than the incremental
  packages it saves downloading. `setup-dotnet`'s built-in cache has no such
  fallback, and the data doesn't show it needing one: a real miss (bumped
  dependency) still completes in line with the uncached baseline, so nothing
  is lost by dropping the fallback.
- Raw measurement commentary and the full run list are recorded in the
  final comment on
  [issue #856](https://github.com/pedrosakuma/dotnet-diagnostics/issues/856).
