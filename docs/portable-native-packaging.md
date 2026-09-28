# Portable import worker packaging

This is producer and packaging preparation for #1054, not a new product, an
automatic activation path, or a platform acceptance claim. Core's import protocol,
foreign-SQLite admission, confinement and strict observation limits are unchanged.
Neither ordinary builds nor installed applications compile or download a worker.

## Activation remains explicit

Both hosts require the operator to supply **both**
`DOTNET_DIAGNOSTICS_IMPORT_WORKER` and `DOTNET_DIAGNOSTICS_SQLITE_LIBRARY`.
Missing configuration remains `ImportWorkerUnavailable`; partial configuration is
an error. Packaging does not set these variables, including in Docker. No runtime
directory search, compiler, downloader, chmod or package-cache mutation is added.

When explicitly included, the application-adjacent layout is:

```text
NativeAssets/portable-capture/linux-x64/
  capture-worker
  libe_sqlite3.so
  provenance.xml
  sqlite-LICENSE.txt
  worker-LICENSE.txt
```

For a trusted extracted publish directory `/opt/diagnostics`, an operator can set:

```bash
export DOTNET_DIAGNOSTICS_IMPORT_WORKER=/opt/diagnostics/NativeAssets/portable-capture/linux-x64/capture-worker
export DOTNET_DIAGNOSTICS_SQLITE_LIBRARY=/opt/diagnostics/NativeAssets/portable-capture/linux-x64/libe_sqlite3.so
```

Use actual absolute regular-file paths, not command shims, symlinks, source/test
output guesses, or values taken from a bundle. Keep the installation and parents
unmodifiable by untrusted users. Core retains its current path/isolation checks.
The worker explicitly loads this SQLite shared library; the static `.a` is not
a runtime dependency. The managed host retains its normal NuGet native resolution.
The separate worker copy is byte-identical to the resolved package's shared asset,
not a custom SQLite build.

## Immutable producer

`src/DotnetDiagnostics.Core/Build/PortableCaptureWorker.proj` is an explicit
producer entrypoint. It is **not** imported into ordinary application builds.
Its Python standard-library helper resolves the existing
`SQLitePCLRaw.lib.e_sqlite3/3.53.3` asset from Core's `project.assets.json`, then
compiles the existing `capture_worker.c` once in a Docker container.

The approved preparation pin is the official `gcc:14-bookworm` **linux/amd64
manifest**, not its mutable tag or multi-platform index:

```text
gcc@sha256:a689e29bc3adf4663ef9a141d23081252764d1319c63f591a027bd6fd676f4c1
```

