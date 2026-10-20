#!/usr/bin/env python3
"""Observation-only CI change-risk classifier.

Records which suites a *hypothetical* selector would recommend for a change,
why, and the estimated savings when measured timings exist. It never gates or
skips anything: every configured job still runs. Any uncertainty (unmapped
path, unreadable diff, missing SHA) recommends full validation.
"""

import argparse
import fnmatch
import json
import os
from pathlib import Path
import subprocess
import sys

POLICY_VERSION = "ci-selection-observer/2"
SCHEMA_VERSION = 1
MAX_LISTED_PATHS = 500

SUITES = (
    "ubuntu-fast", "ubuntu-core", "ubuntu-smoke", "ubuntu-mcp",
    "windows-build", "windows-fast", "windows-core", "windows-smoke", "windows-mcp",
    "docker-smoke", "helm-render-smoke",
)
WINDOWS_SUITES = ("windows-fast", "windows-core", "windows-smoke", "windows-mcp")

# Workflow job id -> suites it executes today (for the actual-execution record).
JOBS = {
    "changes": (),
    "build-test": ("ubuntu-core", "ubuntu-smoke", "ubuntu-mcp"),
    "ubuntu-fast-tests": ("ubuntu-fast",),
    "ubuntu-gate": (),
    "windows-build": ("windows-build",),
    "windows-core-test": ("windows-core", "windows-smoke"),
    "windows-fast-test": ("windows-fast",),
    "windows-mcp-test": ("windows-mcp",),
    "windows-gate": (),
    "docker-smoke": ("docker-smoke",),
    "helm-render-smoke": ("helm-render-smoke",),
    "extension-tests": (),
}

BASELINE = ("ubuntu-fast",)
CLI = BASELINE + ("windows-build", "windows-fast")
BENCHMARK = CLI + ("ubuntu-smoke", "windows-smoke")
MCP = BASELINE + ("ubuntu-mcp", "windows-build", "windows-mcp", "docker-smoke")

# First match wins. A rule with suites=None is full validation (shared-sensitive).
# Anything matching no rule is "unmapped" and also recommends full validation.
RULES = (
    ("docs", ("**/*.md",), (), "markdown only; matches the current docs-only behavior"),
    ("src/cli", ("src/DotnetDiagnostics.Cli/**", "tests/DotnetDiagnostics.Cli.Tests/**"), CLI,
     "CLI references Core only"),
    ("src/benchmarkdotnet", ("src/DotnetDiagnostics.BenchmarkDotNet/**",
                             "tests/DotnetDiagnostics.BenchmarkDotNet.Tests/**"), BENCHMARK,
     "diagnoser plus multi-target runtime smoke"),
    ("src/mcp", ("src/DotnetDiagnostics.Mcp/**", "tests/DotnetDiagnostics.Mcp.IntegrationTests/**"),
     MCP, "MCP server, its integration tests and the image built from it"),
    ("deploy/helm", ("deploy/helm/**", "tests/helm/**"), BASELINE + ("helm-render-smoke",),
     "Helm chart and render smoke"),
    ("deploy/docker", ("deploy/Dockerfile", ".dockerignore", "tests/docker/**"),
     BASELINE + ("docker-smoke",), "container image build and smoke"),
    ("shared/core", ("src/DotnetDiagnostics.Core/**", "tests/DotnetDiagnostics.Core.Tests/**"), None,
     "Core and live-process diagnostics"),
    ("shared/test-infrastructure", ("tests/DotnetDiagnostics.TestSupport/**",
                                    "tests/DotnetDiagnostics.MultiTargetSmoke*/**", "samples/**",
                                    "tests/*.cs", "tests/.editorconfig"), None,
     "shared test infrastructure and live fixtures"),
    ("shared/build-and-dependencies", ("global.json", "Directory.*", "*.slnx", "*.sln", "NuGet.Config",
                                       "nuget.config", ".editorconfig", "**/*.csproj", "**/*.props",
                                       "**/*.targets", "**/packages.lock.json"), None,
     "build configuration or dependencies affect every project"),
    ("shared/ci", (".github/**", "scripts/**"), None, "CI definition or test-evidence tooling"),
)

# Measured wall-clock minutes (min, max) of the step/job in executed main runs.
# Missing entries are explicitly unmeasured; they are never treated as zero.
TIMINGS_FILE = Path(__file__).with_name("ci-selection-timings.json")


