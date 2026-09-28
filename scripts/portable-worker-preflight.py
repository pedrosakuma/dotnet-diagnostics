#!/usr/bin/env python3
"""Validate produced portable-import assets without executing the worker."""

from __future__ import annotations

import argparse
import ctypes
import errno
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET


SCHEMA = "dotnet-diagnostics/portable-worker-preflight/v1"
ACCEPTANCE_FILTER = (
    "FullyQualifiedName="
    "DotnetDiagnostics.Core.Tests.HistoricalComparisonAcceptanceTests."
    "DifferentBundlesCompareAfterSourceDeletionAndDestinationReopen"
)
REQUIRED_FILES = (
    "capture-worker",
    "libe_sqlite3.so",
    "provenance.xml",
    "sqlite-LICENSE.txt",
    "worker-LICENSE.txt",
)
PRODUCER_METADATA = (
    "compiler.txt",
    "libc.txt",
    "toolchain-packages.txt",
    "worker-elf.txt",
)
HASH_FIELDS = {
    "capture-worker": "WorkerSha256",
    "libe_sqlite3.so": "SqliteSha256",
}
SOURCE_FIELDS = {
    "src/DotnetDiagnostics.Core/Captures/Isolation/native/capture_worker.c": "SourceSha256",
    "src/DotnetDiagnostics.Core/Captures/Isolation/native/sqlite_admission.h": "AdmissionSha256",
    "src/DotnetDiagnostics.Core/Captures/Isolation/native/sqlite_rebuild.h": "RebuildSha256",
}


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            value.update(chunk)
    return value.hexdigest()


def require_regular(path: Path) -> os.stat_result:
    if path.is_symlink():
        raise ValueError(f"{path} must not be a symbolic link")
    try:
        info = path.stat()
    except FileNotFoundError as error:
        raise ValueError(f"Missing required asset: {path}") from error
    if not stat.S_ISREG(info.st_mode):
        raise ValueError(f"{path} must be a regular file")
    return info


def require_trusted_path(path: Path, stop: Path) -> None:
    current = path
    while True:
        info = current.stat()
        if info.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise ValueError(f"{current} is writable by group or other users")
        if current == stop:
            return
        if stop not in current.parents:
            raise ValueError(f"{path} is outside trusted root {stop}")
        current = current.parent


def readelf(path: Path, executable: str) -> str:
    completed = subprocess.run(
        [executable, "-h", "-l", "-d", "--version-info", str(path)],
        check=False,
        capture_output=True,
        text=True,
        timeout=15,
    )
    if completed.returncode != 0:
        raise ValueError(f"readelf rejected {path}: {completed.stderr.strip()}")
    return completed.stdout


def validate_elf(worker: Path, library: Path, executable: str) -> dict[str, object]:
    worker_text = readelf(worker, executable)
    library_text = readelf(library, executable)
    if "ELF64" not in worker_text or "Advanced Micro Devices X86-64" not in worker_text:
        raise ValueError("Worker must be an ELF64 x86-64 binary")
    interpreter = re.search(r"Requesting program interpreter:\s*([^\]]+)", worker_text)
    if interpreter is None or interpreter.group(1) != "/lib64/ld-linux-x86-64.so.2":
        raise ValueError("Worker requires an unexpected ELF interpreter")
    dependencies = sorted(set(re.findall(r"Shared library: \[([^\]]+)\]", worker_text)))
    unexpected = sorted(set(dependencies) - {"libc.so.6", "libm.so.6"})
    if unexpected:
        raise ValueError(f"Worker has unexpected shared libraries: {', '.join(unexpected)}")
    versions = [tuple(map(int, match.split("."))) for match in re.findall(r"GLIBC_(\d+\.\d+)", worker_text)]
    maximum = max(versions, default=(0, 0))
    if maximum > (2, 34):
        raise ValueError(f"Worker requires unsupported GLIBC_{maximum[0]}.{maximum[1]}")
    if "ELF64" not in library_text or "Advanced Micro Devices X86-64" not in library_text:
        raise ValueError("SQLite sidecar must be an ELF64 x86-64 library")
    return {
        "workerInterpreter": interpreter.group(1),
        "workerDependencies": dependencies,
        "maximumWorkerGlibc": f"{maximum[0]}.{maximum[1]}",
    }


def validate_directory(
    directory: Path,
    trusted_root: Path,
    expected_hashes: dict[str, str],
    allow_producer_metadata: bool,
    runtime_uid: int | None = None,
) -> dict[str, object]:
    if not directory.is_absolute() or not trusted_root.is_absolute():
        raise ValueError("Asset and trusted-root paths must be absolute")
    if directory.is_symlink() or trusted_root.is_symlink():
        raise ValueError("Asset and trusted-root paths must not be symbolic links")
    require_trusted_path(directory, trusted_root)
    allowed = set(REQUIRED_FILES)
    if allow_producer_metadata:
        allowed.update(PRODUCER_METADATA)
    actual = {entry.name for entry in directory.iterdir()}
    missing = sorted(set(REQUIRED_FILES) - actual)
    extra = sorted(actual - allowed)
    if missing:
        raise ValueError(f"Missing required assets: {', '.join(missing)}")
    if extra:
        raise ValueError(f"Unexpected assets: {', '.join(extra)}")
    modes: dict[str, str] = {}
    for name in REQUIRED_FILES:
        path = directory / name
        info = require_regular(path)
        if info.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise ValueError(f"{path} is writable by group or other users")
        modes[name] = format(stat.S_IMODE(info.st_mode), "04o")
        expected = expected_hashes.get(name)
        if expected is not None and digest(path) != expected:
            raise ValueError(f"{path} does not match producer provenance")
    worker = directory / "capture-worker"
    if not worker.stat().st_mode & stat.S_IXUSR:
        raise ValueError("capture-worker is not executable by its owner")
    if runtime_uid is not None and worker.stat().st_uid != runtime_uid:
        raise ValueError(
            f"{worker} owner UID {worker.stat().st_uid} does not match intended runtime UID {runtime_uid}"
        )
    return {"path": str(directory), "modes": modes, "ownerUid": worker.stat().st_uid}


