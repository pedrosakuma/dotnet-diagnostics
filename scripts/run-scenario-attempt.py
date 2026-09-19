#!/usr/bin/env python3
"""Bound the whole test command, including teardown after xUnit finishes."""
import argparse
import json
import os
import pathlib
import signal
import subprocess
import sys
import time


class ForensicWorker:
    def __init__(self, destination, log_path, command=None):
        self.process = None
        self.status = {"state": "not-started", "droppedPhases": 0, "pid": None}
        self.phase_count = 0
        try:
            self.process = subprocess.Popen(
                command or [sys.executable, "-B", str(pathlib.Path(__file__).with_name("scenario_attempt_forensics.py")),
                            str(destination), str(log_path)],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                close_fds=True)
            self.status.update(state="running", pid=self.process.pid)
            os.set_blocking(self.process.stdin.fileno(), False)
        except (OSError, NotImplementedError) as error:
            self.status.update(state="startup-failed", error=type(error).__name__)
            if self.process is not None:
                try:
                    self.process.stdin.close()
                except OSError:
                    self.status["controlError"] = "close-failed"
                self.stop()

    def stop(self):
        try:
            self.process.kill()
            self.process.wait(timeout=1)
            self.status["exitCode"] = self.process.returncode
        except (OSError, subprocess.TimeoutExpired) as error:
            self.status.update(state="helper-exit-unconfirmed", error=type(error).__name__)

    def phase(self, name, **fields):
        self.phase_count += 1
        message = json.dumps({"phase": name, "monotonic": time.monotonic(), **fields}).encode() + b"\n"
        if (self.process is None or self.status["state"] != "running" or "controlError" in self.status
                or self.phase_count > 12 or len(message) > 512):
            self.status["droppedPhases"] += 1
            return
        try:
            # Nonblocking on supported POSIX and Windows Python 3.12+ pipes.
            # A hung acquisition worker must never stall target termination.
            sent = os.write(self.process.stdin.fileno(), message)
            if sent != len(message):
                self.status["droppedPhases"] += 1
                self.status["controlError"] = "partial-write"
        except (OSError, ValueError):
            self.status["droppedPhases"] += 1

    def finish(self):
        if self.process is None:
            return self.status
        if self.status["state"] != "running":
            return self.status
        self.phase("finish")
        try:
            self.process.stdin.close()
        except OSError:
            self.status["droppedPhases"] += 1
        try:
            self.process.wait(timeout=0.5)
            self.status.update(state="completed" if self.process.returncode == 0 else "acquisition-failed",
                               exitCode=self.process.returncode)
            if self.process.returncode != 0:
                self.status["error"] = {
                    71: "permission-denied", 72: "acquisition-io-failed", 73: "invalid-or-capped-control",
                    74: "worker-lifetime-expired", 75: "control-ended-before-finish",
                }.get(self.process.returncode, "unexpected-helper-exit")
        except subprocess.TimeoutExpired:
            self.status["state"] = "acquisition-deadline"
            self.stop()
        return self.status


def run_attempt(command, log_path, status_path, timeout_seconds, forensic_command=None):
    started = time.monotonic()
    status = {"timedOut": False, "exitCode": None, "pid": None, "cleanup": "not-needed"}
    log_path = pathlib.Path(log_path)
    status_path = pathlib.Path(status_path)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    status_path.parent.mkdir(parents=True, exist_ok=True)
    # Launch before the target so worker startup cannot extend the target's deadline.
    worker = ForensicWorker(status_path.with_name(status_path.name + ".forensics.json"),
                            log_path, forensic_command)
    worker.phase("launch-intent")
    try:
        # A regular file, not a pipe: a descendant inheriting stdout cannot keep tee
        # or communicate() waiting after the test command has already exited.
        with log_path.open("wb") as output:
            kwargs = ({"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
                      if os.name == "nt" else {"start_new_session": True})
            process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT, **kwargs)
            status["pid"] = process.pid
            deadline = time.monotonic() + timeout_seconds
            worker.phase("target-started", pid=process.pid, supervisorPid=os.getpid(), deadline=deadline)
            try:
                status["exitCode"] = process.wait(timeout=max(0, deadline - time.monotonic()))
                worker.phase("root-exited", exitCode=process.returncode)
            except subprocess.TimeoutExpired:
                status["timedOut"] = True
                worker.phase("timeout-detected")
                if os.name == "nt":
                    try:
                        cleanup = subprocess.run(
                            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                            stdout=output, stderr=subprocess.STDOUT, timeout=15, check=False)
                        status["cleanup"] = f"taskkill-exit-{cleanup.returncode}"
                    except (OSError, subprocess.TimeoutExpired) as error:
                        status["cleanup"] = f"taskkill-failed:{type(error).__name__}"
                else:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                        status["cleanup"] = "owned-process-group-killed"
                    except ProcessLookupError:
                        status["cleanup"] = "process-group-already-exited"
                try:
                    status["exitCode"] = process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    status["cleanup"] += ";root-kill-issued"
                output.write(b"\nScenario attempt exceeded its subprocess completion deadline.\n")
                worker.phase("cleanup-complete", exitCode=status["exitCode"])
    except OSError as error:
        status["launchError"] = str(error)
    finally:
        status["forensics"] = worker.finish()
        status["durationSeconds"] = round(time.monotonic() - started, 3)
        status["timeoutSeconds"] = timeout_seconds
        print(f"scenario-attempt phase=target-finished timedOut={status['timedOut']} "
              f"exitCode={status['exitCode']} forensics={status['forensics']['state']}", flush=True)
        partial = status_path.with_name(status_path.name + ".partial")
        partial.write_text(json.dumps(status, indent=2), encoding="utf-8")
        os.replace(partial, status_path)
    if status["timedOut"]:
        return 124
    return status["exitCode"] if status["exitCode"] is not None else 125


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", required=True)
    parser.add_argument("--status", required=True)
    parser.add_argument("--timeout-seconds", type=int, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if args.timeout_seconds < 1 or not command:
        parser.error("a positive timeout and command are required")
    return run_attempt(command, args.log, args.status, args.timeout_seconds)


if __name__ == "__main__":
    raise SystemExit(main())
