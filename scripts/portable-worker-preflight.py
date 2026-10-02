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


SCHEMA = "dotnet-diagnostics/portable-worker-preflight/v2"
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
RID_CONFIG = {
    "linux-x64": {
        "machines": {"x86_64", "amd64"},
        "elf_machine": "Advanced Micro Devices X86-64",
        "interpreter": "/lib64/ld-linux-x86-64.so.2",
        "loader_dependency": "ld-linux-x86-64.so.2",
    },
    "linux-arm64": {
        "machines": {"aarch64", "arm64"},
        "elf_machine": "AArch64",
        "interpreter": "/lib/ld-linux-aarch64.so.1",
        "loader_dependency": "ld-linux-aarch64.so.1",
    },
}
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


def path_parts(path: Path) -> tuple[str, ...]:
    normalized = Path(os.path.abspath(path))
    if normalized != path:
        raise ValueError(f"{path} must be an absolute normalized path")
    return normalized.parts[1:]


def require_trusted_owner(path: Path, info: os.stat_result, trusted_uids: set[int]) -> None:
    if info.st_uid not in trusted_uids:
        expected = ", ".join(str(uid) for uid in sorted(trusted_uids))
        raise ValueError(f"{path} owner UID {info.st_uid} is not trusted; expected one of {expected}")
    if info.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
        raise ValueError(f"{path} is writable by group or other users")


def runtime_can_execute(info: os.stat_result, runtime_uid: int) -> bool:
    if info.st_uid == runtime_uid:
        return bool(info.st_mode & stat.S_IXUSR)
    return bool(info.st_mode & stat.S_IXOTH)


def inspect_path_without_symlinks(path: Path) -> list[tuple[Path, os.stat_result]]:
    components = path_parts(path)
    descriptor = os.open("/", os.O_RDONLY | os.O_DIRECTORY)
    walked = [(Path("/"), os.fstat(descriptor))]
    current = Path("/")
    try:
        for component in components:
            current /= component
            info = os.stat(component, dir_fd=descriptor, follow_symlinks=False)
            if stat.S_ISLNK(info.st_mode):
                raise ValueError(f"{current} must not be a symbolic link")
            if not stat.S_ISDIR(info.st_mode):
                raise ValueError(f"{current} must be a directory")
            next_descriptor = os.open(
                component,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                dir_fd=descriptor,
            )
            opened_info = os.fstat(next_descriptor)
            if (info.st_dev, info.st_ino) != (opened_info.st_dev, opened_info.st_ino):
                os.close(next_descriptor)
                raise ValueError(f"{current} changed while its trust was being validated")
            os.close(descriptor)
            descriptor = next_descriptor
            walked.append((current, opened_info))
    finally:
        os.close(descriptor)
    return walked


def require_trusted_path(
    path: Path,
    stop: Path,
    trusted_uids: set[int],
    runtime_uid: int | None = None,
) -> None:
    path_components = path_parts(path)
    stop_components = path_parts(stop)
    if path_components[:len(stop_components)] != stop_components:
        raise ValueError(f"{path} is outside trusted root {stop}")
    walked = inspect_path_without_symlinks(path)
    trusted_start = len(stop_components)
    for index, (component, info) in enumerate(walked):
        if runtime_uid is not None and not runtime_can_execute(info, runtime_uid):
            raise ValueError(
                f"Intended runtime UID {runtime_uid} cannot traverse directory {component}"
            )
        if index >= trusted_start:
            require_trusted_owner(component, info, trusted_uids)


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


def glibc_versions(text: str) -> list[tuple[int, ...]]:
    return [
        tuple(map(int, match.split(".")))
        for match in re.findall(r"GLIBC_(\d+(?:\.\d+)+)", text)
    ]


def format_version(version: tuple[int, ...]) -> str:
    return ".".join(str(component) for component in version)


