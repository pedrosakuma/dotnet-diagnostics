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

The equivalent producer command, **only in an approved environment with Docker**:

```bash
dotnet restore src/DotnetDiagnostics.Core/DotnetDiagnostics.Core.csproj
dotnet msbuild src/DotnetDiagnostics.Core/Build/PortableCaptureWorker.proj \
  -p:PortableCaptureWorkerOutput=/absolute/new/portable-worker/linux-x64
```

The output directory must not already exist. Do not reuse a failed directory or
replace the container with host compilation. No actual container build was
established by the initial local preparation: Docker was unavailable there.
Public metadata/source verification is not compiler-output or distro validation.

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
The older host-built test worker required GLIBC_2.38 and its SQLite asset required
GLIBC_2.34; those measurements do not describe the not-yet-built pinned producer
output. Inspect its recorded ELF requirements and run independently scheduled
functional acceptance. Landlock ABI >=3, seccomp, procfs and other existing
runtime checks still fail closed; compatible libc alone is insufficient.

Before release inclusion is enabled by default: execute the approved producer,
verify its provenance and hashes, inspect actual tool/publish/archive inventories,
install both tools into fresh locations, verify owner executable permissions,
test single-file sidecars and Docker, and then run separately scheduled native
imports through both hosts. Preserve first failures. A layout test or clean
functional run does not resolve the retained strict watchdog acceptance risk.
