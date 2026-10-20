import importlib.util
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).parents[2]
SPEC = importlib.util.spec_from_file_location(
    "ci_selection_observer", ROOT / "scripts" / "ci_selection_observer.py")
OBS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OBS)
TIMINGS = OBS.load_timings()


def rec(*paths):
    return OBS.recommend(list(paths), TIMINGS)


class ClassifierTests(unittest.TestCase):
    def test_docs_only_recommends_nothing_and_matches_current_behavior(self):
        recommendation, savings = rec("README.md", "docs/a/b.md")
        self.assertFalse(recommendation["fullValidation"])
        self.assertEqual(recommendation["suites"], [])
        self.assertTrue(recommendation["hypothetical"])
        self.assertEqual(savings["unmeasuredSuites"], ["ubuntu-smoke", "windows-fast", "windows-smoke",
                                                       "docker-smoke", "helm-render-smoke"])
        self.assertFalse(savings["complete"])

    def test_unmapped_path_is_full(self):
        recommendation, savings = rec("some/new/thing.bin")
        self.assertTrue(recommendation["fullValidation"])
        self.assertEqual(recommendation["suites"], list(OBS.SUITES))
        self.assertEqual((savings["minMinutes"], savings["complete"]), (0.0, True))

    def test_non_markdown_docs_asset_is_not_docs(self):
        self.assertTrue(rec("docs/diagram.png")[0]["fullValidation"])

    def test_shared_sensitive_paths_are_full(self):
        for path in ("src/DotnetDiagnostics.Core/A.cs", "Directory.Packages.props", "global.json",
                     "DotnetDiagnostics.slnx", ".github/workflows/ci.yml", "scripts/ci-test.py",
                     "samples/CoreClrSample/Program.cs", "tests/DotnetDiagnostics.TestSupport/X.cs"):
            with self.subTest(path=path):
                self.assertTrue(rec(path)[0]["fullValidation"])

    def test_isolated_cli_change_closes_windows_build_dependency(self):
        recommendation, savings = rec("src/DotnetDiagnostics.Cli/Program.cs")
        self.assertFalse(recommendation["fullValidation"])
        self.assertIn("windows-build", recommendation["suites"])
        self.assertNotIn("ubuntu-core", recommendation["suites"])
        self.assertNotIn("windows-core", recommendation["suites"])
        self.assertGreater(savings["minMinutes"], 40)
        self.assertFalse(savings["complete"])

    def test_mixed_isolated_and_shared_change_is_full(self):
        recommendation, _ = rec("src/DotnetDiagnostics.Cli/Program.cs", "src/DotnetDiagnostics.Core/A.cs")
        self.assertTrue(recommendation["fullValidation"])
        self.assertEqual(recommendation["wouldSkipSuites"], [])

    def test_mixed_isolated_changes_union_suites(self):
        recommendation, _ = rec("deploy/helm/x.yaml", "src/DotnetDiagnostics.Mcp/A.cs", "README.md")
        self.assertEqual(set(recommendation["suites"]), {
            "ubuntu-fast", "helm-render-smoke", "ubuntu-mcp", "windows-build", "windows-mcp",
            "docker-smoke"})

    def test_unsafe_paths_are_unmapped(self):
        self.assertTrue(rec("../outside.md")[0]["fullValidation"])
        self.assertTrue(rec("/abs/readme.md")[0]["fullValidation"])

    def test_windows_separators_normalize(self):
        self.assertFalse(rec("src\\DotnetDiagnostics.Cli\\P.cs")[0]["fullValidation"])

    def test_every_job_suite_is_known_and_covered(self):
        covered = {suite for suites in OBS.JOBS.values() for suite in suites}
        self.assertEqual(covered, set(OBS.SUITES))

    def test_timings_only_reference_known_suites(self):
        self.assertTrue(set(TIMINGS["suites"]) <= set(OBS.SUITES))


