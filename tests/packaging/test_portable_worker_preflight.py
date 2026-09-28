"""Deterministic tests for the non-executing produced-worker preflight."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import stat
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/portable-worker-preflight.py"
SPEC = importlib.util.spec_from_file_location("portable_worker_preflight", SCRIPT)
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)
SOURCE_FILES = {
    "src/DotnetDiagnostics.Core/Captures/Isolation/native/capture_worker.c": "SourceSha256",
    "src/DotnetDiagnostics.Core/Captures/Isolation/native/sqlite_admission.h": "AdmissionSha256",
    "src/DotnetDiagnostics.Core/Captures/Isolation/native/sqlite_rebuild.h": "RebuildSha256",
}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


@unittest.skipUnless(platform.system() == "Linux" and platform.machine() == "x86_64",
                     "Portable import preflight supports Linux x86-64 only")
class PortableWorkerPreflightTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="portable preflight ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.assets = self.root / "producer" / "linux-x64"
        self.assets.mkdir(parents=True)
        self.installed = self.root / "installed" / "linux-x64"
        self.installed.mkdir(parents=True)
        self.revision = "a" * 40
        for directory in (self.assets, self.installed):
            (directory / "capture-worker").write_bytes(b"worker")
            (directory / "libe_sqlite3.so").write_bytes(b"sqlite")
            (directory / "sqlite-LICENSE.txt").write_text("sqlite", encoding="utf-8")
            (directory / "worker-LICENSE.txt").write_text("worker", encoding="utf-8")
            os.chmod(directory / "capture-worker", 0o755)
        provenance = ET.Element("PortableCaptureWorker", version="1", kind="container", rid="linux-x64")
        values = {
            "Revision": self.revision,
            "WorkerSha256": digest(self.assets / "capture-worker"),
            "SqliteSha256": digest(self.assets / "libe_sqlite3.so"),
            "SqlitePackage": "SQLitePCLRaw.lib.e_sqlite3/3.53.3",
        }
        for relative, name in SOURCE_FILES.items():
            values[name] = digest(ROOT / relative)
        for key, value in values.items():
            ET.SubElement(provenance, key).text = value
        ET.ElementTree(provenance).write(self.assets / "provenance.xml")
        (self.installed / "provenance.xml").write_bytes((self.assets / "provenance.xml").read_bytes())
        self.readelf = self.root / "readelf"
        self.readelf.write_text(
            "#!/bin/sh\n"
            "for last do :; done\n"
            "cat <<'EOF'\n"
            "Class: ELF64\n"
            "Machine: Advanced Micro Devices X86-64\n"
            "EOF\n"
            "case \"$last\" in\n"
            "*/libe_sqlite3.so)\n"
            "cat <<'EOF'\n"
            "Type: DYN (Shared object file)\n"
            "Shared library: [libm.so.6]\n"
            "Shared library: [libc.so.6]\n"
            "Name: GLIBC_2.34\n"
            "EOF\n"
            ";;\n"
            "*)\n"
            "cat <<'EOF'\n"
            "[Requesting program interpreter: /lib64/ld-linux-x86-64.so.2]\n"
            "Shared library: [libm.so.6]\n"
            "Shared library: [libc.so.6]\n"
            "Name: GLIBC_2.34\n"
            "EOF\n"
            ";;\n"
            "esac\n",
            encoding="utf-8",
        )
        os.chmod(self.readelf, 0o755)
        self.output = self.root / "preflight.json"

    def run_preflight(self, *extra, installed=True):
        installed_arguments = (
            [
                "--installed-dir", str(self.installed),
                "--runtime-uid", str(os.geteuid()),
            ]
            if installed
            else []
        )
        return subprocess.run(
            [
                "python3", str(SCRIPT),
                "--assets-dir", str(self.assets),
                "--trusted-root", str(self.root),
                "--repo-root", str(ROOT),
                "--expected-revision", self.revision,
                *installed_arguments,
                "--readelf", str(self.readelf),
                "--output", str(self.output),
                *extra,
            ],
            text=True,
            capture_output=True,
            timeout=30,
        )

    def test_validates_identity_installed_copy_and_nonexecuting_acceptance_contract(self):
        result = self.run_preflight()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        report = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual("dotnet-diagnostics/portable-worker-preflight/v2", report["schema"])
        self.assertFalse(report["acceptance"]["importExecuted"])
        self.assertEqual(720, report["acceptance"]["outerDeadlineSeconds"])
        self.assertEqual(30, report["acceptance"]["terminationGraceSeconds"])
        self.assertEqual(4096, report["acceptance"]["maximumDiagnosticBytes"])
        self.assertIn("DifferentBundlesCompareAfterSourceDeletionAndDestinationReopen",
                      report["acceptance"]["filter"])
        self.assertEqual(str(self.assets / "capture-worker"),
                         report["activation"]["DOTNET_DIAGNOSTICS_IMPORT_WORKER"])
        self.assertEqual("0755", report["producer"]["modes"]["capture-worker"])
        installed = report["installedPackageValidation"]
        self.assertEqual("validated", installed["status"])
        self.assertEqual(os.geteuid(), installed["runtimeUid"])
        self.assertEqual("runtime-owned", installed["ownerModel"])
        self.assertEqual(1, len(installed["copies"]))
        self.assertEqual("not-probed", report["kernel"]["status"])
        self.assertEqual("DYN", report["elf"]["sqliteType"])
        self.assertIsNone(report["elf"]["sqliteInterpreter"])
        self.assertEqual(["libc.so.6", "libm.so.6"], report["elf"]["sqliteDependencies"])
        self.assertEqual("2.34", report["elf"]["maximumSqliteGlibc"])

    def test_producer_only_report_does_not_claim_runtime_uid_validation(self):
        result = self.run_preflight(installed=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        installed = json.loads(self.output.read_text(encoding="utf-8"))[
            "installedPackageValidation"
        ]
        self.assertEqual({
            "status": "not-requested",
            "runtimeUid": None,
            "ownerModel": None,
            "copies": [],
        }, installed)

    def test_requires_installed_dir_and_runtime_uid_as_a_pair(self):
        without_uid = self.run_preflight(
            "--installed-dir", str(self.installed),
            installed=False,
        )
        self.assertNotEqual(0, without_uid.returncode)
        self.assertIn("must be provided together", without_uid.stderr)
        without_directory = self.run_preflight(
            "--runtime-uid", str(os.geteuid()),
            installed=False,
        )
        self.assertNotEqual(0, without_directory.returncode)
        self.assertIn("must be provided together", without_directory.stderr)

    def test_rejects_revision_mismatch(self):
        self.revision = "b" * 40
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not match", result.stderr)

    def test_rejects_nonexecutable_worker(self):
        os.chmod(self.assets / "capture-worker", 0o644)
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("not executable", result.stderr)

    def test_rejects_installed_hash_mismatch(self):
        (self.installed / "libe_sqlite3.so").write_bytes(b"changed")
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not match producer provenance", result.stderr)

    def test_rejects_unexpected_asset(self):
        (self.installed / "unexpected").write_text("no", encoding="utf-8")
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Unexpected assets", result.stderr)

    def test_rejects_group_writable_parent(self):
        os.chmod(self.installed, stat.S_IMODE(self.installed.stat().st_mode) | stat.S_IWGRP)
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("writable by group or other users", result.stderr)

    def test_rejects_symlinked_intermediate_component(self):
        real = self.root / "real-producer"
        self.assets.parent.rename(real)
        self.assets.parent.symlink_to(real, target_is_directory=True)
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("must not be a symbolic link", result.stderr)

    def test_rejects_attacker_owned_trusted_parent(self):
        attacker = SimpleNamespace(st_uid=os.geteuid() + 1, st_mode=stat.S_IFDIR | 0o755)
        with self.assertRaisesRegex(ValueError, "owner UID .* is not trusted"):
            PREFLIGHT.require_trusted_owner(
                Path("/trusted/parent"),
                attacker,
                {0, os.geteuid()},
            )

    def test_rejects_runtime_traversal_denial_before_file_execution(self):
        os.chmod(self.installed.parent, 0o700)
        result = self.run_preflight("--runtime-uid", str(os.geteuid() + 1))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("cannot traverse directory", result.stderr)

    def test_root_owned_world_executable_model_supports_cross_uid_execution(self):
        root_owned_directory = SimpleNamespace(st_uid=0, st_mode=stat.S_IFDIR | 0o755)
        root_owned_worker = SimpleNamespace(st_uid=0, st_mode=stat.S_IFREG | 0o755)
        trusted_uids, required_owner = PREFLIGHT.installed_ownership_policy(
            "root-owned",
            12345,
        )
        self.assertEqual(({0}, 0), (trusted_uids, required_owner))
        PREFLIGHT.require_trusted_owner(Path("/opt/tool"), root_owned_directory, trusted_uids)
        PREFLIGHT.require_trusted_owner(
            Path("/opt/tool/capture-worker"),
            root_owned_worker,
            trusted_uids,
        )
        self.assertTrue(PREFLIGHT.runtime_can_execute(root_owned_directory, 12345))
        self.assertTrue(PREFLIGHT.runtime_can_execute(root_owned_worker, 12345))

    def test_rejects_newer_glibc_requirement(self):
        self.readelf.write_text(
            self.readelf.read_text(encoding="utf-8").replace("GLIBC_2.34", "GLIBC_2.38"),
            encoding="utf-8",
        )
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("GLIBC_2.38", result.stderr)

    def test_rejects_sqlite_unapproved_dependency_independently(self):
        text = self.readelf.read_text(encoding="utf-8")
        self.readelf.write_text(
            text.replace(
                "Type: DYN (Shared object file)\n",
                "Type: DYN (Shared object file)\nShared library: [libcrypto.so.3]\n",
            ),
            encoding="utf-8",
        )
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SQLite sidecar has unexpected shared libraries: libcrypto.so.3",
                      result.stderr)

    def test_rejects_sqlite_newer_glibc_independently(self):
        text = self.readelf.read_text(encoding="utf-8")
        sqlite_start = text.index("Type: DYN")
        prefix, sqlite = text[:sqlite_start], text[sqlite_start:]
        self.readelf.write_text(prefix + sqlite.replace("GLIBC_2.34", "GLIBC_2.38", 1),
                                encoding="utf-8")
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SQLite sidecar requires unsupported GLIBC_2.38", result.stderr)


if __name__ == "__main__":
    unittest.main()