def validate_elf(worker: Path, library: Path, executable: str, rid: str = "linux-x64") -> dict[str, object]:
    worker_text = readelf(worker, executable)
    library_text = readelf(library, executable)
    expected = RID_CONFIG[rid]
    if "ELF64" not in worker_text or expected["elf_machine"] not in worker_text:
        raise ValueError(f"Worker must be an ELF64 {rid} binary")
    interpreter = re.search(r"Requesting program interpreter:\s*([^\]]+)", worker_text)
    if interpreter is None or interpreter.group(1) != expected["interpreter"]:
        raise ValueError("Worker requires an unexpected ELF interpreter")
    dependencies = sorted(set(re.findall(r"Shared library: \[([^\]]+)\]", worker_text)))
    allowed_worker_dependencies = {
        "libc.so.6",
        "libm.so.6",
        expected["loader_dependency"],
    }
    unexpected = sorted(set(dependencies) - allowed_worker_dependencies)
    if unexpected:
        raise ValueError(f"Worker has unexpected shared libraries: {', '.join(unexpected)}")
    versions = glibc_versions(worker_text)
    maximum = max(versions, default=(0, 0))
    if maximum > (2, 34):
        raise ValueError(f"Worker requires unsupported GLIBC_{format_version(maximum)}")
    if "ELF64" not in library_text or expected["elf_machine"] not in library_text:
        raise ValueError(f"SQLite sidecar must be an ELF64 {rid} library")
    library_type = re.search(r"^\s*Type:\s+(\S+)", library_text, re.MULTILINE)
    if library_type is None or library_type.group(1) != "DYN":
        raise ValueError("SQLite sidecar must have ELF type DYN")
    library_interpreter = re.search(
        r"Requesting program interpreter:\s*([^\]]+)",
        library_text,
    )
    if library_interpreter is not None:
        raise ValueError("SQLite sidecar must not request a program interpreter")
    library_dependencies = sorted(
        set(re.findall(r"Shared library: \[([^\]]+)\]", library_text))
    )
    allowed_library_dependencies = {
        "libc.so.6",
        "libdl.so.2",
        "libm.so.6",
        "libpthread.so.0",
        expected["loader_dependency"],
    }
    unexpected_library_dependencies = sorted(
        set(library_dependencies) - allowed_library_dependencies
    )
    if unexpected_library_dependencies:
        raise ValueError(
            "SQLite sidecar has unexpected shared libraries: "
            + ", ".join(unexpected_library_dependencies)
        )
    library_versions = glibc_versions(library_text)
    maximum_library = max(library_versions, default=(0, 0))
    if maximum_library > (2, 34):
        raise ValueError(
            "SQLite sidecar requires unsupported "
            f"GLIBC_{format_version(maximum_library)}"
        )
    return {
        "workerInterpreter": interpreter.group(1),
        "workerDependencies": dependencies,
        "maximumWorkerGlibc": format_version(maximum),
        "sqliteType": library_type.group(1),
        "sqliteInterpreter": None,
        "sqliteDependencies": library_dependencies,
        "maximumSqliteGlibc": format_version(maximum_library),
    }


def installed_ownership_policy(owner_model: str, runtime_uid: int) -> tuple[set[int], int]:
    if owner_model == "root-owned":
        return {0}, 0
    if owner_model == "runtime-owned":
        return {0, runtime_uid}, runtime_uid
    raise ValueError(f"Unsupported installed owner model: {owner_model}")


