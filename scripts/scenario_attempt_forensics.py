"""Best-effort forensic worker; the supervisor never waits for acquisition at its target deadline."""
import json
import os
import pathlib
import sys
import time

from scenario_process_evidence import OwnedProcesses, bounded_json

MAX_BYTES = 65536
MAX_PHASES = 12
MAX_PHASE_BYTES = 512


def encoded_bounded(value):
    return bounded_json(value, MAX_BYTES)


def insert_bounded(document, key, value):
    try:
        encoded_bounded({**document, key: value})
    except ValueError:
        document["byteCapReached"] = True
        return False
    document[key] = value
    return True


def write_atomic(path, document):
    data = encoded_bounded(document)
    partial = path.with_name(path.name + ".partial")
    with partial.open("wb") as handle:
        handle.write(data)
    os.replace(partial, path)


def main():
    destination = pathlib.Path(sys.argv[1])
    os.set_blocking(sys.stdin.fileno(), False)
    document = {"schemaVersion": 1, "phases": [], "byteCapReached": False,
                "filesystemLimit": "best-effort helper writes; partial artifact may survive external termination"}
    pending = b""
    tracker = None
    end = time.monotonic() + 30
    next_discovery = 0
    next_snapshot = 0
    target_deadline = None
    deadline_snapshot = False
    while time.monotonic() < end:
        try:
            chunk = os.read(sys.stdin.fileno(), MAX_PHASE_BYTES)
        except BlockingIOError:
            chunk = None
        if chunk == b"":
            return 75
        if chunk:
            if len(pending) + len(chunk) > MAX_PHASE_BYTES * 2:
                raise ValueError("control byte cap")
            pending += chunk
            while b"\n" in pending:
                line, pending = pending.split(b"\n", 1)
                if len(line) > MAX_PHASE_BYTES:
                    raise ValueError("phase byte cap")
                phase = json.loads(line)
                if len(document["phases"]) >= MAX_PHASES:
                    raise ValueError("phase count cap")
                if not insert_bounded(document, "phases", [*document["phases"], phase]):
                    raise ValueError("phase retention byte cap")
                if phase["phase"] == "target-started":
                    target_deadline = phase["deadline"]
                    end = target_deadline + 22
                    tracker = OwnedProcesses(phase["pid"], phase["supervisorPid"], sys.argv[2])
                if tracker is not None and phase["phase"] in ("root-exited", "timeout-detected", "cleanup-complete"):
                    insert_bounded(document, "finalSnapshot", tracker.snapshot())
                write_atomic(destination, document)
                if phase["phase"] == "finish":
                    return 0
        now = time.monotonic()
        if tracker is not None and now >= next_discovery:
            tracker.discover()
            next_discovery = now + 1
            if now >= next_snapshot:
                insert_bounded(document, "latestSnapshot", tracker.snapshot())
                write_atomic(destination, document)
                next_snapshot = now + 1
        if tracker is not None and not deadline_snapshot and now >= target_deadline - 3:
            insert_bounded(document, "preDeadlineSnapshot", tracker.snapshot())
            write_atomic(destination, document)
            deadline_snapshot = True
        time.sleep(0.02)
    return 74


if __name__ == "__main__":
    try:
        result = main()
    except PermissionError:
        result = 71
    except OSError:
        result = 72
    except (ValueError, KeyError, TypeError):
        result = 73
    raise SystemExit(result)
