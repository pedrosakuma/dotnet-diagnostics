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
            result = RUNNER.run_slot(self.root.resolve(), self.root.resolve(), 8, "unit-owner", time.monotonic() + 900, "sdk-id")
        self.assertEqual("aborted", result["outcome"])
        self.assertEqual(2, cleanup.call_count)
        self.assertEqual({"live-compat-unit-owner-8-target", "live-compat-unit-owner-8-inspector"},
                         {call.args[0] for call in cleanup.call_args_list})
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

    def test_cleanup_exception_cannot_erase_executed_slot(self):
        result = {"major": 8, "outcome": "cleanup-failure", "collectionOutcome": "assertion-failure",
                  "error": "tests exited 1"}
        output = self.root / "aggregate"
        with patch.object(RUNNER.signal, "signal"), patch.object(RUNNER.signal, "setitimer"), \
                patch.object(RUNNER.platform, "system", return_value="Linux"), \
                patch.object(RUNNER.platform, "machine", return_value="x86_64"), \
                patch.object(Path, "is_file", return_value=True), \
                patch.object(RUNNER, "command", return_value="metadata"), \
                patch.object(RUNNER, "image_metadata", return_value={"Id": "sdk-id"}), \
                patch.object(Path, "read_bytes", return_value=b"clrmd"), \
                patch.object(RUNNER, "run_slot", return_value=result) as slot:
            self.assertEqual(2, RUNNER.main(["--output", str(output)]))
        manifest = json.loads((output / "manifest.json").read_text())
        self.assertEqual([result], manifest["slots"])
        self.assertEqual([9, 10], manifest["missingMajors"])
        slot.assert_called_once()

    def test_filesystem_cleanup_failure_returns_slot_result(self):
        with patch.object(RUNNER.signal, "setitimer"), \
                patch.object(RUNNER, "command", side_effect=ValueError("capture failed")), \
                patch.object(RUNNER, "container_metadata", return_value={}), \
                patch.object(RUNNER, "cleanup_slot", side_effect=PermissionError("root scratch")):
            result = RUNNER.run_slot(self.root.resolve(), self.root.resolve(), 8, "unit-owner",
                                     time.monotonic() + 900, "sdk-id")
        self.assertEqual("cleanup-failure", result["outcome"])
        self.assertEqual("aborted", result["collectionOutcome"])
        self.assertIn("root scratch", result["cleanupError"])
        self.assertEqual(result, json.loads((self.root / "net8/result.json").read_text()))

    def test_unverified_container_removal_retains_both_filesystems(self):
        slot = self.root.resolve() / "net8"
        (slot / "diagnostics").mkdir(parents=True)
        (slot / "runtime").mkdir()
        with patch.object(RUNNER.signal, "setitimer"), \
                patch.object(RUNNER, "cleanup_container", side_effect=[ValueError("unowned"), "removed"]), \
                patch.object(RUNNER, "cleanup_scratch") as scratch:
            result = RUNNER.cleanup_slot(slot, "owner", "image", "target", "inspector")
        scratch.assert_not_called()
        self.assertEqual("cleanup-failure", result["outcome"])
        self.assertTrue((slot / "runtime").exists())
        self.assertTrue((slot / "diagnostics").exists())

    def test_scratch_failure_or_timeout_is_explicit(self):
        for major, error in ((8, PermissionError("root scratch")), (9, TimeoutError("watchdog"))):
            slot = self.root.resolve() / f"net{major}"
            (slot / "diagnostics").mkdir(parents=True)
            (slot / "runtime").mkdir()
            with patch.object(RUNNER.signal, "setitimer"), \
                    patch.object(RUNNER, "cleanup_container", return_value="removed"), \
                    patch.object(RUNNER, "cleanup_scratch", side_effect=error):
                result = RUNNER.cleanup_slot(slot, "owner", "image", "target", "inspector")
            self.assertEqual("cleanup-failure", result["outcome"])
            self.assertIn(type(error).__name__, result["filesystem"]["diagnostics"]["error"])

    def test_cleanup_helper_has_only_exact_mount_and_is_reaped(self):
        slot = self.root.resolve() / "net8"
        directory = slot / "diagnostics"
        directory.mkdir(parents=True)
        child = directory / "owned"
        child.write_text("fixture")
        calls = []

        def command(args, timeout):
            calls.append(args)
            if args[1] == "create":
                return "helper-id"
            if args[1] == "wait":
                child.unlink()
                return "0"
            return ""

        evidence = {}
        with patch.object(RUNNER, "command", side_effect=command), \
                patch.object(RUNNER, "inspect_owned", return_value={"Image": "image"}), \
                patch.object(RUNNER, "cleanup_container", return_value="removed") as reap:
            RUNNER.cleanup_scratch(slot, directory, "owner", "image", lambda n: n, evidence)
        create = calls[0]
        self.assertEqual(1, create.count("--mount"))
        self.assertIn(f"type=bind,src={directory},dst=/owned,bind-recursive=disabled", create)
        for forbidden in ("--pid", "--privileged", "--cap-add"):
            self.assertNotIn(forbidden, create)
        self.assertEqual(["20s", "/usr/bin/find", "-P", "/owned", "-xdev", "-depth", "-mindepth", "1", "-delete"], create[-9:])
        self.assertTrue(evidence["directoryAbsent"])
        reap.assert_called_once()

    def test_scratch_path_refuses_symlink(self):
        slot = self.root.resolve() / "net8"
        slot.mkdir()
        directory = slot / "diagnostics"
        directory.symlink_to(self.root.resolve(), target_is_directory=True)
        with patch.object(RUNNER, "command") as command:
            with self.assertRaisesRegex(ValueError, "noncanonical"):
                RUNNER.cleanup_scratch(slot, directory, "owner", "image", lambda n: n, {})
        command.assert_not_called()

    def test_helper_timeout_retains_primary_error_and_reaps_exact_role(self):
        slot = self.root.resolve() / "net8"
        directory = slot / "diagnostics"
        directory.mkdir(parents=True)
        (directory / "owned").write_text("fixture")
        evidence = {}
        with patch.object(RUNNER, "command", side_effect=["helper", "", subprocess.TimeoutExpired("wait", 25)]), \
                patch.object(RUNNER, "inspect_owned", return_value={"Image": "image"}), \
                patch.object(RUNNER, "cleanup_container", return_value="removed") as reap:
            with self.assertRaises(subprocess.TimeoutExpired):
                RUNNER.cleanup_scratch(slot, directory, "owner", "image", lambda n: n, evidence)
        self.assertIn("TimeoutExpired", evidence["operationError"])
        self.assertEqual("removed", evidence["helperCleanup"])
        self.assertEqual("live-compat-owner-8-cleanup", reap.call_args.args[0])
        self.assertTrue(directory.exists())


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
        missing = subprocess.CompletedProcess([], 1, "", "error: no such object: owned-id")
        with patch.object(RUNNER.subprocess, "run", side_effect=[result, missing]), \
                patch.object(RUNNER, "command") as command:
            self.assertEqual("removed", RUNNER.cleanup_container("exact-name", "owner"))
            command.assert_called_once_with(["docker", "rm", "--force", "owned-id"], timeout=10)

    def test_live_roles_are_stopped_before_removal(self):
        data = {"Id": "owned-id", "State": {"Running": True}}
        with patch.object(RUNNER, "inspect_owned", side_effect=[data, None]), \
                patch.object(RUNNER, "command") as command:
            RUNNER.cleanup_container("exact-name", "owner")
        self.assertEqual(["stop", "rm"], [call.args[0][1] for call in command.call_args_list])

    def test_exact_absence_is_case_insensitive_but_daemon_errors_are_not_absence(self):
        for text in ("Error: No such object: exact-name", "error: no such object: exact-name",
                     "Error response from daemon: No such container: exact-name"):
            self.assertTrue(RUNNER.no_such_container(text, "exact-name"))
        self.assertFalse(RUNNER.no_such_container("Cannot connect to Docker daemon", "exact-name"))
        self.assertFalse(RUNNER.no_such_container("no such object: somebody-else", "exact-name"))

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
