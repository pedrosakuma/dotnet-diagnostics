#!/usr/bin/env python3
"""One sequential, fail-closed Linux x64 sidecar slot per .NET 8/9/10 (advisory)."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

from test_evidence import require, verify_log, verify_trx

SPEC = importlib.util.spec_from_file_location(
    "attempt_supervisor", Path(__file__).with_name("run-scenario-attempt.py"))
SUPERVISOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUPERVISOR)

CLASS = "DotnetDiagnostics.Core.Tests.CrossVersionLiveSidecarTests"
METHODS = ("LiveHeap_RetainsNamedPopulation", "LiveThreads_FindNamedGenericFrame",
           "LiveAsync_FindsPendingFixture", "LiveGenerics_ResolvesConcreteInt32")
EXPECTED = {f"{CLASS}.{method}" for method in METHODS}
SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0.201"
TARGET_IMAGES = {major: f"mcr.microsoft.com/dotnet/runtime:{major}.0" for major in (8, 9, 10)}
TEST_DLL = Path("tests/DotnetDiagnostics.Core.Tests/bin/Release/net10.0/DotnetDiagnostics.Core.Tests.dll")
LABEL = "dotnet-diagnostics.live-compat"


class PrerequisiteError(RuntimeError):
    pass


class AttemptFailure(RuntimeError):
    def __init__(self, message, outcome):
        super().__init__(message)
        self.outcome = outcome


def watchdog(_signal, _frame):
    raise TimeoutError("Slot/global wall-clock watchdog expired")


def interrupted(_signal, _frame):
    raise AttemptFailure("Runner interrupted; remaining slots not executed", "aborted")


def failure_outcome(trx, log):
    try:
        verify_log(log)
        results = ET.parse(trx).findall(
            "t:Results/t:UnitTestResult", {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"})
        return "assertion-failure" if any(row.get("outcome") == "Failed" for row in results) else "aborted"
    except (OSError, ValueError, ET.ParseError):
        return "aborted"


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def command(args, timeout=30):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout, check=False)
    if result.returncode:
        raise RuntimeError(f"{args[0:2]} exited {result.returncode}: {result.stderr[-2000:]}")
    return result.stdout.strip()


def image_metadata(image):
    data = json.loads(command(["docker", "image", "inspect", image]))[0]
    if data["Os"] != "linux" or data["Architecture"] != "amd64":
        raise PrerequisiteError(f"{image} must be Linux amd64")
    return {key: data.get(key) for key in ("Id", "RepoDigests", "Os", "Architecture")}


def validate_inventory(trx, discovery, log):
    verify_log(log)
    inventory = verify_trx(trx, discovery, EXPECTED)
    require(len(inventory) == 4 and {name for name, _ in inventory} == EXPECTED,
            "The provisioned slot must execute exactly the four expected cases")
    require(all(outcome == "Passed" for _, outcome in inventory), "No skips count as coverage")
    return inventory


def container_metadata(identity):
    data = json.loads(command(["docker", "inspect", identity]))[0]
    host = data["HostConfig"]
    return {"id": data["Id"], "image": data["Image"], "user": data["Config"]["User"],
            "pidMode": host["PidMode"], "capAdd": host["CapAdd"], "capDrop": host["CapDrop"],
            "privileged": host["Privileged"], "networkMode": host["NetworkMode"],
            "state": {key: data["State"].get(key) for key in ("Status", "Pid", "ExitCode", "OOMKilled")}}


def cleanup_container(name, owner):
    # A create may have succeeded even when its client timed out. The exact generated name
    # plus ownership label recover that case without listing/deleting unrelated containers.
    result = subprocess.run(["docker", "inspect", name], capture_output=True, text=True, timeout=15)
    if result.returncode:
        if "No such object" in result.stderr:
            return "absent"
        raise RuntimeError(f"Cannot verify cleanup for {name}: {result.stderr[-1000:]}")
    data = json.loads(result.stdout)[0]
    require(data["Config"]["Labels"].get(LABEL) == owner, "Refusing cleanup of unowned container")
    command(["docker", "rm", "--force", data["Id"]], timeout=15)
    return "removed"


def create_options(name, owner, memory):
    return ["docker", "create", "--name", name, "--label", f"{LABEL}={owner}",
            "--user", "0:0", "--network", "none", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--read-only",
            "--pids-limit", "128", "--memory", memory, "--cpus", "2",
            "--ulimit", "core=0", "--log-opt", "max-size=1m", "--log-opt", "max-file=1",
            "--tmpfs", "/scratch:rw,nosuid,nodev,size=128m",
            "--env", "TMPDIR=/diagnostics", "--env", "DOTNET_CLI_HOME=/scratch",
            "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "--env", "DOTNET_NOLOGO=1"]


def mount(source, destination, readonly=True):
    return ["--mount", f"type=bind,src={source},dst={destination}" + (",readonly" if readonly else "")]


def run_slot(repo, root, major, owner, global_deadline):
    slot = root / f"net{major}"
    slot.mkdir()
    evidence = slot / "evidence"
    evidence.mkdir()
    sockets = slot / "diagnostics"
    sockets.mkdir()
    # UID 0 with ALL capabilities dropped cannot bypass host-owned 0755 permissions.
    # Only these new, fixture-owned directories need container writes.
    evidence.chmod(0o777)
    sockets.chmod(0o777)
    runtime = slot / "runtime"
    runtime.mkdir()
    target_name = f"live-compat-{owner}-{major}-target"
    inspector_name = f"live-compat-{owner}-{major}-inspector"
    result = {"major": major, "outcome": "aborted", "expected": sorted(EXPECTED), "cleanup": {}}
    slot_deadline = min(time.monotonic() + 240, global_deadline)
    signal.setitimer(signal.ITIMER_REAL, max(0.01, slot_deadline - time.monotonic()))

    def remaining(limit):
        seconds = min(limit, int(slot_deadline - time.monotonic()))
        if seconds < 1:
            raise TimeoutError("Slot/global watchdog expired")
        return seconds

    def attempt(label, args, limit):
        rc = SUPERVISOR.run_attempt(args, slot / f"{label}.log", slot / f"{label}.status.json", remaining(limit))
        if rc == 124:
            raise TimeoutError(f"{label} watchdog expired")
        if rc:
            raise AttemptFailure(f"{label} exited {rc}; see retained log/status",
                                 failure_outcome(evidence / "live.trx", slot / f"{label}.log"))

    try:
        sample = repo / f"samples/MultiVersionSample/bin/Release/net{major}.0"
        target = command(create_options(target_name, owner, "256m") +
                         mount(sample, "/sample") + mount(sockets, "/diagnostics", False) +
                         ["--env", "DOTNET_ROLL_FORWARD=LatestPatch", "--entrypoint", "dotnet",
                          TARGET_IMAGES[major], "/sample/MultiVersionSample.dll", "--live-compatibility"],
                         remaining(20))
        result["target"] = container_metadata(target)
        # Copy the target-local DAC before starting its finite lifetime. No downloads, restore,
        # package caches or credentials enter either container.
        command(["docker", "cp", f"{target}:/usr/share/dotnet/shared/Microsoft.NETCore.App/.", str(runtime)],
                remaining(30))
        versions = list(runtime.iterdir())
        require(len(versions) == 1 and versions[0].name.startswith(f"{major}."),
                "Target image must contain exactly the expected runtime major")
        version = versions[0].name
        dac = versions[0] / "libmscordaccore.so"
        result["runtime"] = {"version": version, "dacSha256": hashlib.sha256(dac.read_bytes()).hexdigest()}
        inspector = command(create_options(inspector_name, owner, "1g") +
                            ["--cap-add", "SYS_PTRACE", "--pid", f"container:{target}"] +
                            mount(sample, "/sample") + mount(sockets, "/diagnostics", False) +
                            mount(repo / TEST_DLL.parent, "/tests") + mount(evidence, "/evidence", False) +
                            mount(versions[0], f"/usr/share/dotnet/shared/Microsoft.NETCore.App/{version}") +
                            ["--env", f"DOTNET_DIAGNOSTICS_LIVE_COMPAT_MAJOR={major}",
                             "--entrypoint", "/bin/sh", SDK_IMAGE, "-c", "sleep 180"],
                            remaining(20))
        command(["docker", "start", target], remaining(15))
        ready_deadline = min(time.monotonic() + 15, slot_deadline)
        while True:
            log = command(["docker", "logs", target], remaining(5))
            if "\nREADY" in log:
                require(f"Runtime: .NET {major}." in log and "PID: 1" in log
                        and "Fixture: live-compatibility;" in log, "Unexpected target identity")
                break
            if time.monotonic() >= ready_deadline:
                raise TimeoutError("Target readiness deadline expired")
            time.sleep(0.2)
        command(["docker", "start", inspector], remaining(15))
        result["target"] = container_metadata(target)
        result["inspector"] = container_metadata(inspector)
        result["sdk"] = command(["docker", "exec", inspector, "dotnet", "--version"], remaining(10))
        require(result["sdk"] == "10.0.201", "Inspector must use pinned SDK 10.0.201")
        result["topology"] = command(["docker", "exec", inspector, "/bin/sh", "-c",
                                     "id; uname -r; grep -E '^(Uid|Gid|CapEff|NSpid):' /proc/1/status /proc/self/status; "
                                     "readlink /proc/1/ns/pid /proc/self/ns/pid"], remaining(10))
        base = ["docker", "exec", inspector, "dotnet", "vstest", "/tests/" + TEST_DLL.name,
                f"/TestCaseFilter:FullyQualifiedName~{CLASS}"]
        attempt("discovery", base + ["/ListTests"], 20)
        attempt("tests", base + ["/Logger:trx;LogFileName=live.trx", "/ResultsDirectory:/evidence"], 140)
        result["inventory"] = validate_inventory(evidence / "live.trx", slot / "discovery.log", slot / "tests.log")
        result["outcome"] = "passed"
    except (TimeoutError, subprocess.TimeoutExpired) as error:
        result.update(outcome="timeout", error=str(error))
    except AttemptFailure as error:
        result.update(outcome=error.outcome, error=str(error))
    except Exception as error:
        result.update(outcome="aborted", error=str(error))
    finally:
        # Cleanup has its own bounded calls and must not be interrupted by an expired capture
        # alarm. Each role has at most 70 seconds of metadata/log/removal work.
        signal.setitimer(signal.ITIMER_REAL, 0)
        for role, name in (("inspector", inspector_name), ("target", target_name)):
            try:
                result[f"{role}Final"] = container_metadata(name)
                if role == "target":
                    (slot / "target.log").write_text(command(["docker", "logs", name], 10), encoding="utf-8")
            except Exception as error:
                result[f"{role}FinalError"] = str(error)
            try:
                result["cleanup"][role] = cleanup_container(name, owner)
            except Exception as error:
                result["cleanup"][role] = f"failed: {error}"
                result["outcome"] = "cleanup-failure"
        write_json(slot / "result.json", result)
        # Runtime binaries and socket directory are not evidence or upload artifacts.
        shutil.rmtree(runtime)
        shutil.rmtree(sockets)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New persistent result directory")
    args = parser.parse_args(argv)
    repo = Path(__file__).resolve().parent.parent
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    owner = uuid.uuid4().hex[:12]
    report = {"owner": owner, "slots": [], "expectedMajors": [8, 9, 10], "outcome": "aborted"}
    signal.signal(signal.SIGALRM, watchdog)
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    global_deadline = time.monotonic() + 900
    signal.setitimer(signal.ITIMER_REAL, 60)
    try:
        if platform.system() != "Linux" or platform.machine() not in ("x86_64", "amd64"):
            raise PrerequisiteError("Linux x64 host required")
        if not (repo / TEST_DLL).is_file() or any(
                not (repo / f"samples/MultiVersionSample/bin/Release/net{major}.0/MultiVersionSample.dll").is_file()
                for major in TARGET_IMAGES):
            raise PrerequisiteError("Build Core tests and all MultiVersionSample TFMs first")
        try:
            report["docker"] = command(["docker", "version", "--format", "{{.Server.Version}} {{.Server.Os}}/{{.Server.Arch}}"])
            report["images"] = {image: image_metadata(image) for image in [SDK_IMAGE, *TARGET_IMAGES.values()]}
        except (TimeoutError, subprocess.TimeoutExpired, AttemptFailure):
            raise
        except (OSError, RuntimeError) as error:
            raise PrerequisiteError(f"Docker daemon/local images unavailable: {error}") from error
        report["commit"] = command(["git", "-C", str(repo), "rev-parse", "HEAD"])
        report["dirty"] = command(["git", "-C", str(repo), "status", "--porcelain"])
        report["kernel"] = platform.release()
        report["clrmdSha256"] = hashlib.sha256((repo / TEST_DLL.parent / "Microsoft.Diagnostics.Runtime.dll").read_bytes()).hexdigest()
        for major in TARGET_IMAGES:
            if time.monotonic() >= global_deadline:
                report["slots"].append({"major": major, "outcome": "not-executed-global-timeout"})
                continue
            report["slots"].append(run_slot(repo, root, major, owner, global_deadline))
            if report["slots"][-1]["outcome"] in ("cleanup-failure", "aborted"):
                break
        report["outcome"] = ("passed" if len(report["slots"]) == 3 and
                             all(slot["outcome"] == "passed" for slot in report["slots"]) else "failed")
    except PrerequisiteError as error:
        report.update(outcome="missing-or-unsupported-prerequisite", error=str(error))
    except (TimeoutError, subprocess.TimeoutExpired) as error:
        report.update(outcome="timeout", error=str(error))
    except Exception as error:
        report.update(outcome="aborted", error=str(error))
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        executed = {slot["major"] for slot in report["slots"]}
        report["missingMajors"] = sorted(set(TARGET_IMAGES) - executed)
        write_json(root / "manifest.json", report)
        print(json.dumps({"outcome": report["outcome"], "manifest": str(root / "manifest.json")}))
    return 0 if report["outcome"] == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