class ReportTests(unittest.TestCase):
    ENV = {"GITHUB_EVENT_NAME": "pull_request", "CI_BASE_SHA": "a" * 40, "CI_HEAD_SHA": "b" * 40,
           "GITHUB_SHA": "c" * 40, "GITHUB_RUN_ID": "1", "GITHUB_REPOSITORY": "o/r"}

    def build(self, env=None, paths=None, error=None, needs=None):
        with patch.object(OBS, "git", return_value="c" * 40), \
             patch.object(OBS, "changed_paths", side_effect=error, return_value=paths):
            return OBS.build_report(env or self.ENV, needs or {}, TIMINGS)

    def test_assessed_report_records_identities_and_distinguishes_actual(self):
        report = self.build(paths=["README.md"], needs={"build-test": {"result": "success"}})
        self.assertEqual(report["status"], "assessed")
        self.assertEqual(report["policyVersion"], OBS.POLICY_VERSION)
        self.assertEqual(report["shas"], {"base": "a" * 40, "head": "b" * 40, "tested": "c" * 40,
                                          "checkedOut": "c" * 40})
        self.assertTrue(report["recommendation"]["hypothetical"])
        actual = report["actualExecution"]
        self.assertFalse(actual["selectionApplied"])
        self.assertEqual(actual["jobs"]["build-test"]["result"], "success")
        self.assertEqual(actual["jobs"]["windows-build"]["result"], "unknown")

    def test_diff_failure_is_unavailable_full_and_not_zero_savings(self):
        report = self.build(error=ValueError("base/head SHA missing"))
        self.assertEqual(report["status"], "unavailable")
        self.assertTrue(report["recommendation"]["fullValidation"])
        self.assertIsNone(report["estimatedSavings"]["minMinutes"])
        self.assertFalse(report["estimatedSavings"]["complete"])

    def test_checkout_mismatch_is_noted(self):
        with patch.object(OBS, "git", return_value="d" * 40), \
             patch.object(OBS, "changed_paths", return_value=["README.md"]):
            report = OBS.build_report(self.ENV, {}, TIMINGS)
        self.assertTrue(any("differs" in note for note in report["notes"]))

    def test_malformed_needs_does_not_break_actual_record(self):
        self.assertEqual(OBS.actual_execution("garbage")["jobs"]["changes"]["result"], "unknown")

    def test_extension_selection_is_distinct_from_hypothetical_general_policy(self):
        report = self.build(env={**self.ENV, "CI_EXTENSION_ONLY": "true"},
                            paths=["vscode-extension/src/extension.ts"],
                            needs={"extension-tests": {"result": "success"}})
        self.assertTrue(report["actualExecution"]["selectionApplied"])
        self.assertEqual(report["actualExecution"]["selectionPolicy"], "ci-extension-pr/1")
        self.assertEqual(report["actualExecution"]["jobs"]["extension-tests"]["result"], "success")
        # The old, broad classifier is still conservative for unmapped frontend paths.
        self.assertTrue(report["recommendation"]["hypothetical"])
        self.assertTrue(report["recommendation"]["fullValidation"])
        self.assertIn("fast path applied", OBS.summary(report))
        self.assertNotIn("nothing was skipped", OBS.summary(report))

    def test_changed_paths_rejects_missing_or_zero_shas(self):
        for base, head in (("", "b"), ("0" * 40, "b" * 40), ("a", "")):
            with self.assertRaises(ValueError):
                OBS.changed_paths(base, head)

    def test_changed_paths_in_real_repository(self):
        with tempfile.TemporaryDirectory() as directory:
            def run(*args):
                return subprocess.run(["git", "-C", directory, *args], check=True,
                                      capture_output=True, text=True).stdout.strip()
            run("init", "-q")
            run("config", "user.email", "t@example.com")
            run("config", "user.name", "t")
            (Path(directory) / "a.md").write_text("1")
            run("add", ".")
            run("commit", "-q", "-m", "one")
            base = run("rev-parse", "HEAD")
            (Path(directory) / "src file.cs").write_text("x")
            run("add", ".")
            run("commit", "-q", "-m", "two")
            head = run("rev-parse", "HEAD")
            import os
            previous = os.getcwd()
            os.chdir(directory)
            try:
                self.assertEqual(OBS.changed_paths(base, head), ["src file.cs"])
                with self.assertRaises(ValueError):
                    OBS.changed_paths(head, head)
                # Divergent history: a file only on the old side must show up for pushes.
                run("checkout", "-q", "-b", "old", base)
                (Path(directory) / "Directory.Packages.props").write_text("x")
                run("add", ".")
                run("commit", "-q", "-m", "old")
                old = run("rev-parse", "HEAD")
                self.assertNotIn("Directory.Packages.props", OBS.changed_paths(old, head))
                self.assertIn("Directory.Packages.props", OBS.changed_paths(old, head, merge_base=False))
            finally:
                os.chdir(previous)

    def test_summary_sanitizes_untrusted_text(self):
        report = self.build(paths=["a`|<script>.md"])
        report["recommendation"]["reasons"] = ["x`|<b>\n"]
        text = OBS.summary(report)
        self.assertNotIn("<b>", text)
        self.assertNotIn("`|", text.split("Reasons:")[1])

    def test_main_writes_json_and_markdown(self):
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(OBS, "changed_paths", return_value=["README.md"]), \
             patch.object(OBS, "git", return_value="c" * 40), \
             patch.dict("os.environ", {**self.ENV, "CI_NEEDS_JSON": "{not json"}, clear=False):
            self.assertEqual(OBS.main(["--output", directory]), 0)
            data = json.loads((Path(directory) / "ci-selection.json").read_text())
            self.assertEqual(data["mode"], "observation-only")
            self.assertTrue((Path(directory) / "ci-selection.md").exists())


