"""Bounded owned-process metadata; no debugger, stacks, command lines or environment."""
import os
import json
import pathlib
import time

MAX_PROCESSES = 64
MAX_THREADS = 128
MAX_DEPTH = 8
MAX_ENUMERATED = 4096
MAX_DISCOVERY_SECONDS = 0.05
MAX_SNAPSHOT_BYTES = 18000
IS_WINDOWS = os.name == "nt"


def bounded_json(value, limit):
    """Stop encoding at the byte cap rather than building an unlimited JSON string."""
    result = bytearray()
    for part in json.JSONEncoder(ensure_ascii=True, separators=(",", ":")).iterencode(value):
        encoded = part.encode("ascii")
        if len(result) + len(encoded) > limit:
            raise ValueError("forensic artifact byte cap")
        result.extend(encoded)
    return bytes(result)


def append_bounded(items, value, limit):
    try:
        bounded_json([*items, value], limit)
    except ValueError:
        return False
    items.append(value)
    return True


def linux_identity(pid):
    with (pathlib.Path("/proc") / str(pid) / "stat").open() as handle:
        text = handle.read(4096)
    closing = text.rindex(")")
    fields = text[closing + 2:].split()
    return {
        "pid": pid, "parentPid": int(fields[1]), "creation": fields[19],
        "name": text[text.index("(") + 1:closing][:80], "state": fields[0],
    }


def windows_identity(pid):
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.GetProcessTimes.restype = wintypes.BOOL
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    handle = kernel.OpenProcess(0x00100000 | 0x1000, False, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *(ctypes.byref(value) for value in times)):
            raise ctypes.WinError(ctypes.get_last_error())
        created = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        wait = kernel.WaitForSingleObject(handle, 0)
        if wait not in (0, 258):
            raise ctypes.WinError(ctypes.get_last_error())
        return {"pid": pid, "creation": str(created),
                "state": "exited" if wait == 0 else "alive",
                "waitHint": "unavailable", "stdio": "remote-handle-identity-unavailable"}
    finally:
        kernel.CloseHandle(handle)