def validate_directory(
    directory: Path,
    trusted_root: Path,
    expected_hashes: dict[str, str],
    allow_producer_metadata: bool,
    trusted_uids: set[int],
    runtime_uid: int | None = None,
    required_owner_uid: int | None = None,
) -> dict[str, object]:
    if not directory.is_absolute() or not trusted_root.is_absolute():
        raise ValueError("Asset and trusted-root paths must be absolute")
    require_trusted_path(directory, trusted_root, trusted_uids, runtime_uid)
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
    for name in sorted(actual):
        path = directory / name
        info = require_regular(path)
        require_trusted_owner(path, info, trusted_uids)
        if required_owner_uid is not None and info.st_uid != required_owner_uid:
            raise ValueError(
                f"{path} owner UID {info.st_uid} does not match required owner UID "
                f"{required_owner_uid}"
            )
        modes[name] = format(stat.S_IMODE(info.st_mode), "04o")
        expected = expected_hashes.get(name)
        if expected is not None and digest(path) != expected:
            raise ValueError(f"{path} does not match producer provenance")
    worker = directory / "capture-worker"
    worker_info = worker.stat()
    if runtime_uid is None and not worker_info.st_mode & stat.S_IXUSR:
        raise ValueError("capture-worker is not executable by its owner")
    if runtime_uid is not None and not runtime_can_execute(worker_info, runtime_uid):
        raise ValueError(
            f"capture-worker is not executable by intended runtime UID {runtime_uid}"
        )
    return {"path": str(directory), "modes": modes, "ownerUid": worker_info.st_uid}


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
    parser.add_argument("--rid", required=True, choices=sorted(RID_CONFIG))
    parser.add_argument("--assets-dir", required=True, type=Path)
    parser.add_argument("--trusted-root", required=True, type=Path)
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--expected-revision", required=True)
    parser.add_argument("--installed-dir", action="append", default=[], type=Path)
    parser.add_argument("--runtime-uid", type=int)
    parser.add_argument(
        "--installed-owner-model",
        choices=("runtime-owned", "root-owned"),
    )
    parser.add_argument("--probe-kernel", action="store_true")
    parser.add_argument("--readelf", default="readelf")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    if sys.platform != "linux" or platform.machine() not in RID_CONFIG[args.rid]["machines"]:
        raise ValueError(f"Portable import preflight for {args.rid} requires its native Linux architecture")
    for path in (args.assets_dir, args.trusted_root, args.repo_root):
        if not path.is_absolute():
            raise ValueError("All input paths must be absolute")
    if bool(args.installed_dir) != (args.runtime_uid is not None):
        raise ValueError("--installed-dir and --runtime-uid must be provided together")
    if not args.installed_dir and args.installed_owner_model is not None:
        raise ValueError("--installed-owner-model requires --installed-dir and --runtime-uid")

    require_trusted_path(
        args.assets_dir,
        args.trusted_root,
        {0, os.geteuid()},
    )
    provenance_path = args.assets_dir / "provenance.xml"
    require_regular(provenance_path)
    if provenance_path.stat().st_size > 32 * 1024:
        raise ValueError("Portable worker provenance exceeds 32 KiB")
    root = ET.parse(provenance_path).getroot()
    if root.tag != "PortableCaptureWorker" or root.attrib != {
        "version": "1",
        "kind": "container",
        "rid": args.rid,
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
        trusted_uids={0, os.geteuid()},
    )
    installed_owner_model = args.installed_owner_model or "runtime-owned"
    installed_trusted_uids, required_owner_uid = installed_ownership_policy(
        installed_owner_model,
        args.runtime_uid,
    )
    installed = [
        validate_directory(
            path,
            path.parent,
            expected_hashes,
            allow_producer_metadata=False,
            trusted_uids=installed_trusted_uids,
            runtime_uid=args.runtime_uid,
            required_owner_uid=required_owner_uid,
        )
        for path in args.installed_dir
    ]
    elf = validate_elf(
        args.assets_dir / "capture-worker",
        args.assets_dir / "libe_sqlite3.so",
        args.readelf,
        args.rid,
    )
    environment = {
        "DOTNET_DIAGNOSTICS_IMPORT_WORKER": str(args.assets_dir / "capture-worker"),
        "DOTNET_DIAGNOSTICS_SQLITE_LIBRARY": str(args.assets_dir / "libe_sqlite3.so"),
        "DOTNET_DIAGNOSTICS_HISTORICAL_ACCEPTANCE": "1",
    }
    report = {
        "schema": SCHEMA,
        "rid": args.rid,
        "producerRevision": revision,
        "producer": producer,
        "installedPackageValidation": {
            "status": "validated" if installed else "not-requested",
            "runtimeUid": args.runtime_uid if installed else None,
            "ownerModel": installed_owner_model if installed else None,
            "copies": installed,
        },
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
