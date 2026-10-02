#!/usr/bin/env python3
"""Classify one perf-sidecar capture and aggregate per-capture outcomes (issue #934).

Outcome statuses (exactly one per planned capture):
  passed       collector-specific observations and provenance were asserted
  unsupported  an explicit, classified prerequisite was unavailable (not a compatibility claim)
  failed       the collector ran but an assertion or an unclassified error failed
  timeout      the capture did not finish within its bound

A zero exit code, empty JSON or an idle target never counts as passed.
"""

import argparse
import json
from pathlib import Path
import sys

PREREQUISITE_ERROR_KINDS = {"PermissionDenied", "UnsupportedPrerequisite", "PrerequisiteUnavailable", "MissingPerf"}

# kind -> (data field holding the observation count, minimum count, required probed symbol)
COUNT_RULES = {
    "cpu": ("totalSamples", 20, None),
    "native-alloc": ("totalSampledAllocations", 1, "malloc"),
    "native-lock-contention": ("totalSampledLockCalls", 1, "pthread_mutex_lock"),
}


def load_json(path):
    try:
        text = Path(path).read_text(encoding="utf-8")
        return json.loads(text) if text.strip() else None
    except (OSError, ValueError):
        return None


def activation_summary(path):
    ok = failed = 0
    if path and Path(path).exists():
        for line in Path(path).read_text(encoding="utf-8").splitlines():
            code = line.strip().split(" ")[0]
            if code.startswith("2"):
                ok += 1
            elif code:
                failed += 1
    return {"requestsOk": ok, "requestsFailed": failed}


