"""Bounded acquisition tests; only explicitly spawned fixture processes are observed."""
import json
import os
import pathlib
import select
import shutil
import subprocess
import sys
import threading
import time
import unittest
import uuid
from unittest import mock

sys.dont_write_bytecode = True
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import scenario_attempt_forensics as writer
import scenario_process_evidence as evidence
import test_scenario_attempt as controls

supervisor = controls.supervisor


def await_condition(predicate, seconds=8):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        threading.Event().wait(0.01)
    raise AssertionError("Explicit fixture readiness was not observed before its deadline.")


def await_owned_exit(pid, seconds):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x00100000, False, pid)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            if kernel.WaitForSingleObject(handle, int(seconds * 1000)) != 0:
                raise AssertionError("Owned target did not exit at its bounded deadline.")
        finally:
            kernel.CloseHandle(handle)
    else:
        descriptor = os.pidfd_open(pid)
        try:
            poll = select.poll()
            poll.register(descriptor, select.POLLIN)
            if not poll.poll(int(seconds * 1000)):
                raise AssertionError("Owned target did not exit at its bounded deadline.")
        finally:
            os.close(descriptor)


def owned_is_alive(pid):
    identity = evidence.windows_identity(pid) if os.name == "nt" else evidence.linux_identity(pid)
    return identity["state"] not in ("exited", "Z")


def publish_tracking_ready(destination, document, publish):
    """An immutable fixture acknowledgment avoids racing Windows replacement readers."""
    snapshot = document.get("latestSnapshot", {})
    if (not destination.exists()
            and any(item.get("depth") == 1 for item in snapshot.get("processes", []))):
        publish(destination, snapshot)


class ForensicTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path.cwd() / (".scenario-forensics-test-" + uuid.uuid4().hex)
        self.root.mkdir()
        self.log = self.root / "output.log"
        self.status = self.root / "status.json"
        self.forensics = self.root / "status.json.forensics.json"

    def tearDown(self):
        shutil.rmtree(self.root)

    def test_atomic_write_failure_keeps_last_complete_artifact(self):
        writer.write_atomic(self.forensics, {"phase": "launch"})
        with mock.patch.object(writer.os, "replace", side_effect=PermissionError):
            with self.assertRaises(PermissionError):
                writer.write_atomic(self.forensics, {"phase": "next"})
        self.assertEqual({"phase": "launch"}, json.loads(self.forensics.read_text()))
        self.assertEqual({"phase": "next"}, json.loads(
            self.forensics.with_name(self.forensics.name + ".partial").read_text()))

    def test_reader_sees_complete_previous_file_until_atomic_replacement(self):
        writer.write_atomic(self.forensics, {"phase": "launch"})
        pending = threading.Event()
        publish = threading.Event()
        replace = os.replace
        errors = []

        def blocked_replace(source, destination):
            pending.set()
            if not publish.wait(5):
                raise AssertionError("Reader never acknowledged the staged artifact.")
            replace(source, destination)

        def update():
            try:
                writer.write_atomic(self.forensics, {"phase": "next"})
            except Exception as error:
                errors.append(error)

        with mock.patch.object(writer.os, "replace", side_effect=blocked_replace):
            thread = threading.Thread(target=update)
            thread.start()
            try:
                self.assertTrue(pending.wait(5))
                self.assertEqual({"phase": "launch"}, json.loads(self.forensics.read_text()))
            finally:
                publish.set()
                thread.join(timeout=5)
        self.assertFalse(thread.is_alive())
        self.assertEqual([], errors)
        self.assertEqual({"phase": "next"}, json.loads(self.forensics.read_text()))

    def test_byte_cap_is_checked_on_insertion_and_serialization(self):
        document = {"byteCapReached": False}
        self.assertFalse(writer.insert_bounded(document, "snapshot", "x" * writer.MAX_BYTES))
        self.assertNotIn("snapshot", document)
        self.assertTrue(document["byteCapReached"])
        with self.assertRaises(ValueError):
            writer.encoded_bounded({"value": "x" * writer.MAX_BYTES})

    def test_tracking_acknowledgment_is_immutable_and_requires_owned_child(self):
        ready = self.root / "tracked.json"
        publish_tracking_ready(ready, {"latestSnapshot": {"processes": [{"depth": 0}]}},
                               writer.write_atomic)
        self.assertFalse(ready.exists())
        first = {"processes": [{"depth": 1, "pid": 10}]}
        publish_tracking_ready(ready, {"latestSnapshot": first}, writer.write_atomic)
        with mock.patch.object(writer, "write_atomic") as write:
            publish_tracking_ready(ready, {"latestSnapshot": {"processes": [{"depth": 1, "pid": 20}]}},
                                   write)
        write.assert_not_called()
        self.assertEqual(first, json.loads(ready.read_text()))

    def test_permission_failure_is_explicit_not_complete(self):
        tracker = evidence.OwnedProcesses(10, 9, self.log)
        tracker.known[10] = {"pid": 10, "creation": "1", "parentPid": 9, "depth": 0}
        tracker.identity = mock.Mock(side_effect=PermissionError)
        result = tracker.snapshot()
        self.assertEqual("best-effort-incomplete", result["quality"])
        self.assertEqual(1, result["limitations"]["permission-denied"])

    def test_reused_pid_is_not_enriched(self):
        tracker = evidence.OwnedProcesses(10, 9, self.log)
        tracker.known[10] = {"pid": 10, "creation": "1", "parentPid": 9, "depth": 0}
        tracker.identity = mock.Mock(return_value={"pid": 10, "creation": "2"})
        with mock.patch.object(evidence.os, "readlink") as readlink:
            result = tracker.snapshot()
        readlink.assert_not_called()
        self.assertEqual("identity-changed-not-inspected", result["processes"][0]["state"])

    def test_process_and_depth_caps_are_enforced_before_retention(self):
        tracker = evidence.OwnedProcesses(10, 9, self.log)
        tracker.identity = lambda pid: {"pid": pid, "creation": str(pid)}
        relations = [(10, 9)] + [(pid, 10) for pid in range(11, 300)]
        with mock.patch.object(evidence, "IS_WINDOWS", True), \
                mock.patch.object(evidence, "windows_relations", return_value=(relations, False)):
            tracker.discover()
        self.assertEqual(evidence.MAX_PROCESSES, len(tracker.known))
        self.assertIn("process-cap", tracker.limitations)
        tracker = evidence.OwnedProcesses(10, 9, self.log)
        tracker.identity = lambda pid: {"pid": pid, "creation": str(pid)}
        relations = [(pid, pid - 1) for pid in range(10, 100)]
        with mock.patch.object(evidence, "IS_WINDOWS", True), \
                mock.patch.object(evidence, "windows_relations", return_value=(relations, False)):
            tracker.discover()
        self.assertLessEqual(max(item["depth"] for item in tracker.known.values()), evidence.MAX_DEPTH)
        self.assertIn("discovery-depth-cap", tracker.limitations)

    def test_thread_cap_is_shared_across_owned_processes(self):
        tracker = evidence.OwnedProcesses(10, 9, self.log)
        tracker.known = {pid: {"pid": pid, "creation": str(pid), "parentPid": 9, "depth": 0}
                         for pid in (10, 11)}
        tracker.identity = lambda pid: tracker.known[pid]
        entries = [mock.Mock(path=str(self.root / str(tid))) for tid in range(200)]
        for tid, entry in enumerate(entries):
            entry.name = str(tid)
        with mock.patch.object(evidence, "IS_WINDOWS", False), \
                mock.patch.object(evidence, "MAX_DISCOVERY_SECONDS", 2), \
                mock.patch.object(evidence.os, "scandir") as scan, \
                mock.patch.object(pathlib.Path, "open", mock.mock_open(read_data="wait")), \
                mock.patch.object(evidence.os, "readlink", return_value="pipe:[1]"):
            scan.return_value.__enter__.return_value = entries
            result = tracker.snapshot()
        self.assertLessEqual(sum(len(item["threads"]) for item in result["processes"]), evidence.MAX_THREADS)
        self.assertTrue(any("cap" in name for name in result["limitations"]))
        self.assertLessEqual(len(writer.encoded_bounded(result)), evidence.MAX_SNAPSHOT_BYTES)

    def test_partial_control_write_disables_further_messages(self):
        worker = supervisor.ForensicWorker.__new__(supervisor.ForensicWorker)
        worker.phase_count = 0
        worker.status = {"state": "running", "droppedPhases": 0}
        worker.process = mock.Mock()
        with mock.patch.object(supervisor.os, "write", return_value=1) as write:
            worker.phase("target-started")
            worker.phase("root-exited")
        self.assertEqual(1, write.call_count)
        self.assertEqual("partial-write", worker.status["controlError"])
        self.assertEqual(2, worker.status["droppedPhases"])

    def test_helper_denial_preserves_target_success_with_explicit_acquisition_failure(self):
        result = supervisor.run_attempt(
            [sys.executable, "-c", "pass"], self.log, self.status, 2,
            forensic_command=[sys.executable, "-c", "raise SystemExit(71)"])
        self.assertEqual(0, result)
        status = json.loads(self.status.read_text())["forensics"]
        self.assertEqual("acquisition-failed", status["state"])
        self.assertEqual("permission-denied", status["error"])

    def test_nonblocking_setup_failure_reaps_helper_before_launching_target(self):
        for error in (OSError, NotImplementedError):
            with self.subTest(error=error.__name__), \
                    mock.patch.object(supervisor.subprocess, "Popen") as popen, \
                    mock.patch.object(supervisor.os, "set_blocking", side_effect=error):
                worker = supervisor.ForensicWorker(self.forensics, self.log)
                popen.return_value.stdin.close.assert_called_once()
                popen.return_value.kill.assert_called_once()
                popen.return_value.wait.assert_called_once_with(timeout=1)
                self.assertEqual("startup-failed", worker.finish()["state"])

    def test_phase_count_and_size_are_bounded_without_blocking(self):
        worker = supervisor.ForensicWorker.__new__(supervisor.ForensicWorker)
        worker.phase_count = 0
        worker.status = {"state": "running", "droppedPhases": 0}
        worker.process = mock.Mock()
        with mock.patch.object(supervisor.os, "write", side_effect=BlockingIOError):
            for _ in range(20):
                worker.phase("target-started")
            worker.phase("oversized", value="x" * 1000)
        self.assertEqual(21, worker.status["droppedPhases"])

    def test_actual_helper_hang_cannot_extend_target_termination_deadline(self):
        target_pid = self.root / "target.pid"
        helper_pid = self.root / "helper.pid"
        ready = self.root / "observed"
        code = (f"import os,pathlib,threading; pathlib.Path({str(target_pid)!r}).write_text(str(os.getpid())); "
                f"print('All tests finished running',flush=True); "
                "threading.Event().wait()")
        helper = [sys.executable, "-c",
                  f"import pathlib,os,threading; pathlib.Path({str(helper_pid)!r}).write_text(str(os.getpid())); "
                  "threading.Event().wait()"]
        observed = {}

        def observe():
            try:
                await_condition(lambda: target_pid.exists() and helper_pid.exists())
                observed["start"] = time.monotonic()
                ready.write_text("watching")
                await_owned_exit(int(target_pid.read_text()), 3)
                observed["exit"] = time.monotonic()
                observed["helperAliveAtTargetExit"] = owned_is_alive(int(helper_pid.read_text()))
            except Exception as error:
                observed["error"] = error

        watcher = threading.Thread(target=observe)
        watcher.start()
        result = supervisor.run_attempt([sys.executable, "-c", code], self.log, self.status, 1,
                                        forensic_command=helper)
        watcher.join(timeout=4)
        self.assertFalse(watcher.is_alive())
        self.assertNotIn("error", observed)
        self.assertTrue(ready.exists())
        self.assertTrue(helper_pid.exists())
        self.assertEqual(124, result)
        self.assertTrue(observed["helperAliveAtTargetExit"],
                        "target termination must precede waiting for or killing the hung helper")
        self.assertLess(observed["exit"] - observed["start"], 2,
                        "actual target exit must not wait for the hung helper's completion budget")
        status = json.loads(self.status.read_text())
        self.assertEqual("acquisition-deadline", status["forensics"]["state"])
        self.assertIn("exitCode", status["forensics"])


