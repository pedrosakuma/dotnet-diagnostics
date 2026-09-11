#!/usr/bin/env python3
"""Shared fail-closed TRX and independent-discovery checks for CI."""

import collections
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def require(condition, message):
    if not condition:
        raise ValueError(message)


def required_tests(suite):
    manifest = Path(__file__).with_name("clrmd-regression-tests.json")
    return set(json.loads(manifest.read_text(encoding="utf-8")).get(suite, []))


def verify_log(path):
    log = path.read_text(encoding="utf-8-sig")
    require(not re.search(
        r"Test Run Aborted|test host process crashed|Segmentation fault|core dumped",
        log, re.IGNORECASE), "Crash/abort reported in console log")


def verify_trx(trx, discovery_path, required=()):
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    tree = ET.parse(trx)
    summary = tree.find("t:ResultSummary", ns)
    require(summary is not None and summary.get("outcome") == "Completed",
            "TRX run did not complete")
    counters = summary.find("t:Counters", ns)
    require(counters is not None, "Missing TRX counters")
    counts = {key: int(value) for key, value in counters.attrib.items()}
    results = tree.findall("t:Results/t:UnitTestResult", ns)
    require(counts.get("total", 0) == len(results) > 0, "Incomplete/empty TRX results")
    outcomes = collections.Counter(result.get("outcome") for result in results)
    require(set(outcomes) <= {"Passed", "NotExecuted"}, f"Bad outcomes: {outcomes}")
    require(counts.get("executed", 0) == counts.get("passed", 0)
            == outcomes["Passed"] > 0, "Executed tests did not all pass")
    # VSTest may leave notExecuted at zero even when individual xUnit
    # results report NotExecuted. The result rows are the skip inventory.
    skipped = outcomes["NotExecuted"]
    require(counts.get("notExecuted", 0) in {0, skipped},
            "Skipped test counters mismatch")
    require(counts["total"] == counts["passed"] + skipped,
            "TRX counters are incomplete")
    for key, value in counts.items():
        if key not in {"total", "executed", "passed", "notExecuted", "completed"}:
            require(value == 0, f"Nonzero TRX {key}: {value}")
    require(all(info.get("outcome") in {"Warning", "Informational", "Passed"}
                for info in summary.findall("t:RunInfos/t:RunInfo", ns)),
            "TRX contains a run-level error")

    definitions = tree.findall("t:TestDefinitions/t:UnitTest", ns)
    entries = tree.findall("t:TestEntries/t:TestEntry", ns)
    result_ids = {result.get("testId") for result in results}
    # Runtime-expanded theories share a test definition, but every case must
    # have a distinct execution and a matching TestEntry.
    executions = {result.get("executionId") for result in results}
    require(None not in result_ids and "" not in result_ids
            and None not in executions and "" not in executions
            and len(executions) == len(results) == len(entries)
            and len(result_ids) == len(definitions),
            "Missing/duplicate TRX test definitions or entries")
    require(result_ids == {item.get("id") for item in definitions}
            == {item.get("testId") for item in entries}, "TRX identities mismatch")
    require({(item.get("testId"), item.get("executionId")) for item in results}
            == {(item.get("testId"), item.get("executionId")) for item in entries},
            "TRX execution identities mismatch")

    discovery = discovery_path.read_text(encoding="utf-8-sig")
    expected = {line.strip() for line in discovery.splitlines()
                if line.startswith("    DotnetDiagnostics.")}
    actual = {result.get("testName") for result in results}
    require(None not in actual and "" not in actual, "Missing TRX test names")
    require(expected, "No independently discovered tests")
    # Non-enumerated theories discover a method but emit argument-specific cases.
    methods = {name.split("(", 1)[0] for name in actual}
    require(all(name in actual or ("(" not in name and name in methods)
                for name in expected), f"Discovered tests missing from {trx}")
    require(methods == {name.split("(", 1)[0] for name in expected},
            "TRX/discovery method inventory mismatch")

    passed = {result.get("testName") for result in results if result.get("outcome") == "Passed"}
    required = set(required)
    require(required <= expected, f"Required tests missing from discovery: {sorted(required - expected)}")
    require(required <= passed, f"Required live tests missing/skipped: {sorted(required - passed)}")
    inventory = sorted((result.get("testName"), result.get("outcome")) for result in results)
    print(f"{trx}: {counts['passed']} passed, {skipped} unrelated skips; "
          f"{len(required)} required live tests passed; discovery/TRX complete.")
    return inventory
