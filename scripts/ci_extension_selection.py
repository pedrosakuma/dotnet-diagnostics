#!/usr/bin/env python3
"""Authorize only the bounded extension PR fast path, independently of the observer."""

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess

from ci_selection_observer import changed_paths, git

POLICY_VERSION = "ci-extension-pr/1"
SHARED_SUFFIXES = {".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx"}


def extension_only(paths):
    if not paths:
        return False
    has_extension_code = False
    for path in paths:
        parts = path.split("/")
        if "\\" in path or any(part in {"", ".", ".."} for part in parts):
            return False
        if PurePosixPath(path).suffix.lower() in SHARED_SUFFIXES:
            return False
        if path.endswith(".md"):
            continue
        if not path.startswith("vscode-extension/"):
            return False
        has_extension_code = True
    return has_extension_code


def decide(env):
    report = {
        "policyVersion": POLICY_VERSION,
        "extensionOnly": False,
        "reason": "",
        "workflow": {key: env.get(key, "") for key in
                     ("GITHUB_EVENT_NAME", "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
        "shas": {"base": env.get("CI_BASE_SHA", ""), "head": env.get("CI_HEAD_SHA", ""),
                 "tested": env.get("GITHUB_SHA", ""), "checkedOut": None},
        "paths": [],
    }
    if env.get("CI_EXTENSION_FAST_PATH_DISABLED", "") not in {"", "false"}:
        report["reason"] = "Extension fast path disabled; retain existing validation."
        return report
    if env.get("GITHUB_EVENT_NAME") != "pull_request":
        report["reason"] = "Only pull requests are eligible; retain existing push validation."
        return report
    if env.get("CI_TRUSTED_SOURCE") != "true":
        report["reason"] = "Source is not explicitly trusted; retain existing validation."
        return report
    try:
        shas = report["shas"]
        for key in ("base", "head", "tested"):
            sha = shas[key]
            if not re.fullmatch(r"[0-9a-f]{40}", sha) or set(sha) == {"0"}:
                raise ValueError(f"{key} SHA is missing or invalid")
            if git(["rev-parse", "--verify", f"{sha}^{{commit}}"]).strip() != sha:
                raise ValueError(f"{key} does not identify the expected commit")
        shas["checkedOut"] = git(["rev-parse", "HEAD"]).strip()
        if shas["checkedOut"] != shas["tested"]:
            raise ValueError("checked-out SHA differs from tested SHA")
        parents = git(["show", "-s", "--format=%P", shas["tested"]]).split()
        if parents != [shas["base"], shas["head"]]:
            raise ValueError("tested revision is not the merge of the exact base and head")
        # Include base-only changes present in the tested merge, not just the PR's
        # merge-base diff. Diverged/stale base metadata must never hide a shared edit.
        paths = changed_paths(shas["base"], shas["head"])
        tested_paths = changed_paths(shas["base"], shas["tested"], merge_base=False)
        report["paths"] = sorted(set(paths) | set(tested_paths))
        report["extensionOnly"] = extension_only(report["paths"])
        report["reason"] = ("Verified extension-only PR; run Linux/Windows extension tests and CLI handshake."
                            if report["extensionOnly"] else
                            "Change includes shared, unknown or non-extension paths; retain existing validation.")
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        report["reason"] = f"Selection inputs unavailable: {str(error).splitlines()[0]}; retain full validation."
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default="extension-selection-report")
    args = parser.parse_args(argv)
    report = decide(os.environ)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    (output / "selection.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    value = str(report["extensionOnly"]).lower()
    print(f"extension_only={value}: {report['reason']}")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as file:
            file.write(f"extension_only={value}\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as file:
            file.write(f"## Extension PR fast path\n\nPolicy `{POLICY_VERSION}`; selected: **{value}**.\n\n")
            # Do not interpolate git-controlled paths or exception text into Markdown.
            file.write("See `selection.json` for the reason, exact SHAs and complete changed-path inventory.\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
