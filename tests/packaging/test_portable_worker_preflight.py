"""Deterministic tests for the non-executing produced-worker preflight."""

import hashlib
import json
import os
from pathlib import Path
import platform
import stat
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/portable-worker-preflight.py"
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
            "cat <<'EOF'\n"
            "Class: ELF64\n"
            "Machine: Advanced Micro Devices X86-64\n"
            "[Requesting program interpreter: /lib64/ld-linux-x86-64.so.2]\n"
            "Shared library: [libm.so.6]\n"
            "Shared library: [libc.so.6]\n"
            "Name: GLIBC_2.34\n"
            "EOF\n",
            encoding="utf-8",
        )
        os.chmod(self.readelf, 0o755)
        self.output = self.root / "preflight.json"

    def run_preflight(self, *extra):
        return subprocess.run(
            [
                "python3", str(SCRIPT),
                "--assets-dir", str(self.assets),
                "--trusted-root", str(self.root),
                "--repo-root", str(ROOT),
                "--expected-revision", self.revision,
                "--installed-dir", str(self.installed),
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
        self.assertEqual("dotnet-diagnostics/portable-worker-preflight/v1", report["schema"])
        self.assertFalse(report["acceptance"]["importExecuted"])
        self.assertEqual(720, report["acceptance"]["outerDeadlineSeconds"])
        self.assertEqual(30, report["acceptance"]["terminationGraceSeconds"])
        self.assertEqual(4096, report["acceptance"]["maximumDiagnosticBytes"])
        self.assertIn("DifferentBundlesCompareAfterSourceDeletionAndDestinationReopen",
                      report["acceptance"]["filter"])
        self.assertEqual(str(self.assets / "capture-worker"),
                         report["activation"]["DOTNET_DIAGNOSTICS_IMPORT_WORKER"])
        self.assertEqual("0755", report["producer"]["modes"]["capture-worker"])

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

    def test_rejects_newer_glibc_requirement(self):
        self.readelf.write_text(
            self.readelf.read_text(encoding="utf-8").replace("GLIBC_2.34", "GLIBC_2.38"),
            encoding="utf-8",
        )
        result = self.run_preflight()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("GLIBC_2.38", result.stderr)


if __name__ == "__main__":
    unittest.main()
