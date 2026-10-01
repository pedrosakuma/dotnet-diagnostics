"""Producer-only container compilation; never imported or run by installed products."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


SQLITE_PACKAGE = "SQLitePCLRaw.lib.e_sqlite3/3.53.3"
PRODUCER_IMAGES = {
    "linux-x64": "gcc@sha256:a689e29bc3adf4663ef9a141d23081252764d1319c63f591a027bd6fd676f4c1",
    "linux-arm64": "gcc@sha256:66035d353338cb93b64f621393dc6fecde85258651ca454f0cf36ff2639b1352",
}
SQLITE_MEMBER = "runtimes/linux-x64/native/libe_sqlite3.so"
RID_CONFIG = {
    "linux-x64": {"machine": "x86_64", "docker": "linux/amd64", "sqlite": "runtimes/linux-x64/native/libe_sqlite3.so"},
    "linux-arm64": {"machine": "aarch64", "docker": "linux/arm64", "sqlite": "runtimes/linux-arm64/native/libe_sqlite3.so"},
}
FLAGS = [
    "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror",
    "-fstack-protector-strong", "-D_FORTIFY_SOURCE=2",
    "-Wl,-z,relro,-z,now",
]


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            value.update(chunk)
    return value.hexdigest()


def sqlite_assets(assets_path, rid="linux-x64"):
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    library = assets["libraries"].get(SQLITE_PACKAGE)
    member = RID_CONFIG[rid]["sqlite"]
    if not library or member not in library["files"]:
        raise ValueError(f"Resolved assets must contain SQLitePCLRaw.lib.e_sqlite3/3.53.3 {rid} shared library")
    candidates = [
        Path(folder) / library["path"]
        for folder in assets["packageFolders"]
        if (Path(folder) / library["path"] / member).is_file()
    ]
    if len(candidates) != 1:
        raise ValueError("Expected exactly one resolved SQLite package location")
    return candidates[0] / member, candidates[0] / "LICENSE.txt", library["sha512"]


def run(command):
    subprocess.run(command, check=True, timeout=180)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True, choices=sorted(RID_CONFIG))
    parser.add_argument("--image", required=True)
    parser.add_argument("--compiler-version", required=True)
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.image != PRODUCER_IMAGES[args.rid]:
        raise ValueError(f"{args.rid} requires its reviewed immutable official GCC image")
    if sys.platform != "linux" or os.uname().machine != RID_CONFIG[args.rid]["machine"]:
        raise ValueError(f"{args.rid} production requires a native Linux {RID_CONFIG[args.rid]['machine']} host")
    repo = Path(__file__).resolve().parents[3]
    source = repo / "src/DotnetDiagnostics.Core/Captures/Isolation/native/capture_worker.c"
    sqlite, license_file, package_hash = sqlite_assets(args.assets, args.rid)
    output = args.output.absolute()
    if any("," in str(path) for path in (source.parent, output)):
        raise ValueError("Docker bind paths must not contain commas")
    if output.exists():
        raise ValueError("Output must not exist; preserve failed attempts instead of overwriting")
    os.umask(0o022)
    output.mkdir(parents=True)
    # Keep the output writable by the invoking owner, not a root-owned container artifact.
    mounts = [
        "--mount", f"type=bind,source={source.parent},target=/source,readonly",
        "--mount", f"type=bind,source={output},target=/output",
    ]
    docker_platform = RID_CONFIG[args.rid]["docker"]
    run(["docker", "pull", "--platform", docker_platform, args.image])
    script = """
set -eu
umask 022
test "$(gcc -dumpfullversion)" = "$1"
test "$(getconf GNU_LIBC_VERSION)" = "glibc 2.36"
gcc -dumpfullversion > /output/compiler.txt
getconf GNU_LIBC_VERSION > /output/libc.txt
dpkg-query -W libc6 libc6-dev linux-libc-dev binutils > /output/toolchain-packages.txt
shift
gcc "$@" /source/capture_worker.c -ldl -Wl,--no-as-needed -lm -Wl,--as-needed -o /output/capture-worker
readelf -h -l -d --version-info /output/capture-worker > /output/worker-elf.txt
"""
    run(["docker", "run", "--rm", "--platform", docker_platform, "--network", "none",
         "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
         "--user", f"{os.getuid()}:{os.getgid()}", "--tmpfs", "/tmp:rw,nosuid,size=128m",
         *mounts, args.image, "sh", "-c", script, "producer", args.compiler_version, *FLAGS])
    worker = output / "capture-worker"
    if not worker.is_file() or not os.access(worker, os.X_OK) or worker.stat().st_size > 1024 * 1024:
        raise ValueError("Producer output is absent, nonexecutable or over 1 MiB")
    shutil.copyfile(sqlite, output / "libe_sqlite3.so")
    shutil.copyfile(license_file, output / "sqlite-LICENSE.txt")
    shutil.copyfile(repo / "LICENSE", output / "worker-LICENSE.txt")
    revision = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
    provenance = ET.Element("PortableCaptureWorker", version="1", kind="container", rid=args.rid)
    values = {
        "Image": args.image, "Compiler": args.compiler_version, "Libc": "glibc 2.36",
        "Revision": revision, "SourceSha256": digest(source),
        "AdmissionSha256": digest(source.with_name("sqlite_admission.h")),
        "RebuildSha256": digest(source.with_name("sqlite_rebuild.h")),
        "WorkerSha256": digest(worker), "SqliteSha256": digest(output / "libe_sqlite3.so"),
        "SqlitePackage": SQLITE_PACKAGE, "SqlitePackageSha512": package_hash,
        "Flags": " ".join(FLAGS + ["-ldl", "-Wl,--no-as-needed", "-lm", "-Wl,--as-needed"]),
        "CompilerPackages": (output / "toolchain-packages.txt").read_text(),
        "WorkerElf": (output / "worker-elf.txt").read_text(),
    }
    for key, value in values.items():
        ET.SubElement(provenance, key).text = value
    encoded = ET.tostring(provenance, encoding="utf-8", xml_declaration=True)
    if len(encoded) > 32 * 1024:
        raise ValueError("Provenance exceeds 32 KiB")
    (output / "provenance.xml").write_bytes(encoded)
    print(f"Prepared {output}; no worker execution performed.")


if __name__ == "__main__":
    main()