def match(path, pattern):
    if pattern.startswith("**/"):
        return fnmatch.fnmatchcase(path, pattern[3:]) or fnmatch.fnmatchcase(path, pattern)
    if pattern.endswith("/**"):
        return path.startswith(pattern[:-2])
    return fnmatch.fnmatchcase(path, pattern)


def classify_path(path):
    normalized = path.replace("\\", "/")
    if normalized.startswith(("/", "../")) or "/../" in normalized:
        return "unmapped", None, "path is not repository-relative"
    for category, patterns, suites, reason in RULES:
        if any(match(normalized, pattern) for pattern in patterns):
            return category, suites, reason
    return "unmapped", None, "no classification rule; conservative full validation"


def close_suites(suites):
    selected = set(suites)
    if selected & set(WINDOWS_SUITES):
        selected.add("windows-build")
    return [suite for suite in SUITES if suite in selected]


def load_timings(path=TIMINGS_FILE):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    return data


def estimate_savings(skipped, timings):
    """Return the runner-minute range avoided; None bounds mean nothing measured."""
    measured = timings.get("suites", {})
    low = high = 0.0
    unmeasured = []
    for suite in skipped:
        entry = measured.get(suite)
        if entry is None:
            unmeasured.append(suite)
        else:
            low += entry["minMinutes"]
            high += entry["maxMinutes"]
    measured_count = len(skipped) - len(unmeasured)
    return {
        "unit": "runner-minutes (sum of per-suite durations; not wall-clock acceleration)",
        "minMinutes": round(low, 1) if measured_count or not skipped else None,
        "maxMinutes": round(high, 1) if measured_count or not skipped else None,
        "complete": not unmeasured,
        "unmeasuredSuites": unmeasured,
        "basis": {"runs": timings.get("sourceRuns", []), "note": timings.get("note", "")},
    }


def recommend(paths, timings):
    """Pure classification of a non-empty list of changed paths."""
    entries = []
    union = set()
    full_reasons = []
    for path in paths:
        category, suites, reason = classify_path(path)
        entries.append({"path": path, "category": category, "reason": reason})
        if suites is None:
            full_reasons.append(f"{category}: {path}")
        else:
            union.update(suites)
    full = bool(full_reasons)
    suites = list(SUITES) if full else close_suites(union)
    skipped = [suite for suite in SUITES if suite not in suites]
    return {
        "hypothetical": True,
        "fullValidation": full,
        "suites": suites,
        "wouldSkipSuites": skipped,
        "reasons": full_reasons[:50] if full else ["every changed path is mapped to an isolated category"],
        "classifications": entries[:MAX_LISTED_PATHS],
        "classificationsTruncated": len(entries) > MAX_LISTED_PATHS,
    }, estimate_savings(skipped, timings)


def unavailable(reason, timings):
    return {
        "hypothetical": True,
        "fullValidation": True,
        "suites": list(SUITES),
        "wouldSkipSuites": [],
        "reasons": [f"change set unavailable: {reason}"],
        "classifications": [],
        "classificationsTruncated": False,
    }, {
        "unit": "runner-minutes",
        "minMinutes": None,
        "maxMinutes": None,
        "complete": False,
        "unmeasuredSuites": [],
        "basis": {"runs": timings.get("sourceRuns", []), "note": f"not assessed: {reason}"},
    }


