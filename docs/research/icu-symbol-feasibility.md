# Windows ICU symbols: availability is not verified function attribution

**Date:** 2026-09-18. **Issue:** [#987](https://github.com/pedrosakuma/dotnet-diagnostics/issues/987),
extracted from [#985](https://github.com/pedrosakuma/dotnet-diagnostics/issues/985).
**Inspected repository:** `7bcf542b37444e7d1592a49789767ad0aeef35c7`.

## Verdict and scope

**Conditional GO for a separately reviewed offline resolver/provenance follow-up;
NO conclusion about historical sampled-function coverage.** Matching public symbols
were obtained for the current local Windows ICU image and contain corroborated
function-range metadata. Actual Windows DIA/TraceEvent lookup remains **unattempted**:
the subsequent predeclared staging batch stopped on an HTTP redirect.

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

This is an implementation-backed compatibility observation, **not a measured
answer about what DIA's `globalScope.age` returns** for this file. TraceEvent's
local-file matcher compares the DIA-exposed GUID and age for equality, while
the indexed-server/cache path follows a different selection path.[^local-match][^indexed]
Both paths need explicit native execution before changing their validation.

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
download, relaxed policy or repeated attempt was made. Native assembly loading,
DIA GUID/age behavior and excluded-end naming remain **unattempted**, not passed
or failed. A source-only check also found an invalid getter-only
`SymbolCacheDirectory` assignment in the unexecuted draft harness; that draft
is not a working reproduction script.

The existing root Release output supplied TraceEvent 3.2.2, FastSerialization and
`amd64/msdia140.dll`; no new project or package was installed. Public filename-based
opening requires a materialized file and native DIA.[^open][^dia] An internal
stream constructor would bypass part of the identity path being investigated.
Before staging, only three conventional cache locations had been checked:
`C:\symbols\private`, `C:\symbols`, and `C:\symcache`, each with the exact indexed
PDB suffix. Their misses were not an exhaustive cache search.

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
means verified range.” They are **not evidence of historical misnaming**. Even a
future successful three-address library probe would not validate full ETL
aggregation or sampled-address coverage.

## Minimal next work, separately authorized

Do not start another profiling capture merely to retry symbol discovery.

1. **Authorize a new finite staging policy if desired.** Define allowed redirect
   handling/destinations, timeout and byte caps before another GET; verify the
   final file hash and current image again. The failed no-redirect batch stays
   failed. Correct the unexecuted harness using supported public cache/path APIs.
2. **Execute the two offline library paths** and retain path, bounded diagnostics,
   exceptions, DIA GUID/age and name/start outputs. Do not claim range verification
   from fields the API does not supply.
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

Session artifacts retain the full researcher handoff, predeclared plan,
`phase2-download.json`, stopped-batch explanation and unexecuted draft scripts.
No native DLL/PDB is committed. No profiling, build/test run, installation,
elevation, host-policy change, workflow dispatch or production change occurred
in phase 2. Documentation review cannot substitute for the still-unattempted
native library probes or historical exact-image evidence.

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