class MechanismControl(unittest.TestCase):
    setUp = ForensicTests.setUp
    tearDown = ForensicTests.tearDown
    def test_tracking_readiness_precedes_root_exit_with_known_child_alive(self):
        release = self.root / "release"
        ready = self.root / "tracked.json"
        child_pid = self.root / "child.pid"
        child_code = "import threading; threading.Event().wait()"
        code = ("import subprocess,sys,pathlib,threading; "
                f"child=subprocess.Popen([sys.executable,'-c',{child_code!r}]); "
                f"pathlib.Path({str(child_pid)!r}).write_text(str(child.pid)); "
                f"release=pathlib.Path({str(release)!r}); "
                "print('summary before root exit',flush=True)\n"
                "while not release.exists(): threading.Event().wait(0.01)\n")
        outcome = {}
        # Observe the real worker's successful publication once, not a concurrently
        # replaced pathname. Native Windows readers need not race MoveFileEx/UNC.
        helper = (
            f"import sys,pathlib; sys.path.insert(0,{str(pathlib.Path(__file__).resolve().parent)!r}); "
            "import scenario_attempt_forensics as worker; "
            "from test_scenario_forensics import publish_tracking_ready; "
            "publish=worker.write_atomic\n"
            "def acknowledge(path,document):\n"
            " publish(path,document)\n"
            f" publish_tracking_ready(pathlib.Path({str(ready)!r}),document,publish)\n"
            "worker.write_atomic=acknowledge\n"
            "raise SystemExit(worker.main())\n")

        def capture():
            outcome["exit"] = supervisor.run_attempt(
                [sys.executable, "-c", code], self.log, self.status, 15,
                forensic_command=[sys.executable, "-B", "-c", helper, str(self.forensics), str(self.log)])

        run = threading.Thread(target=capture)
        run.start()
        try:
            def tracked():
                if not child_pid.exists() or not ready.exists():
                    return False
                snapshot = json.loads(ready.read_text())
                return any(item["pid"] == int(child_pid.read_text()) for item in snapshot["processes"])
            await_condition(tracked)
            release.write_text("tracking observed; root may now exit")
            run.join(timeout=4)
            self.assertFalse(run.is_alive())
            self.assertEqual(0, outcome["exit"])
            artifact = json.loads(self.forensics.read_text())
            child = next(item for item in artifact["finalSnapshot"]["processes"]
                         if item["pid"] == int(child_pid.read_text()))
            self.assertNotIn(child["state"], ["exited", "unavailable-or-exited"])
            self.assertEqual("best-effort-incomplete", artifact["finalSnapshot"]["quality"])
            self.assertLessEqual(self.forensics.stat().st_size, writer.MAX_BYTES)
        finally:
            release.touch()
            run.join(timeout=40)
            if child_pid.exists():
                try:
                    controls.terminate_owned_fixture_child(int(child_pid.read_text()))
                except ProcessLookupError:
                    pass
            self.assertFalse(run.is_alive(), "Owned supervisor did not finish fixture cleanup.")


if __name__ == "__main__":
    unittest.main()
