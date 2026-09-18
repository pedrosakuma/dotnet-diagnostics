#!/usr/bin/env python3
"""Bound the whole test command, including teardown after xUnit finishes."""
import argparse
import json
import os
import pathlib
import signal
import subprocess
import time


def run_attempt(command, log_path, status_path, timeout_seconds):
    started = time.monotonic()
    status = {"timedOut": False, "exitCode": None, "pid": None, "cleanup": "not-needed"}
    log_path = pathlib.Path(log_path)
    status_path = pathlib.Path(status_path)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    status_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        # A regular file, not a pipe: a descendant inheriting stdout cannot keep tee
        # or communicate() waiting after the test command has already exited.
        with log_path.open("wb") as output:
            kwargs = ({"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
                      if os.name == "nt" else {"start_new_session": True})
            process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT, **kwargs)
            status["pid"] = process.pid
            try:
                status["exitCode"] = process.wait(timeout=timeout_seconds)
            except subprocess.TimeoutExpired:
                status["timedOut"] = True
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
    except OSError as error:
        status["launchError"] = str(error)
    finally:
        status["durationSeconds"] = round(time.monotonic() - started, 3)
        status["timeoutSeconds"] = timeout_seconds
        status_path.write_text(json.dumps(status, indent=2), encoding="utf-8")
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
