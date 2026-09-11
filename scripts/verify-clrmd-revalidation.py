#!/usr/bin/env python3
"""Require stable, complete, zero-crash evidence for every revalidation round."""

import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

from test_evidence import require, required_tests, verify_log, verify_trx


def verify(root, iteration, suite):
    require(suite in {"core", "mcp"}, f"Unknown revalidation suite: {suite}")
    directory = root / iteration / suite
    for path in directory.rglob("*"):
        require(
            not (path.suffix == ".dmp" or path.name.endswith("Sequence.xml")
                 or "crashreport" in path.name.lower()),
            f"Crash/abort artifact: {path}",
        )
    verify_log(directory / "console.log")
    inventory = verify_trx(directory / f"{suite}.trx",
                           root / f"{suite}-discovery.txt", required_tests(suite))
    baseline = root / f"{suite}-baseline.json"
    if iteration != "01":
        require(json.loads(baseline.read_text()) == [list(item) for item in inventory],
                "Test inventory/outcomes changed since iteration 01")
    else:
        baseline.write_text(json.dumps(inventory, indent=2) + "\n")


if __name__ == "__main__":
    try:
        verify(Path(sys.argv[1]), sys.argv[2], sys.argv[3])
    except (ValueError, OSError, ET.ParseError) as error:
        sys.exit(f"Revalidation rejected: {error}")
