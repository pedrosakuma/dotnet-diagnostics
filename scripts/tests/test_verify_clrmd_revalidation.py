import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import unittest
import uuid
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location(
    "verifier", Path(__file__).parents[1] / "verify-clrmd-revalidation.py")
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)
NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


class RevalidationEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.root = Path("artifacts") / f"clrmd-verifier-test-{uuid.uuid4().hex}"
        self.directory = self.root / "01" / "core"
        self.directory.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.root)
        source = Path("tests/DotnetDiagnostics.Core.Tests/LiveCoreClrProcessTests.cs").read_text()
        methods = re.findall(
            r"\[SkipOnLinuxCiFact\(.*?\)\]\s*public async Task (\w+)\(", source, re.DOTALL)
        methods += [
            "DumpInspector_ExtractsHeapStats_AndTypeIdentityForUserCode",
            "DumpInspector_InspectsObjectAndGcRoot_FromDumpOriginSnapshot",
        ]
        self.names = ["DotnetDiagnostics.Core.Tests.LiveCoreClrProcessTests." + name
                      for name in methods]
        self.names += ["DotnetDiagnostics.Core.Tests.OtherTest.PlatformSpecific"]
        self.write_evidence()

    def write_evidence(self):
        tree = ET.Element("TestRun", xmlns=NS)
        results = ET.SubElement(tree, "Results")
        definitions = ET.SubElement(tree, "TestDefinitions")
        entries = ET.SubElement(tree, "TestEntries")
        for index, name in enumerate(self.names):
            ET.SubElement(results, "UnitTestResult", testId=str(index), executionId=str(index), testName=name,
                          outcome="NotExecuted" if index == len(self.names) - 1 else "Passed")
            ET.SubElement(definitions, "UnitTest", id=str(index))
            ET.SubElement(entries, "TestEntry", testId=str(index), executionId=str(index))
        summary = ET.SubElement(tree, "ResultSummary", outcome="Completed")
        ET.SubElement(summary, "Counters", total=str(len(self.names)),
                      executed=str(len(self.names) - 1), passed=str(len(self.names) - 1),
                      notExecuted="1", aborted="0", failed="0")
        self.trx = self.directory / "core.trx"
        ET.ElementTree(tree).write(self.trx, encoding="utf-8")
        (self.directory / "console.log").write_text("Passed!\n")
        (self.root / "core-discovery.txt").write_text(
            "The following Tests are available:\n" + "\n".join("    " + name for name in self.names))

    def verify(self):
        VERIFIER.verify(self.root, "01", "core")

    def mutate(self, path, attribute, value):
        tree = ET.parse(self.trx)
        tree.find(path, {"t": NS}).set(attribute, value)
        tree.write(self.trx)

    def test_complete_run_accepts_unrelated_platform_skip(self):
        self.verify()
        self.assertTrue((self.root / "core-baseline.json").exists())

    def test_skipped_quarantine_is_rejected(self):
        tree = ET.parse(self.trx)
        results = tree.findall("t:Results/t:UnitTestResult", {"t": NS})
        results[0].set("outcome", "NotExecuted")
        results[-1].set("outcome", "Passed")
        tree.write(self.trx)
        with self.assertRaisesRegex(ValueError, "Required live tests missing/skipped"):
            self.verify()

    def test_missing_discovered_test_is_rejected(self):
        with (self.root / "core-discovery.txt").open("a") as file:
            file.write("\n    DotnetDiagnostics.Core.Tests.OtherTest.Missing")
        with self.assertRaisesRegex(ValueError, "Discovered tests missing"):
            self.verify()

    def test_counter_mismatch_is_rejected(self):
        self.mutate("t:ResultSummary/t:Counters", "total", "999")
        with self.assertRaisesRegex(ValueError, "Incomplete/empty"):
            self.verify()

    def test_run_abort_is_rejected(self):
        self.mutate("t:ResultSummary", "outcome", "Aborted")
        with self.assertRaisesRegex(ValueError, "did not complete"):
            self.verify()

    def test_abort_counter_is_rejected(self):
        self.mutate("t:ResultSummary/t:Counters", "aborted", "1")
        with self.assertRaisesRegex(ValueError, "Nonzero TRX aborted"):
            self.verify()

    def test_console_crash_is_rejected(self):
        (self.directory / "console.log").write_text("Test Run Aborted.")
        with self.assertRaisesRegex(ValueError, "Crash/abort reported"):
            self.verify()

    def test_crash_artifacts_are_rejected(self):
        for name in ("host.dmp", "host.crashreport.json", "test_Sequence.xml"):
            with self.subTest(name=name):
                path = self.directory / name
                path.write_text("evidence")
                with self.assertRaisesRegex(ValueError, "Crash/abort artifact"):
                    self.verify()
                path.unlink()

    def test_definition_mismatch_is_rejected(self):
        self.mutate("t:TestDefinitions/t:UnitTest", "id", "not-a-result")
        with self.assertRaisesRegex(ValueError, "identities mismatch"):
            self.verify()

    def test_expanded_theory_cases_can_share_a_definition(self):
        tree = ET.parse(self.trx)
        ns = {"t": NS}
        results = tree.find("t:Results", ns)
        entries = tree.find("t:TestEntries", ns)
        definitions = tree.find("t:TestDefinitions", ns)
        name = "DotnetDiagnostics.Core.Tests.OtherTest.Theory"
        ET.SubElement(definitions, f"{{{NS}}}UnitTest", id="theory")
        for value in (1, 2):
            ET.SubElement(results, f"{{{NS}}}UnitTestResult", testId="theory",
                          executionId=f"theory-{value}", testName=f"{name}(value: {value})",
                          outcome="Passed")
            ET.SubElement(entries, f"{{{NS}}}TestEntry", testId="theory",
                          executionId=f"theory-{value}")
        counts = tree.find("t:ResultSummary/t:Counters", ns)
        for key in ("total", "passed", "executed"):
            counts.set(key, str(int(counts.get(key)) + 2))
        tree.write(self.trx)
        with (self.root / "core-discovery.txt").open("a") as file:
            file.write(f"\n    {name}")
        self.verify()

    def test_missing_execution_entry_is_rejected(self):
        self.mutate("t:TestEntries/t:TestEntry", "executionId", "missing")
        with self.assertRaisesRegex(ValueError, "execution identities mismatch"):
            self.verify()

    def test_truncated_xml_is_rejected(self):
        self.trx.write_text("<TestRun>")
        with self.assertRaises(ET.ParseError):
            self.verify()

    def test_inventory_drift_is_rejected(self):
        self.verify()
        shutil.copytree(self.directory, self.root / "02" / "core")
        (self.root / "core-baseline.json").write_text(json.dumps([]))
        with self.assertRaisesRegex(ValueError, "inventory/outcomes changed"):
            VERIFIER.verify(self.root, "02", "core")

    def test_runner_preserves_crash_and_timeout_exit_without_retry(self):
        runner = Path("scripts/revalidate-clrmd-linux.sh").resolve()
        for exit_code in (139, 124):
            with self.subTest(exit_code=exit_code):
                work = self.root / f"runner-{exit_code}"
                binary = work / "bin"
                binary.mkdir(parents=True)
                shutil.copy("Directory.Packages.props", work)
                stub = binary / "dotnet"
                stub.write_text(
                    "#!/usr/bin/env bash\n"
                    'case "$*" in\n'
                    '  *--info*|*--list-tests*) exit 0 ;;\n'
                    'esac\n'
                    'echo invoked >> calls.txt\n'
                    f"exit {exit_code}\n")
                stub.chmod(0o755)
                environment = dict(os.environ, PATH=f"{binary.resolve()}:{os.environ['PATH']}")
                result = subprocess.run(
                    ["bash", str(runner)], cwd=work, env=environment,
                    capture_output=True, text=True, timeout=15, check=False)
                self.assertEqual(result.returncode, exit_code, result.stdout + result.stderr)
                self.assertEqual((work / "calls.txt").read_text().splitlines(), ["invoked"])
                self.assertFalse((work / "TestResults/clrmd-revalidation/completed.txt").exists())


if __name__ == "__main__":
    unittest.main()