def _positive(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and value > 0


def evaluate(kind, payload, exit_code, timed_out, activation, expect_error_kind=None, expect_message=None):
    result = {"kind": kind, "exitCode": exit_code, "activation": activation}

    def finish(status, detail):
        result["status"] = status
        result["detail"] = detail
        return result

    if timed_out:
        return finish("timeout", "capture exceeded its bound and was aborted")

    error = payload.get("error") if isinstance(payload, dict) else None

    if expect_error_kind:
        if not isinstance(error, dict):
            return finish("failed", f"expected a classified '{expect_error_kind}' error but the capture reported none")
        message = str(error.get("message", ""))
        if error.get("kind") != expect_error_kind:
            return finish("failed", f"expected error kind '{expect_error_kind}' but got '{error.get('kind')}': {message[:200]}")
        if expect_message and expect_message.lower() not in message.lower():
            return finish("failed", f"error kind matched but message lacks actionable text '{expect_message}': {message[:200]}")
        result["observedError"] = {"kind": error.get("kind"), "message": message[:300]}
        return finish("passed", f"negative control produced the expected '{expect_error_kind}' classification")

    if payload is None:
        return finish("failed", f"no parsable JSON output (exit code {exit_code})")

    if isinstance(error, dict):
        message = str(error.get("message", ""))
        result["observedError"] = {"kind": error.get("kind"), "message": message[:300]}
        if error.get("kind") in PREREQUISITE_ERROR_KINDS:
            return finish("unsupported", f"prerequisite unavailable ({error.get('kind')}): {message[:300]}")
        return finish("failed", f"collector error ({error.get('kind')}): {message[:300]}")

    if exit_code != 0:
        return finish("failed", f"non-zero exit code {exit_code} without a classified error")

    if activation.get("requestsOk", 0) < 1:
        return finish("failed", "no successful workload request was observed during the capture window")

    data = payload.get("data")
    if not isinstance(data, dict):
        return finish("failed", "result has no data object")

    if kind == "off_cpu":
        count = next((data[key] for key in ("totalOffCpuSamples", "totalSamples", "totalWaitSamples") if key in data), None)
        if not _positive(count):
            return finish("failed", f"off-CPU capture reported no wait observations (fields: {sorted(data)[:12]})")
        result["observations"] = count
        return finish("passed", f"{count} off-CPU observations during activated workload")

    field, minimum, symbol = COUNT_RULES[kind]
    count = data.get(field)
    if not isinstance(count, (int, float)) or count < minimum:
        return finish("failed", f"{field}={count!r} is below the required minimum {minimum}")
    if symbol:
        probed = [str(item) for item in (data.get("probedFunctions") or [])]
        if symbol not in probed:
            return finish("failed", f"expected probed function '{symbol}' but saw {probed}")
    if kind == "cpu":
        provenance = json.dumps(data.get("evidence", {})) + str(payload.get("summary", ""))
        if "LinuxPerf" not in provenance:
            return finish("failed", "CPU result lacks Linux perf evidence provenance")
    result["observations"] = count
    return finish("passed", f"{field}={count} during activated workload")


CAPABILITY_NAMES = [
    "CHOWN", "DAC_OVERRIDE", "DAC_READ_SEARCH", "FOWNER", "FSETID", "KILL", "SETGID", "SETUID", "SETPCAP",
    "LINUX_IMMUTABLE", "NET_BIND_SERVICE", "NET_BROADCAST", "NET_ADMIN", "NET_RAW", "IPC_LOCK", "IPC_OWNER",
    "SYS_MODULE", "SYS_RAWIO", "SYS_CHROOT", "SYS_PTRACE", "SYS_PACCT", "SYS_ADMIN", "SYS_BOOT", "SYS_NICE",
    "SYS_RESOURCE", "SYS_TIME", "SYS_TTY_CONFIG", "MKNOD", "LEASE", "AUDIT_WRITE", "AUDIT_CONTROL", "SETFCAP",
    "MAC_OVERRIDE", "MAC_ADMIN", "SYSLOG", "WAKE_ALARM", "BLOCK_SUSPEND", "AUDIT_READ", "PERFMON", "BPF",
    "CHECKPOINT_RESTORE",
]


def decode_capabilities(hex_mask):
    mask = int(hex_mask, 16)
    return [name for bit, name in enumerate(CAPABILITY_NAMES) if mask & (1 << bit)]


def build_topology(raw_path):
    """Turn `key=value` probe lines (repeated sections prefixed `name.key`) into a nested report."""
    topology = {}
    for line in Path(raw_path).read_text(encoding="utf-8").splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        section, _, field = key.partition(".")
        topology.setdefault(section, {})[field] = value.strip()
    for section in topology.values():
        if "CapEff" in section:
            try:
                section["effectiveCapabilities"] = decode_capabilities(section["CapEff"])
            except ValueError:
                section["effectiveCapabilities"] = None
    return topology


def build_report(directory):
    outcomes = []
    for path in sorted(Path(directory).glob("*.outcome.json")):
        outcomes.append(json.loads(path.read_text(encoding="utf-8")))
    counts = {}
    for outcome in outcomes:
        counts[outcome["status"]] = counts.get(outcome["status"], 0) + 1
    return {"captures": len(outcomes), "counts": counts, "outcomes": outcomes}


def render_markdown(report, planned):
    lines = ["### Perf sidecar smoke (advisory)", ""]
    seen = {outcome["name"] for outcome in report["outcomes"]}
    missing = [name for name in planned if name not in seen]
    if missing:
        lines += [f"**Incomplete coverage — no outcome recorded for: {', '.join(missing)}**", ""]
    lines += ["| Capture | Status | Detail |", "|---|---|---|"]
    for outcome in report["outcomes"]:
        detail = outcome["detail"].replace("|", "\\|").replace("\n", " ")
        lines.append(f"| {outcome['name']} | {outcome['status']} | {detail[:300]} |")
    for name in missing:
        lines.append(f"| {name} | missing | no outcome recorded |")
    return "\n".join(lines) + "\n"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    cap = sub.add_parser("capture")
    cap.add_argument("--name", required=True)
    cap.add_argument("--kind", required=True, choices=sorted(set(COUNT_RULES) | {"off_cpu"}))
    cap.add_argument("--json", required=True)
    cap.add_argument("--exit-code", type=int, required=True)
    cap.add_argument("--timed-out", action="store_true")
    cap.add_argument("--activation")
    cap.add_argument("--expect-error-kind")
    cap.add_argument("--expect-message")
    cap.add_argument("--out", required=True)

    top = sub.add_parser("topology")
    top.add_argument("--raw", required=True)
    top.add_argument("--out", required=True)

    rep = sub.add_parser("report")
    rep.add_argument("--dir", required=True)
    rep.add_argument("--planned", nargs="+", required=True)
    rep.add_argument("--summary-json", required=True)
    rep.add_argument("--summary-md", required=True)

    args = parser.parse_args(argv)
    if args.command == "capture":
        outcome = evaluate(
            args.kind, load_json(args.json), args.exit_code, args.timed_out,
            activation_summary(args.activation), args.expect_error_kind, args.expect_message)
        outcome["name"] = args.name
        Path(args.out).write_text(json.dumps(outcome, indent=2), encoding="utf-8")
        print(f"{args.name}: {outcome['status']} - {outcome['detail']}")
        return 0

    if args.command == "topology":
        Path(args.out).write_text(json.dumps(build_topology(args.raw), indent=2), encoding="utf-8")
        return 0

    report = build_report(args.dir)
    report["planned"] = args.planned
    seen = {outcome["name"] for outcome in report["outcomes"]}
    report["missing"] = [name for name in args.planned if name not in seen]
    Path(args.summary_json).write_text(json.dumps(report, indent=2), encoding="utf-8")
    Path(args.summary_md).write_text(render_markdown(report, args.planned), encoding="utf-8")
    bad = report["counts"].get("failed", 0) + report["counts"].get("timeout", 0) + len(report["missing"])
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