def git(args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def changed_paths(base, head, merge_base=True):
    for sha in (base, head):
        if not sha or set(sha) <= {"0"}:
            raise ValueError("base/head SHA missing or all-zero")
    # PRs compare against the merge base; pushes compare the exact tree transition so a
    # force-push cannot hide files that existed only on the old side.
    spec = f"{base}...{head}" if merge_base else f"{base}..{head}"
    out = subprocess.run(["git", "diff", "--name-only", "--no-renames", "-z", spec],
                         check=True, capture_output=True, text=True).stdout
    paths = [p for p in out.split("\0") if p]
    if not paths:
        raise ValueError("diff produced no changed files")
    return paths


def actual_execution(needs, extension_only=False):
    """Record what ran; `needs` maps job id -> {result}. Missing means unknown."""
    jobs = {}
    for job, suites in JOBS.items():
        entry = needs.get(job) if isinstance(needs, dict) else None
        result = entry.get("result") if isinstance(entry, dict) else None
        jobs[job] = {"result": result or "unknown", "suites": list(suites)}
    return {
        "selectionApplied": extension_only,
        "selectionPolicy": "ci-extension-pr/1" if extension_only else None,
        "note": ("Bounded extension-only PR selection applied; extension-tests replaces unrelated "
                 ".NET/deployment suites. The general recommendation remains hypothetical."
                 if extension_only else
                 "No selection applied; results below record actual configured job outcomes."),
        "jobs": jobs,
    }


def build_report(env, needs, timings):
    event = env.get("GITHUB_EVENT_NAME", "")
    base = env.get("CI_BASE_SHA", "")
    head = env.get("CI_HEAD_SHA", "")
    tested = env.get("GITHUB_SHA", "")
    notes = []
    checkout = None
    try:
        checkout = git(["rev-parse", "HEAD"]).strip()
    except (OSError, subprocess.CalledProcessError) as error:
        notes.append(f"checked-out SHA unavailable: {error}")
    try:
        recommendation, savings = recommend(
            changed_paths(base, head, merge_base=event == "pull_request"), timings)
        status = "assessed"
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        recommendation, savings = unavailable(str(error).splitlines()[0] or "git diff failed", timings)
        status = "unavailable"
    if checkout and tested and checkout != tested:
        notes.append("checked-out SHA differs from the tested SHA")
    return {
        "schemaVersion": SCHEMA_VERSION,
        "policyVersion": POLICY_VERSION,
        "mode": "observation-only",
        "status": status,
        "workflow": {
            "event": event,
            "repository": env.get("GITHUB_REPOSITORY", ""),
            "runId": env.get("GITHUB_RUN_ID", ""),
            "runAttempt": env.get("GITHUB_RUN_ATTEMPT", ""),
            "trustedSource": env.get("CI_TRUSTED_SOURCE", "unknown"),
        },
        "shas": {"base": base or None, "head": head or None, "tested": tested or None,
                 "checkedOut": checkout},
        "recommendation": recommendation,
        "estimatedSavings": savings,
        "actualExecution": actual_execution(needs, env.get("CI_EXTENSION_ONLY") == "true"),
        "notes": notes,
    }


def clean(text):
    return "".join(c if c.isprintable() and c not in "`|<>" else "?" for c in str(text))


def summary(report):
    rec = report["recommendation"]
    sav = report["estimatedSavings"]
    lines = [
        "## CI selection observer (observation only)",
        "",
        f"Policy `{report['policyVersion']}` - status **{report['status']}**. " +
        ("Bounded extension PR fast path applied; general recommendations remain hypothetical."
         if report["actualExecution"]["selectionApplied"] else
         "No selection applied; this observer does not skip jobs."),
        "",
        f"- Hypothetical recommendation: **{'full validation' if rec['fullValidation'] else 'targeted'}**",
        f"- Recommended suites: {', '.join(rec['suites'])}",
        f"- Would skip: {', '.join(rec['wouldSkipSuites']) or 'none'}",
    ]
    if sav["minMinutes"] is None:
        lines.append(f"- Estimated savings: not assessed ({clean(sav['basis']['note'])})")
    else:
        lines.append(f"- Estimated savings: {sav['minMinutes']}-{sav['maxMinutes']} runner-minutes "
                     f"({'complete' if sav['complete'] else 'partial'}; unmeasured: "
                     f"{', '.join(sav['unmeasuredSuites']) or 'none'})")
    shas = report["shas"]
    lines += [f"- SHAs: base `{clean(shas['base'])}`, head `{clean(shas['head'])}`, "
              f"tested `{clean(shas['tested'])}`", "", "Reasons:"]
    lines += [f"- {clean(reason)}" for reason in rec["reasons"][:10]]
    lines += ["", "Actual job results:", ""]
    lines += [f"- {job}: {value['result']}" for job, value in report["actualExecution"]["jobs"].items()]
    return "\n".join(lines) + "\n"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default="ci-selection-report")
    args = parser.parse_args(argv)
    try:
        needs = json.loads(os.environ.get("CI_NEEDS_JSON") or "{}")
    except json.JSONDecodeError:
        needs = {}
    report = build_report(os.environ, needs, load_timings())
    out = Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    (out / "ci-selection.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    text = summary(report)
    (out / "ci-selection.md").write_text(text, encoding="utf-8")
    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as file:
            file.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
