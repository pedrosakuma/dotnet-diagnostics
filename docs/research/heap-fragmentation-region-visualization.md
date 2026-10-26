# Heap-fragmentation region visualization spike

Issue [#1266](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1266)
asks whether a compact region visualization changes a diagnostic decision beyond
the existing ranked fragmentation table. The parent roadmap is
[#1257](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1257).

This is a test-only prototype and point-in-time feasibility result. It does not
change the Core, CLI, or VS Code production contract.

> **Implementation follow-up:** issue
> [#1270](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1270) promotes
> the bounded observed free-block evidence into the canonical Core/CLI
> `fragmentation` result. The spike's visualization remains out of scope and is
> tracked separately.

## Decision

**Partial GO.**

- **GO:** add a separately scoped Core/CLI contract for the largest observed
  free block, observed free-block count, and deterministic bounded top-K free
  ranges, with explicit partial/unknown and omission metadata.
- **GO after that contract:** add a compact VS Code presentation that keeps the
  current ranked table, adds a proportional used/free/unknown strip, shows the
  largest observed free block, and exposes bounded ranges under disclosure.
- **NO-GO:** do not ship the proportional strip by itself as a topology signal.
  It improves scanning but contains the same byte totals as the current table.
- **NO-GO:** do not add pinned-object concentration until pinned roots or handles
  can be correlated reliably with object and segment addresses.
- **NO-GO:** do not add a full-heap mosaic, per-object map, dominator tree, or
  retained-size/liveness claim.

The result follows the Satori comparison used by the roadmap: a region map is
an anti-reference unless it changes the next diagnostic action. Visual novelty
or legibility alone is not acceptance evidence.

## Current contract

The existing ClrMD heap walk already enumerates every object in every segment.
For each segment it retains:

- generation and logical heap;
- start/end, length, committed bytes, and reserved bytes;
- sum of non-free object bytes;
- sum and count of `obj.IsFree` objects;
- non-free object count and free percentage.

The canonical `fragmentation` query ranks segments by free bytes and then free
percentage. The CLI emits those segment rows, and VS Code renders a table with
generation, heap, free bytes, free percentage, used bytes, segment size, and
object count.

The walk discards every individual free object's address and size after adding
it to the aggregate. Therefore the retained snapshot cannot answer:

- whether equal free totals are one large block or many small blocks;
- the largest observed free block;
- the addresses of representative free blocks;
- spatial pinned-object concentration.

`UsedBytes` and `FreeBytes` are observed walk totals. They are not retained-size
or object-liveness evidence. The live contract also has no per-segment
walk-complete flag; unresolved objects can make observed totals incomplete.

## Validation protocol

The spike used two complementary evidence paths.

### Deterministic projection prototype

`tests/DotnetDiagnostics.Core.Tests/HeapFragmentationSpike/` contains a
test-only model of the information available while the existing walker visits
objects:

- a non-fragmented control with 800 used bytes and one 200-byte free tail;
- a fragmented layout with the same 800 used and 200 free bytes split into ten
  20-byte blocks;
- a partial walk with explicit unknown bytes;
- two layouts whose pinned-object ground truth differs but whose currently
  observable contract is identical;
- a 100,000-free-block stress layout.

The prototype compares:

1. the current `SegmentStat` aggregate;
2. a 20-cell used/free/unknown proportional strip;
3. largest observed free block plus deterministic top-K free ranges.

The top-K retainer applies its cap at insertion time. It keeps aggregate counts
for all observed ranges, reports retained and omitted counts, and orders equal
sizes by address.

### Live ClrMD trials

Five bounded attempts ran on 2026-10-26. Every scenario used a fresh target
process and was captured once through both the repository CLI and
`ClrMdDumpInspector` plus the canonical `HeapSnapshotQueryDispatcher`
fragmentation view. No failed attempt was retried into a success.

Environment:

- Windows 10.0.26300 x64;
- .NET runtime 10.0.14;
- SDK 10.0.403 selected through the repository's `global.json` roll-forward
  policy;
- workstation, non-concurrent GC;
- `DOTNET_GCRetainVM=1`;
- LOH compaction mode `Default`;
- 96 arrays with 1,048,576-byte payloads;
- two blocking generation-2 collections with `compacting: false`.

The live target retained selected arrays to request these shapes:

- a dead allocation tail;
- one contiguous dead run in the middle;
- alternating live/dead allocations;
- a matched pair with one contiguous 16-array dead run versus 16 separated dead
  arrays.

The compact-tail request is intentionally reported as a failed fixture
assumption: the runtime trimmed or released the dead tail instead of preserving
it as a visible large ClrMD free object.

The aggregate trials were followed by a committed fresh-process fixture that
directly enumerates `obj.IsFree` addresses and sizes between two pinned LOH guard
arrays. It creates two equal-size regions:

- a control with eight adjacent dead 128 KiB arrays;
- a separated layout with the same eight dead arrays divided by live 128 KiB
  separators.

The test classifies runtime trimming or marker relocation explicitly instead of
weakening assertions. On the recorded run, both shapes were classified
`Matched`.

## Results

### Deterministic comparison

| Projection | Non-fragmented control | Fragmented layout | Distinguishes topology? |
| --- | ---: | ---: | --- |
| Current used/free totals | 800 / 200 | 800 / 200 | No |
| Proportional strip | `UUUUUUUUUUUUUUUUFFFF` | `UUUUUUUUUUUUUUUUFFFF` | No |
| Free-object count | 1 | 10 | Sometimes, but not a stable hole model |
| Largest observed free block | 200 | 20 | Yes |
| Top-4 ranges | 1 retained, 0 omitted | 4 retained, 6 omitted | Yes, with explicit loss |

The partial fixture accounts for 500 used, 150 free, and 350 unknown bytes. Its
strip is `UUUUUUUUUUFFF???????`; unknown is not silently folded into used or
free. The result explicitly marks object liveness and retained object size as
unavailable.

The pinning fixtures contain different ground-truth pinned addresses but
produce the same current observable projection. Pinned-root count alone cannot
support pinned bytes or a spatial concentration claim.

### Live current-output comparison

LOH aggregates from the current ClrMD segment rows:

| Scenario | Live/dead arrays | LOH segments | Used bytes | Free bytes | Objects | Free objects | Core walk |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Tail control | 48 / 48 | 2 | 50,332,800 | 1,536 | 48 | 48 | 1,592.8 ms |
| One large middle run | 48 / 48 | 3 | 50,332,800 | 10,487,856 | 48 | 48 | 664.9 ms |
| Alternating | 48 / 48 | 4 | 50,332,800 | 48,238,608 | 48 | 48 | 648.5 ms |
| One large run of 16 | 80 / 16 | 4 | 83,888,000 | 16,780,672 | 80 | 80 | 2,274.3 ms |
| Sixteen separated frees | 80 / 16 | 4 | 83,888,000 | 15,732,040 | 80 | 80 | 836.7 ms |

The matched 16-array pair is the decisive current-contract result:

- known topology differs: one contiguous dead run versus 16 separated dead
  allocations;
- used bytes are identical;
- object count is identical;
- `freeObjectCount` is identical at 80;
- free totals differ by approximately one allocation because of runtime
  segment-tail behavior, not because the contract describes adjacency;
- the largest per-segment aggregate free total differs, but that value still
  does not establish the largest individual hole.

ClrMD also reports small alignment or padding free objects around LOH
allocations. `freeObjectCount` is therefore neither the logical dead-allocation
count nor a reliable topology projection.

The tail-control attempt requested a contiguous 48 MiB dead tail but observed
only 1,536 LOH free bytes. Runtime trimming is a real diagnostic outcome, not a
fixture success that should be forced by weakening the assertion.

### Live observed free-block comparison

The committed direct ClrMD fixture produced:

| Shape | Guarded region | Observed free | All observed free records | Material free runs | Largest observed free block | Top-4 sizes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Adjacent dead arrays | 1,966,952 B | 1,049,280 B | 8 | 1 | 1,049,056 B | 1,049,056; 32; 32; 32 |
| Separated dead arrays | 1,966,952 B | 1,049,280 B | 8 | 8 | 131,160 B | 131,160 x 4 |

This closes the gap left by the aggregate-only trials. Equal region size, free
bytes, and raw free-record count still produce materially different largest
blocks and bounded range projections. The test observes the post-GC ClrMD
layout rather than assuming that requested allocation order survived.

The live fixture labels evidence completeness `Indeterminate`. Its guarded
region and resolved byte-array markers make the comparison reproducible, but
ClrMD exposes no formal per-segment or per-region completion signal. The
contract must preserve that distinction rather than convert a successful walk
into an exhaustive claim.

Every walk returned segment rows. The only warning was the existing deliberate
omission of live stack-root enumeration because of the DAC native-fault risk.
No unresolved-object or abandoned-segment warning appeared, but the absence of
a warning is not a formal completeness guarantee.

## Diagnostic-decision impact

| Signal | Changes the next diagnostic decision? | Verdict |
| --- | --- | --- |
| Current ranked table | Identifies segments with substantial observed free capacity and whether it is concentrated by segment, but cannot state hole topology. | Keep |
| Used/free/unknown strip | Makes byte proportions faster to scan and preserves unknown, but cannot distinguish the equal-total synthetic pair. | UI support only; not sufficient alone |
| `freeObjectCount` | Failed on the live matched pair; both shapes reported 80. | Not a topology contract |
| Largest observed free block | Distinguishes whether observed free capacity includes a block large enough for the investigated allocation. | GO |
| Bounded top-K free ranges | Explains whether the largest blocks are isolated or repeated while keeping payload bounded. | GO under disclosure |
| Pinned concentration | Current pinned-root/handle summaries have no reliable object/segment address correlation. | NO-GO |

The largest-block signal changes a concrete action. A large aggregate free total
with no sufficiently large observed block supports investigating fragmentation
or pinning constraints; a sufficiently large observed block redirects the
investigation toward allocation policy, segment selection, runtime trimming, or
another failure mechanism. It does not prove that an allocation will succeed.

## Resource boundedness

The current heap walk already pays O(objects) enumeration cost. The proposed
additional state can remain bounded at the point of observation:

- total free bytes, free-block count, and largest block: O(1) memory and O(1)
  work per observed free object;
- top-K largest blocks: O(K) memory and bounded selection work per observed
  free object;
- no all-hole list and no sort after retaining every range.

The test prototype uses a small ordered array. With 100,000 observed free
blocks and K=8 it retained exactly 8 slots/ranges, omitted 99,992, and performed
799,964 selection comparisons against a deterministic upper bound of 800,000.
This demonstrates O(K) retained memory and O(free blocks x K) selection work for
the prototype. A production implementation may use a fixed min-heap for
O(log K) selection without changing the contract.

The five live baseline walks took 648.5-2,274.3 ms. Those timings include the
existing full ClrMD walk and are not an incremental-overhead comparison for the
proposed fields. Target-process counters remained stable across the diagnostic
reads: the maximum absolute working-set change was 1,445,888 bytes and maximum
absolute private-memory change was 671,744 bytes. These target counters do not
measure allocations in the diagnostic host.

Therefore:

- bounded-memory feasibility is established;
- the expected incremental CPU work is characterized;
- a production contract issue must still measure diagnostic-host allocation,
  RSS, and elapsed delta against the unchanged walker before shipping.

Cap hits must be explicit. The contract must report observed, retained, and
omitted range counts and must never publish an unbounded list.

## Canonical view proposal

Keep `fragmentation` as the canonical view and keep the ranked table directly
accessible.

For each segment row:

1. render an accessible proportional strip for observed used, free, and unknown
   bytes;
2. show exact bytes and percentages as text;
3. show `largest observed free block`;
4. allow keyboard-accessible expansion of at most K largest observed free
   ranges, with start, end/length, retained count, observed count, and omitted
   count;
5. keep quality, partial-walk, and unavailable messages adjacent to the strip;
6. perform no implicit live read when expanding saved evidence.

The strip is a summary, not a map. The range disclosure is bounded evidence,
not a full-heap mosaic. The UI must use "observed free block/range" wording and
must not claim allocator availability, object liveness, retained size, or a
pinning cause.

## Proposed follow-up issues

These are proposals only. This spike does not create them.

### Contract: bounded observed free-block projection

Target Core and CLI.

Acceptance:

- add optional per-segment `largestObservedFreeBlockBytes`;
- add `observedFreeBlockCount`;
- retain a deterministic top-K list of observed free ranges at insertion time;
- report retained and omitted counts plus the configured cap;
- preserve used/free/unknown accounting and expose
  complete/partial/indeterminate state without inferring completeness from a
  successful walk;
- define addresses and lengths as observed ClrMD `obj.IsFree` records, not
  allocator-success guarantees;
- retain the live guarded-region validation for one-large versus many-small
  layouts;
- add deterministic high-hole-count boundedness tests;
- measure incremental diagnostic-host allocation, RSS, and elapsed time;
- keep existing aggregate fields backward compatible;
- add no pinned-concentration, liveness, or retained-size field.

### VS Code: compact canonical fragmentation visualization

Depends on the contract issue.

Acceptance:

- preserve the ranked table and expert direct access;
- add accessible used/free/unknown strips;
- show largest observed free block in the segment row;
- add keyboard-accessible bounded-range disclosure;
- show cap/omission, partial, unknown, and provenance information;
- keep saved evidence read-only and avoid implicit live reads;
- test empty, unavailable, partial, truncated, and complete states;
- use no full-heap mosaic, retained-size wording, liveness wording, or pinned
  overlay.

## Reproduction and validation

The committed deterministic prototype is validated with:

```powershell
dotnet test tests\DotnetDiagnostics.Core.Tests\DotnetDiagnostics.Core.Tests.csproj `
  -c Release --filter FullyQualifiedName~HeapFragmentation
```

The initial aggregate trials built the CLI and a scratch .NET 10 target, then
invoked:

```text
dotnet-diagnostics inspect-heap --source live --pid <pid> --top-types 10 --acknowledge-risk high --json
ClrMdDumpInspector.InspectLiveAsync(pid, TopTypes=10, SnapshotTopTypes=50)
HeapSnapshotQueryDispatcher.Dispatch(view=fragmentation, topN=100)
```

The raw local trial output contained no process dump or trace and was kept
outside git. The durable evidence is the protocol, exact aggregate table,
limitations, prototype, and assertions recorded here.

The committed direct free-block trial is included by the same test filter. It
starts the Core test assembly with
`--heap-fragmentation-loh-fixture tail-free|separated`, attaches with ClrMD,
enumerates observed free records inside the guarded LOH region, and terminates
both owned processes.