Public registry metadata identifies GCC **14.3.0**, Debian Bookworm base
`buildpack-deps:bookworm`, and upstream source revision
`d262936418fbf062bb25907d5126a178578ab58b`.
The [pinned upstream Dockerfile](https://github.com/docker-library/gcc/blob/d262936418fbf062bb25907d5126a178578ab58b/14/Dockerfile)
installs that compiler and selects it as `cc`. Bookworm uses the glibc 2.36
family. The producer verifies `gcc -dumpfullversion` and `getconf GNU_LIBC_VERSION`,
records exact `libc6`, development-header and binutils package versions, and
records output ELF headers/dependencies/version requirements without executing
the generated worker.

Bookworm's Linux 6.1 development headers omit `LANDLOCK_ACCESS_FS_TRUNCATE`.
The worker supplies its stable [Linux 6.2 UAPI value](https://github.com/torvalds/linux/blob/v6.2/include/uapi/linux/landlock.h),
`1ULL << 14`, only when the build header does not define it, and rejects a
conflicting definition at compile time. This is header compatibility, not a
runtime fallback: Landlock ABI >=3 is still mandatory, truncation remains in the
handled-access mask, and only the existing private writable staging profile
allows it. The compiler image, runtime checks and confinement policy are unchanged.
Compile-only regression cases cover missing, matching and conflicting definitions
without producing or executing a worker.

This existing complete compiler image avoids maintaining a new compiler build or
running mutable apt installs in our producer. It is not a minimal-size runtime
image and is never shipped to consumers. Compilation runs network-disabled,
read-only except for output and bounded temporary storage, with dropped
capabilities and the invoking UID. Image retrieval is a producer-only prerequisite.
The C11/O2/warning/hardening/link flags match the existing Core-test recipe.

Provenance is bounded to **32 KiB** and binds the producer image, toolchain,
source revision and hashes of the C source and both local headers, worker hash,
exact SQLite package identity/package hash,
shared-library hash, flags and observed ELF metadata. Hashes establish identity,
not trust: verify the producing workflow/revision and protect the downloaded
artifact. The build does not treat an arbitrary self-authored manifest as
cryptographic producer authentication.

## Preparation and release gates

The nonpublishing `portable-native-packaging.yml` workflow is manually callable
or reusable. It has `contents: read`, no publishing credentials, a 15-minute job
limit and an internal tar artifact preserving Unix permissions. It does not run
the worker or any native import. Failed producer output is retained rather than
retried into success.

The equivalent producer command, **only in an approved environment with Docker
and the configured private NuGet source**:

```bash
dotnet restore src/DotnetDiagnostics.Core/DotnetDiagnostics.Core.csproj \
  --configfile "$HOME/.nuget/NuGet/NuGet.Config"
dotnet msbuild src/DotnetDiagnostics.Core/Build/PortableCaptureWorker.proj \
  -p:PortableCaptureWorkerOutput=/absolute/new/portable-worker/linux-x64
```

The output directory must not already exist. Do not reuse a failed directory or
replace the container with host compilation. No actual container build was
established by the initial local preparation: Docker was unavailable there.
That limitation was subsequently closed by the reviewed digest-pinned producer
run. Public metadata/source verification alone is still not compiler-output or
distro validation.

The retained producer evidence for revision
`a04c0410e27cf27eda7ff3ce1f3e4dd54c6fdfa5` is:

| Property | Observed value |
|---|---|
| Workflow run | `36281250444` |
| Worker size | 53,816 bytes |
| Worker SHA-256 | `b58a54e4b7822a5ba5c3af7d69dcb30d6e6108c1b2014aea5af1d1dc556ac322` |
| Archive worker mode | `0755` |
| Maximum worker GLIBC requirement | `2.34` |

These values identify the reviewed output; they do not authorize import,
publication, or substitution of an asset built from another revision.

Both host projects import the same `PortableCaptureWorker.targets`. Supply the
prepared artifact to source builds/publish/tool packing:

```bash
dotnet pack src/DotnetDiagnostics.Cli -c Release \
  -p:PortableCaptureWorkerAssetsDir=/absolute/prepared/linux-x64 \
  -p:RequirePortableCaptureWorkerAssets=true
```

The equivalent properties work for MCP. Missing files, oversized provenance,
wrong producer/package identity, or worker/library/source hash mismatch fail
before copying. `AllowPortableCaptureWorkerFixtureAssets=true` exists only for
explicit deterministic layout tests; fixture provenance **cannot** satisfy
`RequirePortableCaptureWorkerAssets=true`. Do not distribute fixture output.
These are producer MSBuild controls, not application modes or public tool APIs.
Configured assets are always copied, regardless of timestamps. Publishing or
tool packing without configuration rejects a publish directory containing old
portable sidecars; use a fresh output directory instead of implicitly reusing
or deleting those assets.
The standalone Core library package is unchanged; this preparation adds assets
to the two application hosts, not a fourth package.

Host content is excluded from single-file embedding and is included beneath
`tools/net10.0/any/` by ordinary framework-dependent tool packing. Release archives
must keep the sidecar directory beside the executable; distributing the executable
alone does not deliver portable import assets. Unsupported RID-specific outputs
do not include the Linux-x64 sidecar.

The existing release workflow gains **manual opt-in**
`include_portable_worker` (default false). When opted in, it calls the producer
once, reuses its internal artifact for both tool packages and Linux-x64 binaries,
and requires the sidecars. Existing tag-triggered releases remain unchanged until
a separately reviewed activation of release inclusion. Dispatching the release
workflow still builds and attests product artifacts; public NuGet/GitHub Release
publication remains tag-only. Use the nonpublishing preparation workflow for
initial producer validation.

When the preparation workflow is not registered on the default branch, the
registered `release.yml` offers a separate **`producer_only=true`**
manual mode (default false). This calls only the unchanged reusable producer:
`pack-tool`, `publish-binaries`, `release`, and `publish-nuget` are explicitly
skipped. The running job has `contents: read` and receives only the required
private `NUGET_CONFIG` secret for an explicit `--configfile` restore. The
configuration is staged under runner temporary storage and removed before
production or artifact upload. It produces only internal success/failure
artifacts. No product packing, attestation, tag, package publication or GitHub
Release is performed in this mode. The existing required `version` input stays
required; supply the inert value `producer-only`, which no product job consumes.

After independent review and verification of the pushed preparation branch SHA,
the dispatch body for registered workflow `release.yml` is:

```json
{
  "ref": "feature/1054-producer-only-validation",
  "inputs": {
    "version": "producer-only",
    "producer_only": true,
    "include_portable_worker": false
  }
}
```

Do not dispatch an older workflow revision lacking this gate. Before dispatch,
inspect default-branch completion triggers for downstream publishing. Verify the
resulting run's `head_sha`, read-only token permissions, skipped product jobs and
internal-artifact inventory; retain the first failure without rerunning. This
preparation mode is not release authorization. Ordinary manual and tag paths
retain their prior behavior when `producer_only` is absent or false.

Docker consumes the same already-prepared artifact; it does not compile one.
Extract it into `artifacts/portable-worker/linux-x64` in the build context and use
`--build-arg INCLUDE_PORTABLE_CAPTURE_WORKER=true`. The deny-all `.dockerignore`
admits only the five named assets. The opt-in amd64 build requires validated
assets; the default and ARM64 images do not gain import support. No activation
environment variables are baked into the image. The operator can explicitly use
`/app/NativeAssets/portable-capture/linux-x64/...` for both hosts; the CLI also
has its application-adjacent copy under `/app/cli/NativeAssets/...`.

## Validation limits

An actual SDK **10.0.201** minimal tool pack/install probe observed source/publish
mode `0755`, package entry mode `0644`, and installed sidecar mode **`0744`**,
with identical bytes. This establishes **owner-execute availability only**.
It is not exact permission preservation, a cross-UID/shared-install guarantee,
a read-only-install compatibility guarantee, or worker execution evidence.
No runtime permission repair is permitted. Validate the real packaged hosts
and intended installing/running identity before release.

Initial support remains **Linux x64 glibc only**, contingent on actual loader and
confinement availability. Musl, Windows, macOS and ARM64 are not covered.
The reviewed pinned producer worker requires no GLIBC version newer than 2.34.
The preflight independently checks the worker and SQLite sidecar ELF identity,
interpreter/dependencies and GLIBC ceiling; compatible libc alone is
insufficient. Landlock ABI >=3, seccomp, procfs and other existing runtime checks
still fail closed.

The installed global-tool probe establishes a **same-owner** support boundary:
the identity that installs the tool must also execute its `0744` worker. It does
not establish arbitrary cross-UID access. The container uses a separate model:
root-owned, non-writable application assets with traversal/execution permission
for runtime UID 10001 and writable access limited to explicit state directories.
Only the Linux/amd64 image can opt into the prepared worker; Linux/arm64 images
remain deliberately asset-free.

`scripts/portable-worker-preflight.py` validates the produced and optional
installed copies without starting `capture-worker`. It checks provenance/source
identity, hashes, trusted non-symlink ancestry, ownership and runtime access,
ELF/GLIBC compatibility, exact activation variables and the single selected
acceptance-test filter. Its report explicitly records `importExecuted: false`.
Optional Landlock/seccomp probes inspect host prerequisites without importing a
bundle.

Before release inclusion is enabled by default: verify the pinned producer
artifact and preflight report against the final revision, inspect actual
tool/publish/archive inventories, install both tools into fresh same-owner
locations, verify single-file sidecars and the non-publishing Docker variants,
and then run separately authorized native imports through both hosts. Preserve
first failures. A layout test, preflight, or clean workload-only comparison does
not resolve the retained strict watchdog acceptance risk.

Current status for #1054:

- **Verified with the actual assets from producer run `36281250444`, without an
  import:**
  - the CLI and MCP package inventories;
  - same-owner tool installs;
  - the Linux/amd64 Docker variant.

  See [the packaging checkpoint](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1054#issuecomment-5862579266).
- **One authorized Core-level two-bundle import passed** with those assets (see
  [historical comparisons](historical-comparisons.md#bounds-and-acceptance)).
  The watchdog's root cause remains unproven.
- **Not yet run with the producer assets:** native imports through the
  *installed* CLI and MCP hosts. Earlier host-level import flows used pinned
  test assets.
- **Tag-triggered releases still exclude the worker.** Release inclusion stays
  a separate, explicit decision.
