import importlib.util
import json
from pathlib import Path
import subprocess
import time
import unittest
from unittest.mock import patch

from test_verify_clrmd_revalidation import EvidenceFixture

SPEC = importlib.util.spec_from_file_location(
    "live_compat", Path(__file__).parents[1] / "live-clrmd-compat.py")
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class LiveCompatibilityEvidenceTests(EvidenceFixture):
    def setUp(self):
        super().setUp()
        self.names = sorted(RUNNER.EXPECTED)
        self.write_evidence()
        self.mutate("t:Results/t:UnitTestResult[@outcome='NotExecuted']", "outcome", "Passed")
        self.mutate("t:ResultSummary/t:Counters", "executed", "4")
        self.mutate("t:ResultSummary/t:Counters", "passed", "4")

    def verify(self):
        return RUNNER.validate_inventory(self.trx, self.root / "core-discovery.txt",
                                         self.directory / "console.log")

    def test_exact_inventory_passes(self):
        self.assertEqual(4, len(self.verify()))

    def test_expected_skip_is_rejected(self):
        self.mutate("t:Results/t:UnitTestResult", "outcome", "NotExecuted")
        self.mutate("t:ResultSummary/t:Counters", "executed", "3")
        self.mutate("t:ResultSummary/t:Counters", "passed", "3")
        with self.assertRaisesRegex(ValueError, "missing/skipped"):
            self.verify()

    def test_missing_discovery_is_rejected(self):
        (self.root / "core-discovery.txt").write_text("")
        with self.assertRaisesRegex(ValueError, "No independently discovered"):
            self.verify()

    def test_failed_assertion_classification(self):
        self.mutate("t:Results/t:UnitTestResult", "outcome", "Failed")
        self.assertEqual("assertion-failure", RUNNER.failure_outcome(
            self.trx, self.directory / "console.log"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_abort_overrides_failed_assertions(self):
        self.mutate("t:Results/t:UnitTestResult", "outcome", "Failed")
        (self.directory / "console.log").write_text("Test Run Aborted")
        self.assertEqual("aborted", RUNNER.failure_outcome(self.trx, self.directory / "console.log"))

    def test_missing_trx_is_abort_not_pass(self):
        self.trx.unlink()
        self.assertEqual("aborted", RUNNER.failure_outcome(self.trx, self.directory / "console.log"))

    def test_unexpected_test_is_rejected(self):
        self.names.append("DotnetDiagnostics.Core.Tests.Unrelated.Extra")
        self.write_evidence()
        with self.assertRaisesRegex(ValueError, "exactly the four"):
            self.verify()

    def test_harness_failure_is_abort_and_cleans_exact_owned_names(self):
        with patch.object(RUNNER.signal, "setitimer"), \
                patch.object(RUNNER, "command", side_effect=ValueError("Unexpected target identity")), \
                patch.object(RUNNER, "container_metadata", return_value={}), \
                patch.object(RUNNER, "cleanup_container", return_value="absent") as cleanup:
            result = RUNNER.run_slot(self.root, self.root, 8, "unit-owner", time.monotonic() + 900)
        self.assertEqual("aborted", result["outcome"])
        self.assertEqual(2, cleanup.call_count)
        cleanup.assert_any_call("live-compat-unit-owner-8-target", "unit-owner")
        cleanup.assert_any_call("live-compat-unit-owner-8-inspector", "unit-owner")
        self.assertEqual(0o777, (self.root / "net8" / "evidence").stat().st_mode & 0o777)
        self.assertFalse((self.root / "net8" / "runtime").exists())
        self.assertFalse((self.root / "net8" / "diagnostics").exists())

    def test_prerequisite_timeout_is_not_relabelled_missing(self):
        for index, (error, outcome) in enumerate((
                (TimeoutError("watchdog"), "timeout"),
                (RuntimeError("daemon unavailable"), "missing-or-unsupported-prerequisite"),
                (RUNNER.AttemptFailure("interrupted", "aborted"), "aborted"))):
            with self.subTest(outcome=outcome), \
                    patch.object(RUNNER.signal, "signal"), patch.object(RUNNER.signal, "setitimer"), \
                    patch.object(RUNNER.platform, "system", return_value="Linux"), \
                    patch.object(RUNNER.platform, "machine", return_value="x86_64"), \
                    patch.object(Path, "is_file", return_value=True), \
                    patch.object(RUNNER, "command", side_effect=error):
                output = self.root / f"preflight-{index}"
                self.assertEqual(2, RUNNER.main(["--output", str(output)]))
                manifest = json.loads((output / "manifest.json").read_text())
                self.assertEqual(outcome, manifest["outcome"])
                self.assertEqual([8, 9, 10], manifest["missingMajors"])


class LiveCompatibilityTopologyTests(unittest.TestCase):
    def test_target_defaults_are_bounded_unprivileged_and_no_host_pid(self):
        options = RUNNER.create_options("owned-name", "owner", "256m")
        self.assertNotIn("--privileged", options)
        self.assertNotIn("--pid", options)
        self.assertNotIn("--cap-add", options)
        self.assertIn("ALL", options)
        self.assertIn("--read-only", options)
        self.assertIn("--memory", options)
        self.assertIn("--pids-limit", options)
        self.assertEqual("0:0", options[options.index("--user") + 1])

    def test_cleanup_refuses_wrong_owner(self):
        result = subprocess.CompletedProcess([], 0, json.dumps(
            [{"Id": "other-id", "Config": {"Labels": {RUNNER.LABEL: "other"}}}]), "")
        with patch.object(RUNNER.subprocess, "run", return_value=result), \
                patch.object(RUNNER, "command") as command:
            with self.assertRaisesRegex(ValueError, "unowned"):
                RUNNER.cleanup_container("exact-name", "owner")
            command.assert_not_called()

    def test_cleanup_uses_verified_exact_id(self):
        result = subprocess.CompletedProcess([], 0, json.dumps(
            [{"Id": "owned-id", "Config": {"Labels": {RUNNER.LABEL: "owner"}}}]), "")
        with patch.object(RUNNER.subprocess, "run", return_value=result), \
                patch.object(RUNNER, "command") as command:
            self.assertEqual("removed", RUNNER.cleanup_container("exact-name", "owner"))
            command.assert_called_once_with(["docker", "rm", "--force", "owned-id"], timeout=15)

    def test_unreachable_daemon_is_not_successful_cleanup(self):
        result = subprocess.CompletedProcess([], 1, "", "Cannot connect to Docker daemon")
        with patch.object(RUNNER.subprocess, "run", return_value=result):
            with self.assertRaisesRegex(RuntimeError, "Cannot verify cleanup"):
                RUNNER.cleanup_container("exact-name", "owner")

    def test_wrong_architecture_is_missing_prerequisite(self):
        with patch.object(RUNNER, "command", return_value='[{"Os":"linux","Architecture":"arm64"}]'):
            with self.assertRaises(RUNNER.PrerequisiteError):
                RUNNER.image_metadata("owned-image")


if __name__ == "__main__":
    unittest.main()
