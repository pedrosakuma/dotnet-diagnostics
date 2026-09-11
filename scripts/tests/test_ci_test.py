import importlib.util
import io
from pathlib import Path
import shutil
import subprocess
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

from test_verify_clrmd_revalidation import EvidenceFixture, NS

SPEC = importlib.util.spec_from_file_location(
    "ci_runner", Path(__file__).parents[1] / "ci-test.py")
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class CiEvidenceTests(EvidenceFixture):
    def setUp(self):
        super().setUp()
        self.directory = self.root / "core"
        shutil.move(self.root / "01" / "core", self.directory)
        self.trx = self.directory / "core.trx"
        shutil.copy(self.root / "core-discovery.txt", self.directory / "discovery.txt")
        (self.directory / "exit-code.txt").write_text("0")

    def verify(self):
        RUNNER.verify(self.root, "core")

    def test_complete_run_accepts_unrelated_platform_skip(self):
        self.verify()

    def test_missing_discovered_test_is_rejected(self):
        with (self.directory / "discovery.txt").open("a") as file:
            file.write("\n    DotnetDiagnostics.Core.Tests.OtherTest.Missing")
        with self.assertRaisesRegex(ValueError, "Discovered tests missing"):
            self.verify()

    def test_intentional_target_crash_artifacts_are_allowed(self):
        for name in ("target.dmp", "target.crashreport.json", "target_Sequence.xml"):
            (self.directory / name).write_text("Intentional CrashGuard target crash")
        self.verify()

    def test_nonzero_exit_with_all_passed_rows_is_rejected(self):
        for code in (1, 124, 139):
            with self.subTest(code=code):
                (self.directory / "exit-code.txt").write_text(str(code))
                with self.assertRaisesRegex(ValueError, "no retry or masking"):
                    self.verify()

    def test_failed_summary_with_zero_failed_counter_is_rejected(self):
        self.mutate("t:ResultSummary", "outcome", "Failed")
        with self.assertRaisesRegex(ValueError, "did not complete"):
            self.verify()

    def test_run_level_error_is_rejected(self):
        tree = ET.parse(self.trx)
        summary = tree.find("t:ResultSummary", {"t": NS})
        infos = ET.SubElement(summary, f"{{{NS}}}RunInfos")
        ET.SubElement(infos, f"{{{NS}}}RunInfo", outcome="Error")
        tree.write(self.trx)
        with self.assertRaisesRegex(ValueError, "run-level error"):
            self.verify()

    def test_empty_run_is_rejected(self):
        tree = ET.parse(self.trx)
        tree.find("t:Results", {"t": NS}).clear()
        tree.find("t:ResultSummary/t:Counters", {"t": NS}).set("total", "0")
        tree.write(self.trx)
        with self.assertRaisesRegex(ValueError, "Incomplete/empty"):
            self.verify()

    def test_missing_evidence_is_rejected(self):
        for name in ("core.trx", "exit-code.txt", "discovery.txt", "console.log"):
            with self.subTest(name=name):
                path = self.directory / name
                content = path.read_bytes()
                path.unlink()
                with self.assertRaises(OSError):
                    self.verify()
                path.write_bytes(content)

    def test_required_manifest_is_independent_of_test_attributes(self):
        required = RUNNER.required_tests("core")
        self.assertEqual(len(required), 12)
        source = Path("tests/DotnetDiagnostics.Core.Tests/LiveCoreClrProcessTests.cs").read_text()
        for name in required:
            self.assertIn("public async Task " + name.rsplit(".", 1)[1] + "(", source)

    def test_required_test_missing_from_discovery_is_rejected(self):
        path = self.directory / "discovery.txt"
        path.write_text(path.read_text().replace("    " + self.names[0], ""))
        with self.assertRaises(ValueError):
            self.verify()

    def test_runner_invokes_once_and_preserves_failure_with_valid_trx(self):
        for code in (0, 1, 124, 139):
            with self.subTest(code=code):
                root = self.root / f"run-{code}"

                def discover(command, **kwargs):
                    self.assertEqual(command[-1], "--list-tests")
                    kwargs["stdout"].write((self.directory / "discovery.txt").read_bytes())
                    return subprocess.CompletedProcess(command, 0)

                class Process:
                    stdout = io.BytesIO(b"All test rows passed\n")

                    def __enter__(self):
                        shutil.copy(self.trx, root / "core" / "core.trx")
                        return self

                    def __exit__(self, *args):
                        pass

                    def wait(self):
                        return code

                process = Process()
                process.trx = self.trx
                with patch.object(RUNNER.subprocess, "run", side_effect=discover), \
                     patch.object(RUNNER.subprocess, "Popen", return_value=process) as launch:
                    self.assertEqual(RUNNER.run(root, "core", "core.csproj", None), code)
                    launch.assert_called_once()
                self.assertEqual((root / "core" / "exit-code.txt").read_text(), str(code))
                if code:
                    with self.assertRaisesRegex(ValueError, "no retry or masking"):
                        RUNNER.verify(root, "core")

    def test_runner_rejects_stale_directory_before_launch(self):
        with patch.object(RUNNER.subprocess, "run") as launch:
            with self.assertRaises(FileExistsError):
                RUNNER.run(self.root, "core", "core.csproj", None)
            launch.assert_not_called()


if __name__ == "__main__":
    unittest.main()