def job_block(text, job):
    match = re.search(rf"^  {re.escape(job)}:\n(.*?)(?=^  [a-z0-9-]+:\n|\Z)", text, re.S | re.M)
    assert match, job
    return match.group(1)


class WorkflowContractTests(unittest.TestCase):
    text = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")

    def test_observer_is_non_gating_and_secretless(self):
        block = job_block(self.text, "selection-observer")
        self.assertIn("continue-on-error: true", block)
        self.assertIn("if: ${{ always() }}", block)
        self.assertNotIn("secrets.", block)
        self.assertNotIn("NUGET_CONFIG", block)
        self.assertIn("persist-credentials: false", block)
        self.assertIn("permissions:\n      contents: read", block)

    def test_no_other_job_depends_on_observer(self):
        for match in re.finditer(r"^  ([a-z0-9-]+):\n", self.text, re.M):
            if match.group(1) != "selection-observer":
                self.assertNotIn("selection-observer", job_block(self.text, match.group(1)))

    def test_observer_needs_cover_every_job_in_the_script(self):
        needs = re.search(r"needs: \[([^\]]+)\]", job_block(self.text, "selection-observer")).group(1)
        self.assertEqual({n.strip() for n in needs.split(",")}, set(OBS.JOBS))

    def test_triggers_and_trust_posture_unchanged(self):
        self.assertNotIn("pull_request_target", self.text)
        self.assertIn("on:\n  push:\n    branches: [main]\n  pull_request:\n    branches: [main]", self.text)
        self.assertIn("'!**/*.md'", self.text)

    def test_required_check_contexts_still_exist(self):
        for name in ("Build & Test (ubuntu-latest)", "Build & Test (windows-latest)",
                     "Helm render smoke (B5.5)"):
            self.assertIn(f"name: {name}", self.text)

    def test_observer_does_not_gate_heavy_steps(self):
        self.assertNotIn("selection-observer", self.text.replace(
            "  selection-observer:\n" + job_block(self.text, "selection-observer"), ""))
        self.assertNotIn("steps.observer", self.text)


if __name__ == "__main__":
    unittest.main()