def windows_relations(deadline):
    """Toolhelp only exposes global discovery; discard all content except PID/PPID."""
    import ctypes
    from ctypes import wintypes

    class Entry(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
            ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.Process32FirstW.restype = wintypes.BOOL
    kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.Process32NextW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == wintypes.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    relations = []
    truncated = False
    try:
        entry = Entry()
        entry.dwSize = ctypes.sizeof(entry)
        more = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            if len(relations) >= MAX_ENUMERATED or time.monotonic() >= deadline:
                truncated = True
                break
            relations.append((entry.th32ProcessID, entry.th32ParentProcessID))
            more = kernel.Process32NextW(snapshot, ctypes.byref(entry))
        if not more and ctypes.get_last_error() not in (0, 18):
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        kernel.CloseHandle(snapshot)
    return relations, truncated


class OwnedProcesses:
    def __init__(self, root_pid, supervisor_pid, log_path):
        self.root_pid = root_pid
        self.supervisor_pid = supervisor_pid
        self.log_path = str(pathlib.Path(log_path).resolve())
        self.known = {}
        self.limitations = {}
        self.identity = windows_identity if IS_WINDOWS else linux_identity

    def note(self, kind):
        # Only fixed identifiers emitted by this module; never exception text/paths.
        if kind in self.limitations:
            self.limitations[kind] = min(1_000_000, self.limitations[kind] + 1)
        elif len(self.limitations) < 24:
            self.limitations[kind] = 1

    def _identity(self, pid):
        try:
            return self.identity(pid)
        except PermissionError:
            self.note("permission-denied")
        except (FileNotFoundError, ProcessLookupError):
            self.note("process-exited-before-observation")
        except (OSError, ValueError, IndexError):
            self.note("identity-unavailable")
        return None

    def discover(self):
        deadline = time.monotonic() + MAX_DISCOVERY_SECONDS
        relations = None
        snapshot_time = None
        if IS_WINDOWS:
            try:
                snapshot_time = time.time_ns() // 100 + 116444736000000000
                relations, truncated = windows_relations(deadline)
                if truncated:
                    self.note("discovery-enumeration-incomplete")
            except PermissionError:
                self.note("permission-denied")
                return
            except OSError:
                self.note("discovery-unavailable")
                return
        if self.root_pid not in self.known:
            root = self._identity(self.root_pid)
            parent = (root or {}).get("parentPid")
            if relations is not None:
                parent = next((ppid for pid, ppid in relations if pid == self.root_pid), None)
            if (root is None or parent != self.supervisor_pid or self.root_pid == os.getpid()
                    or (snapshot_time is not None and int(root["creation"]) > snapshot_time)):
                self.note("root-ownership-not-established")
                return
            self.known[self.root_pid] = dict(root, parentPid=parent, depth=0)
        queue = list(self.known.values())
        thread_budget = MAX_THREADS
        while queue:
            if time.monotonic() >= deadline:
                self.note("discovery-time-budget")
                break
            expected = queue.pop(0)
            current = self._identity(expected["pid"])
            if current is None:
                continue
            if current["creation"] != expected["creation"]:
                self.note("pid-identity-changed")
                continue
            if expected["depth"] >= MAX_DEPTH:
                self.note("discovery-depth-cap")
                continue
            children = []
            if relations is not None:
                for pid, parent in relations:
                    if time.monotonic() >= deadline:
                        self.note("discovery-time-budget")
                        break
                    if parent == expected["pid"]:
                        if len(children) >= MAX_PROCESSES:
                            self.note("process-cap")
                            break
                        children.append(pid)
            else:
                try:
                    tasks = pathlib.Path("/proc") / str(expected["pid"]) / "task"
                    with os.scandir(tasks) as entries:
                        for entry in entries:
                            if thread_budget <= 0 or time.monotonic() >= deadline:
                                self.note("discovery-thread-or-time-cap")
                                break
                            thread_budget -= 1
                            path = pathlib.Path(entry.path) / "children"
                            with path.open() as handle:
                                text = handle.read(4096)
                            if len(text) == 4096:
                                self.note("child-list-truncated")
                            for value in text.split():
                                if len(children) >= MAX_PROCESSES:
                                    self.note("process-cap")
                                    break
                                if value.isdecimal():
                                    children.append(int(value))
                except PermissionError:
                    self.note("permission-denied")
                except OSError:
                    self.note("child-list-unavailable")
            for pid in children:
                if pid in self.known or pid == os.getpid():
                    continue
                if len(self.known) >= MAX_PROCESSES:
                    self.note("process-cap")
                    break
                if time.monotonic() >= deadline:
                    self.note("discovery-time-budget")
                    break
                child = self._identity(pid)
                # Revalidate the recorded parent's lifetime before assigning ancestry.
                parent = self._identity(expected["pid"])
                if child is None or parent is None or parent["creation"] != expected["creation"]:
                    self.note("ancestry-race")
                    continue
                if not IS_WINDOWS and child["parentPid"] != expected["pid"]:
                    self.note("ancestry-race")
                    continue
                if snapshot_time is not None and (
                        int(child["creation"]) > snapshot_time
                        or int(child["creation"]) < int(expected["creation"])):
                    self.note("ancestry-creation-time-not-established")
                    continue
                child = dict(child, parentPid=expected["pid"], depth=expected["depth"] + 1)
                self.known[pid] = child
                queue.append(child)

    def snapshot(self):
        deadline = time.monotonic() + MAX_DISCOVERY_SECONDS
        result = []
        thread_budget = MAX_THREADS
        for expected in self.known.values():
            if len(result) >= MAX_PROCESSES or time.monotonic() >= deadline:
                self.note("snapshot-process-or-time-cap")
                break
            current = self._identity(expected["pid"])
            record = {"pid": expected["pid"], "creation": expected["creation"],
                      "parentPidWhenObserved": expected["parentPid"], "depth": expected["depth"]}
            if current is None:
                record["state"] = "unavailable-or-exited"
            elif current["creation"] != expected["creation"]:
                self.note("pid-identity-changed")
                record["state"] = "identity-changed-not-inspected"
            else:
                record.update(current)
                if not IS_WINDOWS:
                    pid_path = pathlib.Path("/proc") / str(expected["pid"])
                    stdio = {}
                    for number in range(3):
                        try:
                            target = os.readlink(pid_path / "fd" / str(number))
                            stdio[str(number)] = ("attempt-log" if target == self.log_path
                                else target[:80] if target.startswith("pipe:[") else "other")
                        except PermissionError:
                            stdio[str(number)] = "permission-denied"
                        except OSError:
                            stdio[str(number)] = "unavailable"
                    record["stdio"] = stdio
                    threads = []
                    try:
                        with os.scandir(pid_path / "task") as entries:
                            for entry in entries:
                                if thread_budget <= 0 or time.monotonic() >= deadline:
                                    self.note("snapshot-thread-or-time-cap")
                                    break
                                thread_budget -= 1
                                with (pathlib.Path(entry.path) / "wchan").open() as handle:
                                    hint = handle.read(80)
                                if not append_bounded(
                                        threads, {"tid": int(entry.name), "waitChannel": hint,
                                                  "stack": "unavailable-not-collected"}, MAX_SNAPSHOT_BYTES // 2):
                                    self.note("snapshot-thread-byte-cap")
                                    break
                    except PermissionError:
                        self.note("permission-denied")
                    except (OSError, ValueError):
                        self.note("thread-hints-unavailable")
                    record["threads"] = threads
                    # Discard enriched fields if the PID changed during procfs reads.
                    after = self._identity(expected["pid"])
                    if after is None or after["creation"] != expected["creation"]:
                        record = {"pid": expected["pid"], "creation": expected["creation"],
                                  "state": "identity-lost-during-enrichment"}
            if not append_bounded(result, record, MAX_SNAPSHOT_BYTES - 2048):
                self.note("snapshot-byte-cap")
                break
        return {
            "quality": "best-effort-incomplete",
            "ancestry": "only-observed-while-owned; short-lived/reparented-unseen-descendants-may-be-missed",
            "stackCapture": "unavailable-not-collected",
            "threadHints": "procfs-wchan-not-a-stack" if not IS_WINDOWS else "unavailable-on-windows",
            "processes": result, "limitations": dict(self.limitations),
        }
