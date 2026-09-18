# Windows ICU symbols: availability is not verified function attribution

**Date:** 2026-09-18. **Issue:** [#987](https://github.com/pedrosakuma/dotnet-diagnostics/issues/987),
extracted from [#985](https://github.com/pedrosakuma/dotnet-diagnostics/issues/985).
**Inspected repository:** `7bcf542b37444e7d1592a49789767ad0aeef35c7`.

## Verdict and scope

**Conditional GO for a separately reviewed offline resolver/provenance follow-up;
NO conclusion about historical sampled-function coverage.** Matching public symbols
were obtained for the current local Windows ICU image and contain corroborated
function-range metadata. **Phase 4 executed both declared Windows DIA/TraceEvent
offline lookup paths:** both accepted the requested identity, exposed DIA age 1
and returned a questionable `??`-prefixed name at the excluded-end control.
This establishes concrete library behavior, **not historical sampled-function
coverage or full product ETL aggregation**. Phase 2's redirect-policy failure and
phase 3's PowerShell authorization failure remain preserved below.
Subsequent source inspection **held the proposed rendered-name fix**: the marker
is not unambiguous typed provenance. The next decision is bounded feasibility of
a typed resolver contract before interning, not a string heuristic or log parser.

Keep three outcomes separate:

1. A PDB was obtained for an identified image.
2. A resolver returned a name for an address.
3. The address belongs to a verified function range in that exact image.

None implies the next automatically. This document proposes no production resolver,
artifact schema or MCP tool changes. It does not change the durable
[`culture-lookup@2.0.0` ownership contract](../case-studies/culture-lookup.md#reproduce-the-workload),
whose successful Windows acceptance does **not** validate native ICU leaf symbols.
Private ICU names and comparer cost ordering must not become its CI invariants.

### Evidence provenance

- **Phase 1 — researcher-observed:** read-only PE/PDB inspection and three bounded
  in-memory public downloads. Counts and hashes below are preserved from that
  research handoff; they were not remeasured by the documentation writer.
- **Source-verified:** repository code at the revision above, PerfView/TraceEvent
  v3.2.2 at `ffa46a1548d9ba6cdbf92be3dd4271d1de723046`, and immutable Microsoft
  implementation/documentation citations below.
- **Phase 2 — directly observed:** image hash recheck and one new HTTP request
  under the [published finite plan](https://github.com/pedrosakuma/dotnet-diagnostics/issues/987#issuecomment-5736646400).
  No native lookup was executed.
- **Phase 3 — directly observed:** a new, explicitly authorized bounded redirect
  policy staged the hash-verified PDB in one two-GET chain. The first native
  setup-launcher invocation failed authorization before script execution.
- **Parent HEAD — relayed, separate:** one HTTP HEAD, not a PDB download,
  identified the redirect host used to constrain phase 3. Its signed query was
  neither reused nor retained.
- **Phase 4 — directly observed:** an authorized session-only native .NET console
  harness completed the two offline identity lookups and six name/start calls.
  Setup, actual API arguments, dependency hashes and bounded logs were retained.

## Historical capture cannot be rebound to the current image

[Run 35381314262](https://github.com/pedrosakuma/dotnet-diagnostics/actions/runs/35381314262),
revision `e6334d8d30f679d2c594b70b26b747e2923fc875`, recorded 24,570
Windows ETW on-CPU samples. Its retained evidence contains:

| Observation | Value | What it establishes |
|---|---:|---|
| ICU module-exclusive samples | 18,761 / 24,570 = 76.36% | Module attribution, not named function attribution |
| Retained distinct unresolved ICU PCs | 25 | A bounded candidate inventory |
| Samples represented by those PCs | 13,108 / 18,761 = 69.87% | **Retention** of ICU sample weight, not symbol coverage |
| Named ICU functions in those candidates | 0 | The retained candidates are unresolved |
| Largest candidate, `icu!0x7FFC65FEC653` | 2,095 samples; 8.5267% of all samples | One unresolved address, below the unchanged 15% concentration gate |
| Managed `IcuGetHashCodeOfString` wrapper | 1,242 / 24,570 = 5.055% exclusive | Managed leaf observations, not its native descendants' CPU |

The researcher inspected six diagnostic-result files and 32 preceding follow-up
files. Neither inventory contained ETL/ETLX, a native image, PDB or nettrace;
searched text lacked the ICU RSDS identity, load base, image size and exact
path/version. Therefore:

- Historical exact-image lookup was **unattempted**, not “symbols unavailable.”
- **Zero historical PCs were resolved.** Do not infer an ASLR base from clustered
  addresses or substitute the current machine's DLL.
- Neither global `PdbResolved` nor all-stack resolved-frame counts establish
  exclusive ICU function coverage.

Original failures remain failures of their original contract. Symbolization cannot
transfer native exclusive samples into the exclusive count of a managed ancestor.

## Phase 1: exact current-image availability

The researcher read `C:\Windows\System32\icu.dll` through WSL and native Windows
version metadata. This is a **different, identified local environment**, not a
reproduction of the historical hosted image.

| Field | Observed value |
|---|---|
| Reported Windows version | `10.0.26200.0` |
| Architecture | AMD64 |
| ICU file/product version | `72.1.0.4` |
| DLL file length | 2,769,976 bytes |
| PE image size | `0x29F000` |
| PE timestamp field | `0x4569CD7C` |
| DLL SHA-256 | `7e28c779e6015cc794677ffb00fef38ed76f0f0734130f8c85deaa2ff22fe703` |
| RSDS PDB name | `icu.pdb` |
| RSDS GUID | `94451369-D782-EA5D-26A5-A3501C131722` |
| Requested image age | 1 |
| Downloaded PDB length | 3,059,712 bytes |
| PDB SHA-256 | `39903687ca8e10aec69ad64f98d100c3f67c5182a64a3b24c865726f60b0adb9` |
| PDB Info-stream age / DBI age | 3 / 1 |

Exact public request:

```text
https://msdl.microsoft.com/download/symbols/icu.pdb/94451369D782EA5D26A5A3501C1317221/icu.pdb
```

Phase 1 reported **three successful HTTP-200 GETs**, each with a 16 MiB cap,
20-second read timeout and zero retries: initial inspection, DBI/procedure
verification, and section/OMAP/range corroboration. Approximately 8.75 MiB was
read in total; no PDB cache or binaries were written. The redirect chain was not
retained. A `llvm-pdbutil` stdin attempt failed; successful structural observations
came from in-memory inspection, with PE identity independently inspected using
`llvm-readobj`. These results are **not native DIA execution**.

The GUID matches; all 320 bytes of the PDB's eight section headers match the PE
headers; no OMAP-to/from streams were present. Windows' combined `icu.dll`
distribution is documented from Windows 10 version 1903 onward.[^windows-icu]
This says nothing about arbitrary ICU builds.

### Age counterexample: newer Info age is not automatically a mismatch

The initial simplistic `InfoAge == imageAge` comparison was false. Treating that
alone as rejection would be wrong here. Microsoft's published implementation
checks the GUID, rejects `imageAge > PdbAge`, and requires a **nonzero DBI age**
to equal the image age.[^pdb-guid][^pdb-age] Thus image age 1, Info age 3 and DBI
age 1 satisfy those structural checks.

Phase 1 supplied an implementation-backed compatibility observation, **not a
measurement of DIA's `globalScope.age`**. Phase 4 subsequently measured **DIA
age 1 for this exact PDB**, with both declared paths accepting the requested
GUID/age. TraceEvent's local-file matcher compares DIA GUID/age for equality,
while the indexed-server/cache path follows different selection logic.[^local-match][^indexed]
There is no evidence here that this legitimate newer Info age requires a
production age-matcher change; neither one file nor the indexed-cache hit tests
all possible identity mismatches.

### Metadata corroboration, not sampled coverage

| Inventory | Count |
|---|---:|
| Public symbol records | 15,249 |
| Public symbols marked as functions | 8,215 |
| Explicit length-bearing procedures | 1,360 |
| PE runtime-function entries | 6,618 |
| Procedures exactly matching a PE runtime-function start **and end** | 1,026 |
| Unique PE runtime-function starts coinciding with public function symbols | 5,296 |

Examples observed in this **one image**:

| Name | Corroborated half-open metadata range |
|---|---|
| `initialize_legacy_wide_specifiers` | `[0xCCF50, 0xCCF6D)` |
| `initialize_msvcrt_compatibility` | `[0xCCFA0, 0xCCFBD)` |
| `dllmain_crt_dispatch` | `[0xCCFD0, 0xCD020)` |

The public ICU name `RuleBasedCollator::writeSortKey` coincides with
`[0x38874, 0x38ACE)`. These names are observations, **not proposed test invariants**.
Metadata-only boundary checks accepted start/final-byte addresses and excluded
the end. They were neither sampled addresses nor product resolver calls.

Microsoft describes x64 runtime-function entries as image-relative start/end/
unwind triples for nonleaf or stack-allocating functions, not an exhaustive
named-function inventory.[^pdata] Aliases, omitted unwind entries, split/chained
ranges and partial procedure metadata prevent interpreting these counts as
either full function inventory or sampled coverage.

## Phase 2: finite offline library preflight stopped before execution

The [predeclared batch](https://github.com/pedrosakuma/dotnet-diagnostics/issues/987#issuecomment-5736646400)
allowed:

1. One new exact PDB GET, 16 MiB response cap, 20-second socket timeout within a
   30-second total deadline, **no redirects and zero retries**. Require the exact
   length/hash above and stop if the DLL hash changed.
2. If staging succeeded, at most one explicit local-directory `CacheOnly`
   `FindSymbolFilePath` call and one prepopulated indexed
   `srv*session-cache*https://msdl.microsoft.com/download/symbols` `CacheOnly`
   call, with separate readers and no inherited paths/network lookup.
3. For each successful lookup only, `OpenNativeSymbolFile` and three name/start
   probes at `0xCCF50`, `0xCCF6C`, `0xCCF6D`: metadata start, last byte and excluded
   end. Native execution would have a 60-second deadline, shared validation lock
   and no retries.

**Actual outcome:** the DLL SHA-256 and 2,769,976-byte length matched. The single
request returned **HTTP 302 Found**, rejected by the declared no-redirect policy.
The batch stopped: **zero staged PDB files, zero native executions, zero
`FindSymbolFilePath` calls and zero RVA probes**. The redirect destination was
not retained and is not inferred here.

This is a **transport-policy stop**, not an authoritative missing-symbol response.
It does not invalidate phase 1's successful availability observation. No alternate
download, relaxed policy or repeated attempt was made in phase 2. At that stage,
native assembly loading, DIA GUID/age behavior and excluded-end naming remained
**unattempted**, not passed or failed. A source-only check also found an invalid getter-only
`SymbolCacheDirectory` assignment in the unexecuted draft harness; that draft
is not a working reproduction script.

The existing root Release output supplied TraceEvent 3.2.2, FastSerialization and
`amd64/msdia140.dll`; no new project or package was installed. Public filename-based
opening requires a materialized file and native DIA.[^open][^dia] An internal
stream constructor would bypass part of the identity path being investigated.
Before staging, only three conventional cache locations had been checked:
`C:\symbols\private`, `C:\symbols`, and `C:\symcache`, each with the exact indexed
PDB suffix. Their misses were not an exhaustive cache search.

## Phase 3: verified staging, launcher authorization blocker

After a **separate parent HEAD** observed an HTTP 302 to
`vsblobprodscussu5shard65.blob.core.windows.net`, the parent explicitly authorized
a new redirect-aware staging policy. The HEAD downloaded no PDB and is not
counted as a phase-3 GET. The [phase-3 plan was published before execution](https://github.com/pedrosakuma/dotnet-diagnostics/issues/987#issuecomment-5736735352):

- One chain, at most two GETs and one redirect from the exact MSDL symbol key to
  **that exact HTTPS host**. No delegated credentials, global redirect following,
  other host, second redirect or retry.
- A fresh `Location` from the GET, never the parent's signed URL. Reject userinfo,
  nonstandard ports and fragments. Keep the signed query only in memory; retain
  only the host/path and full-URL hash.
- A 16 MiB total response-body cap, 20-second socket timeout and 45-second
  whole-operation deadline; the unchanged DLL and PDB hash/length requirements.
- Separate bounded native setup/reflection from the unchanged maximum two
  identity lookups and three RVAs per successful lookup. Unsupported setup
  stops actual probes. Native commands use the shared validation lock.

### Observed request chain

| Request | Host | HTTP status |
|---|---|---:|
| First GET | `msdl.microsoft.com` | 302 |
| Only redirected GET | `vsblobprodscussu5shard65.blob.core.windows.net` | 200 |

Exactly **two GETs, one redirect and zero retries** read **3,059,712 bytes**.
The current DLL still had the exact SHA-256 and length in the phase-1 table.
The downloaded PDB SHA-256 was again
`39903687ca8e10aec69ad64f98d100c3f67c5182a64a3b24c865726f60b0adb9`.
The verified bytes were staged in the two session-owned local/indexed locations;
both stored files were subsequently hash-checked. No signed query or redirect
exception text was logged or persisted; no binary is committed.

This is new availability evidence under an explicitly changed policy, **not a
retry of phase 2's unchanged policy**. Phase 2's HTTP-302 failure remains preserved.
Phase 1's three successful in-memory downloads, phase 2's single rejected GET,
the parent's HEAD, and phase 3's two-GET chain are distinct operations.

### Preparation and actual native counts

Source inspection established why the unexecuted harness needed changes:
`SymbolCacheDirectory` is getter-only, and `SymbolPathElement.IsRemote` classifies
an absolute WSL UNC folder as remote, which `CacheOnly` skips for ordinary
directory lookup.[^path-policy] The new preparation script uses an explicit
session-relative local directory with a recorded UNC backing path, an explicit
session-owned indexed cache, no inherited symbol paths, and `CacheOnly`.
It would verify those effective paths/options by reflection, load the existing
native DIA DLL without opening a PDB, and provide an HTTP-denying handler and
an insertion-bounded diagnostic writer. These are **prepared controls, not
executed validation**.

The first native Windows PowerShell 7 invocation used `-NoLogo -NoProfile
-NonInteractive -File` on the session-owned UNC launcher under `flock --close`.
It exited **1** with:

```text
SecurityError: AuthorizationManager check failed.
```

No launcher/setup reports were emitted: the launcher script was rejected before
it started its bounded child. Actual counts:

| Operation | Count |
|---|---:|
| Native PowerShell host invocations | 1 |
| Launcher script / setup child executions | 0 / 0 |
| Reflection or native DIA prerequisite execution | 0 |
| Local-folder / indexed-cache identity lookups | 0 / 0 |
| `OpenNativeSymbolFile` / RVA probes | 0 / 0 |
| Lookup retries / policy changes | 0 / 0 |

The batch stopped without changing execution policy or trying an alternate
launch route. The error's underlying authorization cause was not established.
This is **a launcher authorization blocker**, not an assembly incompatibility,
DIA failure, GUID/age mismatch, unavailable symbol or successful lookup. At the
end of phase 3, PDB staging was satisfied but native execution remained blocked.
Phase 4 below is a **separately authorized ordinary .NET route**, not a retry or
policy workaround for the rejected PowerShell script.

## Phase 4: actual offline TraceEvent/DIA results

The [phase-4 plan](https://github.com/pedrosakuma/dotnet-diagnostics/issues/987#issuecomment-5736810063)
was published before build/execution. It authorized a session-only C# console
harness using installed native Windows **SDK 10.0.303**, direct references to
existing TraceEvent/FastSerialization binaries and the existing
`amd64/msdia140.dll`. No package dependencies were added; the session NuGet config
cleared feeds and restore used installed reference packs. No PowerShell,
execution-policy change, new PDB download or production source edit was involved.

The single native build succeeded in **9.74 seconds, zero warnings/errors**.
Both build and execution used the shared `flock --close` validation lock.
The supervisor started exactly one child with a 60-second deadline; it exited 0
without deadline termination. Supervisor stdout/stderr were empty.

### Setup and provenance

Before any identity lookup, the harness recorded public API reflection, loaded
the native DIA dependency without opening a PDB, cleared inherited symbol paths
in its own process, and rechecked the exact system DLL plus **both** staged PDB
hashes/lengths. All matched the earlier tables.

| Field | Executed value |
|---|---|
| Platform / architecture | Windows `10.0.26200.0` / X64 |
| Runtime | `.NET 10.0.12` |
| TraceEvent assembly / file version | `3.2.2.0` / `3.2.2` |
| TraceEvent SHA-256 | `4251a4352098b77857ab70286f45718df373cc709e7cb4448843e6ab252e50fb` |
| Native DIA SHA-256 | `7341081feac7a2cebcc796c9c2bc5041a295df0673244f2595294e06a0bd7ee8` |
| Harness `Program.cs` SHA-256 | `31d255ba6223881ee4a3da0b4236fa583623e4a8f11526eae416b25a1a9bab5f` |

The project, `global.json`, offline NuGet config, runtime config and source hashes
are retained in session artifacts. Public reflection confirmed:

```text
SymbolReader(TextWriter, String, DelegatingHandler)
String FindSymbolFilePath(String, Guid, Int32, String, String, Boolean)
NativeSymbolModule OpenNativeSymbolFile(String)
String FindNameForRva(UInt32, UInt32 ByRef)
Guid PdbGuid
Int32 PdbAge
```

`SymbolCacheDirectory` was confirmed getter-only. No private reflection/PDB
parser or identity/security-check override was used.

### Two paths, unchanged actual lookup budget

Each separate reader used `CacheOnly`, an explicit symbol path and an
HTTP-denying handler. The local path was `.\phase3\local` with the session root as
the explicit process current directory; public path classification reported
`IsRemote=false`, and its resolved backing path was the local WSL UNC session
folder. The indexed path named the session's prepopulated cache explicitly and
`https://msdl.microsoft.com/download/symbols`; its **cache hit** required no HTTP.
This checks local WSL file access, not permission to use an arbitrary remote share.

Both calls had exactly these arguments:

```text
FindSymbolFilePath("icu.pdb",
  Guid("94451369-D782-EA5D-26A5-A3501C131722"), 1, null, "", false)
```

| Path | Identity calls | Explicit `OpenNativeSymbolFile` calls | RVA calls | HTTP-handler attempts |
|---|---:|---:|---:|---:|
| Explicit local folder | 1 | 1 | 3 | 0 |
| Prepopulated indexed cache | 1 | 1 | 3 | 0 |

Both returned their staged PDB path, exposed GUID
`94451369-D782-EA5D-26A5-A3501C131722` and **DIA `PdbAge=1`**.
The local matcher also opens the PDB internally during identity checking; that is
not an extra harness lookup/retry. No attempt was repeated.

### Boundary result: a returned name is not a containing-range guarantee

Both paths produced identical results:

| RVA | Metadata control | Returned name | Returned start |
|---|---|---|---|
| `0xCCF50` | Start | `initialize_legacy_wide_specifiers` | `0xCCF50` |
| `0xCCF6C` | Last byte | `initialize_legacy_wide_specifiers` | `0xCCF50` |
| `0xCCF6D` | Excluded end | `??initialize_legacy_wide_specifiers` | `0xCCF50` |

Both logs explicitly reported:

```text
Warning: NOT IN RANGE: address 0xccf6d start ccf50 end ccf6d Offset 1d Len 1d,
symbol initialize_legacy_wide_specifiers, prefixing with ??.
```

The local/indexed logs retained **584 / 1,345 characters**, respectively, with
no truncation under the insertion-enforced 65,536-character cap. These are actual
library results for the previously selected metadata controls. The public
structured result still supplies **name/start, not end or confidence**: the
human-readable warning is evidence of this counterexample, not a proposed
stable range-metadata interface. The harness marked every result
`containingRangeVerified=false`.

**Implication:** the source-level questionable-name concern is now reproduced
through the actual dependency, and this exact newer-Info-age PDB works without
relaxing the local age check. This initially suggested a narrow questionable-name
filter. Subsequent construction tracing, below, **blocks that rendered-name
approach**: filtering `??` cannot reliably distinguish a known range warning
from literal name content, and some warnings can lose that prefix. Even an
unambiguous questionable-result rejection would **not** prove containing ranges
for every remaining name, including zero-length symbols.
No historical PC, ICU sampled-function coverage, managed exclusive cost or
full ETL capture/aggregation was tested.

## Source-verified product and dependency limits

At the inspected repository revision:

- [`SymbolPathBuilder`](https://github.com/pedrosakuma/dotnet-diagnostics/blob/7bcf542b37444e7d1592a49789767ad0aeef35c7/src/DotnetDiagnostics.Core/Symbols/SymbolPathBuilder.cs)
  uses explicit path, configured default, `_NT_SYMBOL_PATH`, then executable
  directory. It does not add Microsoft's public server automatically.
- [`EtwNativeAotCpuSampler`](https://github.com/pedrosakuma/dotnet-diagnostics/blob/7bcf542b37444e7d1592a49789767ad0aeef35c7/src/DotnetDiagnostics.Core/CpuSampling/EtwNativeAotCpuSampler.cs)
  disables automatic resolution during ETL conversion, then calls per-module
  symbol lookup with `TextWriter.Null`. Lookup diagnostics disappear.
- Its global `PdbResolved` classification means a retained hotspot has a name
  that does not look unresolved. This is not per-module identity/range evidence
  and may reflect managed metadata rather than a native PDB.

Pinned TraceEvent makes additional distinctions important:

- Its local-file matcher checks DIA GUID/age; the indexed source/cache path
  selects an exact GUID/age key.[^local-match][^indexed][^dia-age]
- `FindNameForRva` can return a zero-length symbol's name. For a nonzero-length
  symbol outside its range it prefixes the name with `??` rather than returning
  unresolved.[^questionable] The sampler's unresolved-name checks do not reject
  that prefix.
- TraceLog interns nonempty returned names without independently proving a
  containing range.[^intern] The public lookup interface exposes **name/start,
  not an end address or range-confidence value**.[^interface]

These are source-level capability gaps and counterexamples to “name returned
means verified range”; phase 4 also directly reproduced the excluded-end
questionable name. They are **not evidence of historical misnaming**. The completed
three-address-per-path library probes do not validate full ETL aggregation or
sampled-address coverage.

### Construction ambiguity: source fix held before implementation

After phase 4, the proposed production follow-up was inspected before editing.
The public aggregation input does not preserve the status needed to reject
**only known upstream out-of-range results**:

1. `NativeSymbolModule.FindNameForRva` starts with `symbol.name`. It prepends `??`
   when a nonzero-length symbol does not contain the requested RVA.[^questionable]
   **At this point**, the resolver knows why it added the marker.
2. **Afterward**, if the rendered string contains `@`, it calls
   `symbol.get_undecoratedNameEx`. A non-null result **replaces** the string,
   potentially removing the added marker. If undecoration returns null, literal
   leading question marks are not escaped or separately tagged, and the string
   may be truncated at `@`.[^undecoration] The resulting name cannot prove whether
   a leading `??` came from range validation or literal native/decorated content.
3. Project N merged-assembly mapping may then prepend an assembly name and `!`.
   A surviving marker need not be at the beginning of the final string.[^merged-name]
   This is a resolver transformation; `TraceMethods.FullMethodName` itself does
   **not** prepend the module name.
4. TraceLog interns the resulting nonempty string by name and stores the name,
   module index and native symbol start. It retains neither an explicit
   questionable-range status nor a containing end.[^intern][^method-store]
5. `TraceCodeAddress.FullMethodName` returns that stored name directly.
   `MethodToken`/`MethodRva` can distinguish managed/native identity, but do not
   recover marker provenance or range validity.[^full-name][^method-kind]

**Evidence boundary:** these transformations are verified in the pinned source.
Phase 4 directly executed only the retained-prefix counterexample for its three
selected RVAs. It did **not** execute additional decorated-name, failed-
undecoration or Project N mapping controls. The source establishes why provenance
is not encoded unambiguously; it is not a claim that a specific legitimate symbol
was misclassified in the historical capture.

Consequently, no `StartsWith("??")`, arbitrary substring filter, invented
decorated-name grammar or formatted-log parser was implemented as a production
contract. The isolated attribution worktree remains unused and clean at the
inspected base. No extra profiling, symbol lookup or fixture execution was
performed for this conclusion.

**Chosen direction, pending separate feasibility:** obtain typed resolver
provenance while identity, requested RVA and range status are known, and retain
it **before name interning/aggregation**. Its unknown/name-only/questionable/
verified distinctions must be explicit; a valid-looking name alone is not proof.
The feasibility work must establish what the existing dependency can expose or
what upstream API work is necessary—it must not claim such fields already exist.
Do not substitute parsing `NOT IN RANGE` text or a custom production PDB parser.
No external MCP schema/tool change or production fix is authorized by this note.

## Minimal next work, separately authorized

Do not start another profiling capture merely to retry symbol discovery.

1. **Use the measured phase-4 results, not another discovery retry.** The approved
   ordinary .NET route completed both paths. Preserve prior policy/launcher
   failures and do not relax the age matcher on the basis of Info age alone.
2. **Scope typed resolver-provenance feasibility before interning**, backed by
   owned native fixtures. The rendered-name-only fix is held: do not reject `??`
   heuristically or parse formatted warning logs as the production contract.
   Establish a typed upstream boundary before proposing implementation.
3. **Design bounded per-module provenance** before production changes: exact image
   identity/load base; requested/matched PDB identity and relevant ages; permitted
   source/cache and lookup outcome; retained PC/RVA counts and sample weights;
   omitted counts/caps; name-only versus verified-range evidence. Explicitly mark
   metadata unavailable when the API cannot supply it.
4. **Require matching identity plus proven containing range** for strict function
   attribution. Keep ambiguous, zero-length, questionable and out-of-range cases
   unresolved. Never infer an end from the next public symbol or merge solely by
   display name; preserve aliases and discontiguous ranges. No speculative custom
   PDB parser is proposed for production.

Suggested outcome vocabulary for that future design:

| Outcome | Required interpretation |
|---|---|
| Unattempted | Missing identity, no permitted source, or disabled lookup |
| Unavailable | Authoritative negative for the exact identity/source; not a timeout or redirect |
| Failed | Network/timeout/permission/malformed/mismatch or policy failure |
| Partial | Symbols obtained, but only some names/ranges or sampled addresses established |

### Durable regression matrix

Generate a small **repository-owned native PE/PDB fixture**, not committed
binaries or dependence on a private ICU name:

- Correct GUID/DBI age with newer Info age; wrong GUID/DBI age and stale same-name PDB.
- Start/interior/last byte positive; excluded end, padding and interfunction gap negative.
- Missing/partial/public-only and zero-length symbols; `??` names.
- Aliases, identical names at different starts, split/chained ranges.
- No permitted source versus exact-key negative versus timeout/permission.
- Merge only the same verified function identity/range; preserve exclusive sample
  and module totals when unresolved; never credit a resolved managed ancestor
  with unresolved native **exclusive** samples.
- Bound retained diagnostics/address inventories and account for omitted evidence.

Existing culture diagnostic tests already protect raw candidates, the unchanged
15% gate and no native-to-managed exclusive promotion. Native fixture coverage
would complement—not redefine—the real culture lookup ownership control.

Any later sampled-coverage experiment must predeclare a reviewed revision, exact
authorized Windows x64 environment, finite capture count (for example one
eight-second capture) and zero retries. Preflight the **actually loaded** image;
retain base/identity/exact lookup outcome and bounded weighted PC/RVA inventory.
Report distinct-address and sample-weighted verified-range coverage separately,
and distinguish exclusive leaves from all-stack frames.

No new metadata is claimed to exist today. Future exposed-schema work must budget
the tool catalog: the inspected full catalog was **281,048 bytes** against the
**282,000-byte cap**, only **952 bytes** of margin. No tool/schema growth is part
of this research slice.

## Evidence retention and validation limits

Session artifacts retain the full researcher handoff, all predeclared plans,
`phase2-download.json`, `phase3-download.json`, the launcher error/count record,
stopped-batch explanations and unexecuted PowerShell drafts. Phase 4 additionally
retains source/configuration hashes, build output, actual native API/runtime/
identity/name/log results and the supervisor record. Session-only DLL/PDB/harness
binaries are not committed. Phases 2–3 had no build/test run; phase 4 built and ran
only its offline harness. No profiling, installation, elevation, host-policy
change, workflow dispatch or production change occurred. The completed native
library probes do not supply historical exact-image evidence or full-capture
validation.

[^windows-icu]: [Windows combined ICU distribution, immutable documentation](https://github.com/MicrosoftDocs/win32/blob/e103fa4e8810bd8d42c4777e17081e24dbe62dbd/desktop-src/Intl/international-components-for-unicode--icu-.md#L1130-L1149).
[^pdb-guid]: [Microsoft PDB GUID validation](https://github.com/microsoft/microsoft-pdb/blob/805655a28bd8198004be2ac27e6e0290121a5e89/PDB/dbi/pdb.cpp#L826-L833).
[^pdb-age]: [Microsoft image/PDB/DBI age checks and explanatory comment](https://github.com/microsoft/microsoft-pdb/blob/805655a28bd8198004be2ac27e6e0290121a5e89/PDB/dbi/pdb.cpp#L848-L883).
[^pdata]: [x64 runtime-function start/end/unwind metadata](https://github.com/MicrosoftDocs/cpp-docs/blob/4865d418fe500c109fa14dd1e8040ad4c8b492a0/docs/build/exception-handling-x64.md#L12-L30).
[^local-match]: [TraceEvent local-file GUID/age equality](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/SymbolReader.cs#L1148-L1173).
[^indexed]: [TraceEvent indexed source/cache selection](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/SymbolReader.cs#L245-L272).
[^open]: [Public filename-based PDB opening](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/SymbolReader.cs#L655-L711).
[^dia]: [Native DIA construction](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/NativeSymbolModule.cs#L1020-L1058).
[^dia-age]: [DIA-exposed GUID/age properties](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/NativeSymbolModule.cs#L432-L455).
[^questionable]: [Zero-length and out-of-range naming](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/NativeSymbolModule.cs#L57-L94).
[^intern]: [TraceLog name/method-index interning](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/TraceLog.cs#L9038-L9084).
[^interface]: [Public name/start-only lookup interface](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/ISymbolLookup.cs#L4-L8).
[^path-policy]: [Cache derivation](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/SymbolPath.cs#L174-L202), [remote-path classification](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/SymbolPath.cs#L343-L365), and [CacheOnly directory gating](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/SymbolReader.cs#L264-L280).
[^undecoration]: [Undecoration can replace the marker-bearing string; fallback truncation](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/NativeSymbolModule.cs#L96-L120).
[^merged-name]: [Merged-assembly name transformation and assembly prefix](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/Symbols/NativeSymbolModule.cs#L129-L170).
[^method-store]: [TraceMethods stores rendered name, module and token/RVA](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/TraceLog.cs#L10164-L10168).
[^full-name]: [TraceCodeAddress.FullMethodName delegates to stored method name](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/TraceLog.cs#L9862-L9890), [direct stored-string return](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/TraceLog.cs#L10094-L10103).
[^method-kind]: [Managed token/native RVA distinction, without validity status](https://github.com/microsoft/perfview/blob/ffa46a1548d9ba6cdbf92be3dd4271d1de723046/src/TraceEvent/TraceLog.cs#L10035-L10076).
