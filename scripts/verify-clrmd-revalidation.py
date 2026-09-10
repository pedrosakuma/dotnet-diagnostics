#!/usr/bin/env python3
"""Fail closed on incomplete, skipped-quarantine, or crashed revalidation evidence."""

import collections
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify(root, iteration, suite):
    directory = root / iteration / suite
    for path in directory.rglob("*"):
        require(
            not (path.suffix == ".dmp" or path.name.endswith("Sequence.xml")
                 or "crashreport" in path.name.lower()),
            f"Crash/abort artifact: {path}",
        )
    log = (directory / "console.log").read_text(encoding="utf-8-sig")
    require(not re.search(
        r"Test Run Aborted|test host process crashed|Segmentation fault|core dumped",
        log, re.IGNORECASE), "Crash/abort reported in console log")

    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    tree = ET.parse(directory / f"{suite}.trx")
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
    require(counts.get("notExecuted", 0) == outcomes["NotExecuted"],
            "Skipped test counters mismatch")
    require(counts["total"] == counts["passed"] + counts.get("notExecuted", 0),
            "TRX counters are incomplete")
    for key, value in counts.items():
        if key not in {"total", "executed", "passed", "notExecuted", "completed"}:
            require(value == 0, f"Nonzero TRX {key}: {value}")
    require(not summary.findall("t:RunInfos/t:RunInfo[@outcome='Error']", ns),
            "TRX contains a run-level error")

    definitions = tree.findall("t:TestDefinitions/t:UnitTest", ns)
    entries = tree.findall("t:TestEntries/t:TestEntry", ns)
    result_ids = {result.get("testId") for result in results}
    require(len(result_ids) == len(results) == len(definitions) == len(entries),
            "Missing/duplicate TRX test definitions or entries")
    require(result_ids == {item.get("id") for item in definitions}
            == {item.get("testId") for item in entries}, "TRX identities mismatch")

    discovery = (root / f"{suite}-discovery.txt").read_text(encoding="utf-8-sig")
    expected = {line.strip() for line in discovery.splitlines()
                if line.startswith("    DotnetDiagnostics.")}
    actual = {result.get("testName") for result in results}
    require(expected, "No independently discovered tests")
    # Non-enumerated theories discover a method but emit argument-specific cases.
    methods = {name.split("(", 1)[0] for name in actual}
    require(all(name in actual or ("(" not in name and name in methods)
                for name in expected), f"Discovered tests missing from {suite} TRX")
    require(methods == {name.split("(", 1)[0] for name in expected},
            "TRX/discovery method inventory mismatch")

    if suite == "core":
        source = Path("tests/DotnetDiagnostics.Core.Tests/LiveCoreClrProcessTests.cs").read_text()
        quarantined = re.findall(
            r"\[SkipOnLinuxCiFact\(.*?\)\]\s*public async Task (\w+)\(",
            source, re.DOTALL)
        require(len(quarantined) == source.count("[SkipOnLinuxCiFact(") > 0,
                "Could not enumerate every quarantined test")
        required = {
            f"DotnetDiagnostics.Core.Tests.LiveCoreClrProcessTests.{name}"
            for name in quarantined + [
                "DumpInspector_ExtractsHeapStats_AndTypeIdentityForUserCode",
                "DumpInspector_InspectsObjectAndGcRoot_FromDumpOriginSnapshot",
            ]
        }
    else:
        required = {
            "DotnetDiagnostics.Mcp.IntegrationTests.McpInvocationSafetyTests."
            "LowCall_IsPromptFree_AndModerateCallCarriesWarnings"
        }
    passed = {result.get("testName") for result in results if result.get("outcome") == "Passed"}
    require(required <= passed, f"Required live tests missing/skipped: {sorted(required - passed)}")

    inventory = sorted((result.get("testName"), result.get("outcome")) for result in results)
    baseline = root / f"{suite}-baseline.json"
    if iteration != "01":
        require(json.loads(baseline.read_text()) == [list(item) for item in inventory],
                "Test inventory/outcomes changed since iteration 01")
    else:
        baseline.write_text(json.dumps(inventory, indent=2) + "\n")
    print(f"{suite} {iteration}: {counts['passed']} passed, "
          f"{counts.get('notExecuted', 0)} unrelated skips; "
          f"{len(required)} required live tests passed; discovery/TRX complete.")


if __name__ == "__main__":
    try:
        verify(Path(sys.argv[1]), sys.argv[2], sys.argv[3])
    except (ValueError, OSError, ET.ParseError) as error:
        sys.exit(f"Revalidation rejected: {error}")
