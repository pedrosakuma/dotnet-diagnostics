"""Deterministic supervisor controls; uses only owned files/processes."""
import importlib.util
import json
import os
import pathlib
import select
import shutil
import signal
import subprocess
import sys
import unittest
import uuid
from unittest import mock

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    "scenario_attempt", pathlib.Path(__file__).with_name("run-scenario-attempt.py"))
supervisor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(supervisor)


def terminate_owned_fixture_child(pid):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        kernel.TerminateProcess.restype = wintypes.BOOL
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        handle = kernel.OpenProcess(0x00100000 | 0x0001, False, pid)  # SYNCHRONIZE | PROCESS_TERMINATE
        if not handle:
            error = ctypes.get_last_error()
            if error == 87:  # ERROR_INVALID_PARAMETER: this PID no longer exists.
                raise ProcessLookupError(f"Owned fixture child {pid} has already exited.")
            raise ctypes.WinError(error)
        try:
            if not kernel.TerminateProcess(handle, 1):
                error = ctypes.get_last_error()
                if kernel.WaitForSingleObject(handle, 0) != 0:
                    raise ctypes.WinError(error)
            waited = kernel.WaitForSingleObject(handle, 5000)
            if waited == 0xFFFFFFFF:
                raise ctypes.WinError(ctypes.get_last_error())
            if waited != 0:
                raise AssertionError(f"Owned fixture child {pid} did not exit within 5 seconds.")
        finally:
            if not kernel.CloseHandle(handle):
                cleanup_error = ctypes.WinError(ctypes.get_last_error())
                primary_error = sys.exc_info()[1]
                if primary_error is None:
                    raise cleanup_error
                primary_error.add_note(f"CloseHandle also failed: {cleanup_error}")
    else:
        # The fixture is a grandchild: waitpid cannot reap it here. A pidfd gives
        # a stable identity and an exit notification before its log is removed.
        descriptor = os.pidfd_open(pid)
        try:
            signal.pidfd_send_signal(descriptor, signal.SIGKILL)
            exited = select.poll()
            exited.register(descriptor, select.POLLIN)
            if not exited.poll(5000):
                raise AssertionError(f"Owned fixture child {pid} did not exit within 5 seconds.")
        finally:
            os.close(descriptor)


class ScenarioAttemptTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path.cwd() / (".scenario-attempt-test-" + uuid.uuid4().hex)
        self.root.mkdir()
        self.log = self.root / "output.log"
        self.status = self.root / "process.json"

    def tearDown(self):
        shutil.rmtree(self.root)

    def run_child(self, code, timeout=2):
        result = supervisor.run_attempt(
            [sys.executable, "-c", code], self.log, self.status, timeout)
        return result, json.loads(self.status.read_text(encoding="utf-8"))

    def test_success_preserves_stdout_and_stderr(self):
        result, status = self.run_child("import sys; print('stdout'); print('stderr',file=sys.stderr)")
        self.assertEqual(0, result)
        self.assertFalse(status["timedOut"])
        self.assertIn("stdout", self.log.read_text())
        self.assertIn("stderr", self.log.read_text())

    def test_real_exit_124_is_not_timeout(self):
        result, status = self.run_child("raise SystemExit(124)")
        self.assertEqual(124, result)
        self.assertFalse(status["timedOut"])

    def test_timeout_after_test_summary_keeps_artifact_and_trx(self):
        artifact, trx = self.root / "attempt.json", self.root / "attempt.trx"
        result, status = self.run_child(
            f"import pathlib,time; pathlib.Path({str(artifact)!r}).write_text('passed artifact'); "
            f"pathlib.Path({str(trx)!r}).write_text('completed TRX'); "
            "print('All tests finished running',flush=True); time.sleep(30)", timeout=1)
        self.assertEqual(124, result)
        self.assertTrue(status["timedOut"])
        self.assertEqual("passed artifact", artifact.read_text())
        self.assertEqual("completed TRX", trx.read_text())
        self.assertIn("All tests finished running", self.log.read_text())
        self.assertNotEqual("not-needed", status["cleanup"])

    def test_inherited_stdout_does_not_hold_completion_open(self):
        child_pid = self.root / "child.pid"
        try:
            result, status = self.run_child(
                "import subprocess,sys,pathlib; "
                "child=subprocess.Popen([sys.executable,'-c','import threading; threading.Event().wait()']); "
                f"pathlib.Path({str(child_pid)!r}).write_text(str(child.pid)); "
                "print('parent done',flush=True)", timeout=2)
            self.assertEqual(0, result)
            self.assertFalse(status["timedOut"])
        finally:
            if child_pid.exists():
                try:
                    terminate_owned_fixture_child(int(child_pid.read_text()))
                except ProcessLookupError:
                    pass

    def test_launch_error_is_recorded(self):
        result = supervisor.run_attempt(
            [str(self.root / "does-not-exist")], self.log, self.status, 1)
        self.assertEqual(125, result)
        self.assertIn("launchError", json.loads(self.status.read_text()))

    def test_missing_fixture_child_has_consistent_cleanup_error(self):
        if os.name == "nt":
            with mock.patch("ctypes.WinDLL") as library, mock.patch("ctypes.get_last_error", return_value=87):
                library.return_value.OpenProcess.return_value = None
                with self.assertRaises(ProcessLookupError):
                    terminate_owned_fixture_child(123)
        else:
            with mock.patch("os.pidfd_open", side_effect=ProcessLookupError):
                with self.assertRaises(ProcessLookupError):
                    terminate_owned_fixture_child(123)

    def test_fixture_cleanup_does_not_hide_access_denied(self):
        if os.name == "nt":
            with mock.patch("ctypes.WinDLL") as library, mock.patch("ctypes.get_last_error", return_value=5):
                library.return_value.OpenProcess.return_value = None
                with self.assertRaises(OSError) as error:
                    terminate_owned_fixture_child(123)
        else:
            with mock.patch("os.pidfd_open", side_effect=PermissionError):
                with self.assertRaises(OSError) as error:
                    terminate_owned_fixture_child(123)
        self.assertNotIsInstance(error.exception, ProcessLookupError)

    def test_fixture_cleanup_preserves_primary_error(self):
        if os.name == "nt":
            with mock.patch("ctypes.WinDLL") as library, mock.patch("ctypes.get_last_error", return_value=6):
                library.return_value.OpenProcess.return_value = 1
                library.return_value.TerminateProcess.return_value = True
                library.return_value.WaitForSingleObject.return_value = 258
                library.return_value.CloseHandle.return_value = False
                with self.assertRaisesRegex(AssertionError, "did not exit") as error:
                    terminate_owned_fixture_child(123)
                self.assertIn("CloseHandle also failed", error.exception.__notes__[0])
        else:
            with mock.patch("os.pidfd_open", return_value=7), \
                    mock.patch("signal.pidfd_send_signal", side_effect=PermissionError), \
                    mock.patch("os.close") as close:
                with self.assertRaises(PermissionError):
                    terminate_owned_fixture_child(123)
                close.assert_called_once_with(7)

    @unittest.skipIf(os.name == "nt", "Shell integration uses the Linux bash control; supervisor runs natively on both OSes.")
    def test_runner_timeout_overrides_passed_artifact_without_retry(self):
        fake_dotnet = self.root / "dotnet"
        fake_dotnet.write_text(
            f"#!{sys.executable}\n"
            "import json,os,pathlib,time\n"
            "pathlib.Path(os.environ['DOTNET_DIAGNOSTICS_SCENARIO_TRIAL_ARTIFACT_PATH']).write_text("
            "json.dumps({'outcome':'Passed','failureKind':'None','detail':'test completed'}))\n"
            "print('All tests finished running',flush=True)\n"
            "time.sleep(30)\n", encoding="utf-8")
        fake_dotnet.chmod(0o755)
        results = self.root / "results"
        repository = pathlib.Path(__file__).resolve().parents[1]
        completed = subprocess.run(
            ["bash", str(repository / "scripts/run-scenario-evaluation-isolated.sh"),
             "--scenario", "gc-storm", "--repetitions", "1", "--max-crash-retries", "3",
             "--attempt-timeout-seconds", "1", "--results-root", str(results)],
            cwd=repository, env=dict(os.environ, PATH=str(self.root) + os.pathsep + os.environ["PATH"]),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30, check=False)
        self.assertEqual(1, completed.returncode, completed.stdout.decode())
        trial = json.loads((results / "trials/gc-storm.trial-1.result.json").read_text())
        self.assertEqual("failed", trial["finalOutcome"])
        self.assertEqual("environment", trial["finalFailureKind"])
        self.assertEqual(1, trial["attemptCount"])
        self.assertEqual("Passed", trial["trialArtifact"]["outcome"])
        self.assertTrue(trial["attempts"][0]["timedOut"])
        self.assertIn("post-test teardown", trial["detail"])


if __name__ == "__main__":
    unittest.main()