def probe_kernel() -> dict[str, object]:
    libc = ctypes.CDLL(None, use_errno=True)
    landlock_create_ruleset = 444
    landlock_create_ruleset_version = 1
    abi = libc.syscall(landlock_create_ruleset, 0, 0, landlock_create_ruleset_version)
    if abi < 0:
        code = ctypes.get_errno()
        if code in (errno.ENOSYS, errno.EOPNOTSUPP, errno.EINVAL):
            raise ValueError(f"Landlock ABI query is unavailable: errno {code}")
        raise OSError(code, os.strerror(code))
    if abi < 3:
        raise ValueError(f"Landlock ABI {abi} is below the required ABI 3")
    pr_get_seccomp = 21
    seccomp_mode = libc.prctl(pr_get_seccomp, 0, 0, 0, 0)
    if seccomp_mode < 0:
        code = ctypes.get_errno()
        if code in (errno.ENOSYS, errno.EINVAL):
            raise ValueError(f"seccomp query is unavailable: errno {code}")
        raise OSError(code, os.strerror(code))
    return {"landlockAbi": abi, "currentProcessSeccompMode": seccomp_mode}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets-dir", required=True, type=Path)
    parser.add_argument("--trusted-root", required=True, type=Path)
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--expected-revision", required=True)
    parser.add_argument("--installed-dir", action="append", default=[], type=Path)
    parser.add_argument("--runtime-uid", type=int, default=os.geteuid())
    parser.add_argument("--probe-kernel", action="store_true")
    parser.add_argument("--readelf", default="readelf")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    if sys.platform != "linux" or platform.machine() != "x86_64":
        raise ValueError("Portable import preflight supports Linux x86-64 only")
    for path in (args.assets_dir, args.trusted_root, args.repo_root):
        if not path.is_absolute():
            raise ValueError("All input paths must be absolute")

    provenance_path = args.assets_dir / "provenance.xml"
    require_regular(provenance_path)
    if provenance_path.stat().st_size > 32 * 1024:
        raise ValueError("Portable worker provenance exceeds 32 KiB")
    root = ET.parse(provenance_path).getroot()
    if root.tag != "PortableCaptureWorker" or root.attrib != {
        "version": "1",
        "kind": "container",
        "rid": "linux-x64",
    }:
        raise ValueError("Portable worker provenance identity is invalid")

    def field(name: str) -> str:
        value = root.findtext(name)
        if value is None or not value.strip():
            raise ValueError(f"Portable worker provenance lacks {name}")
        return value.strip()

    revision = field("Revision")
    if revision != args.expected_revision:
        raise ValueError(f"Producer revision {revision} does not match {args.expected_revision}")
    if field("SqlitePackage") != "SQLitePCLRaw.lib.e_sqlite3/3.53.3":
        raise ValueError("Portable worker uses an unexpected SQLite package")
    expected_hashes = {name: field(provenance) for name, provenance in HASH_FIELDS.items()}
    for relative, provenance in SOURCE_FIELDS.items():
        source = args.repo_root / relative
        require_regular(source)
        if digest(source) != field(provenance):
            raise ValueError(f"{relative} does not match producer provenance")

    producer = validate_directory(
        args.assets_dir,
        args.trusted_root,
        expected_hashes,
        allow_producer_metadata=True,
    )
    installed = [
        validate_directory(
            path,
            path.parent,
            expected_hashes,
            allow_producer_metadata=False,
            runtime_uid=args.runtime_uid,
        )
        for path in args.installed_dir
    ]
    elf = validate_elf(
        args.assets_dir / "capture-worker",
        args.assets_dir / "libe_sqlite3.so",
        args.readelf,
    )
    environment = {
        "DOTNET_DIAGNOSTICS_IMPORT_WORKER": str(args.assets_dir / "capture-worker"),
        "DOTNET_DIAGNOSTICS_SQLITE_LIBRARY": str(args.assets_dir / "libe_sqlite3.so"),
        "DOTNET_DIAGNOSTICS_HISTORICAL_ACCEPTANCE": "1",
    }
    report = {
        "schema": SCHEMA,
        "producerRevision": revision,
        "producer": producer,
        "installedCopies": installed,
        "intendedRuntimeUid": args.runtime_uid,
        "hashes": expected_hashes,
        "elf": elf,
        "kernel": probe_kernel() if args.probe_kernel else {"status": "not-probed"},
        "activation": environment,
        "acceptance": {
            "filter": ACCEPTANCE_FILTER,
            "outerDeadlineSeconds": 720,
            "terminationGraceSeconds": 30,
            "maximumDiagnosticBytes": 4096,
            "importExecuted": False,
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"Validated portable assets without worker execution; report={args.output}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, ET.ParseError, subprocess.SubprocessError) as error:
        print(f"portable worker preflight failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
