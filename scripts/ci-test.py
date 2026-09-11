#!/usr/bin/env python3
"""Run a suite once, preserving its exit status and verifying complete evidence."""

import argparse
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

from test_evidence import require, required_tests, verify_log, verify_trx

SUITES = ("core", "mcp", "smoke", "cli", "benchmark")


def verify(root, suite):
    directory = root / suite
    exit_code = int((directory / "exit-code.txt").read_text())
    require(exit_code == 0, f"{suite}: dotnet test exited {exit_code}; no retry or masking")
    verify_log(directory / "console.log")
    inventory = verify_trx(directory / f"{suite}.trx",
                           directory / "discovery.txt", required_tests(suite))
    if suite == "smoke":
        require(len(inventory) >= 2 and all(outcome == "Passed" for _, outcome in inventory),
                "Both net8.0/net9.0 runtime smoke cases must pass")


def run(root, suite, project, test_filter):
    directory = root / suite
    # Never accept stale evidence left by a previous invocation.
    directory.mkdir(parents=True, exist_ok=False)
    command = ["dotnet", "test", project, "--no-build", "--configuration", "Release"]
    if test_filter:
        command += ["--filter", test_filter]
    environment = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en-US")
    with (directory / "discovery.txt").open("wb") as output:
        discovery = subprocess.run(command + ["--list-tests"], stdout=output,
                                   stderr=subprocess.STDOUT, env=environment,
                                   timeout=120, check=False)
    require(discovery.returncode == 0, f"{suite}: discovery exited {discovery.returncode}")
    with (directory / "console.log").open("wb") as output:
        with subprocess.Popen(
            command + ["--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
                       "--blame-crash", "--blame-crash-dump-type", "full",
                       "--logger", f"trx;LogFileName={suite}.trx",
                       "--results-directory", str(directory.resolve())],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=environment,
        ) as process:
            for line in process.stdout:
                output.write(line)
                output.flush()
                sys.stdout.buffer.write(line)
                sys.stdout.buffer.flush()
            exit_code = process.wait()
    (directory / "exit-code.txt").write_text(str(exit_code))
    if exit_code:
        print(f"::error::{suite}: dotnet test exited {exit_code}; no retry or masking", file=sys.stderr)
        return exit_code if exit_code > 0 else 128 - exit_code
    verify(root, suite)
    return 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suite", choices=SUITES)
    parser.add_argument("project", nargs="?")
    parser.add_argument("--filter")
    parser.add_argument("--results-directory", type=Path, default=Path("TestResults"))
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    try:
        if args.verify_only:
            verify(args.results_directory, args.suite)
        else:
            require(args.project, "A test project is required")
            sys.exit(run(args.results_directory, args.suite, args.project, args.filter))
    except (ValueError, OSError, ET.ParseError, subprocess.TimeoutExpired) as error:
        sys.exit(f"::error::CI test evidence rejected: {error}")
