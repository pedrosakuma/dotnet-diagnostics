import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).parents[2]
BASH = str(Path(os.environ["ProgramFiles"]) / "Git/bin/bash.exe") if os.name == "nt" else "bash"
sys.path.insert(0, str(ROOT / "scripts"))
SPEC = importlib.util.spec_from_file_location("ci_extension_selection", ROOT / "scripts/ci_extension_selection.py")
SELECT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SELECT)


class PathTests(unittest.TestCase):
    def test_extension_plus_markdown_is_eligible(self):
        self.assertTrue(SELECT.extension_only(["vscode-extension/src/extension.ts", "README.md"]))
        self.assertTrue(SELECT.extension_only(["vscode-extension/package-lock.json"]))
        self.assertTrue(SELECT.extension_only(["vscode-extension/.vscodeignore"]))

    def test_shared_mixed_unknown_and_unsafe_paths_are_not_eligible(self):
        for path in ("src/DotnetDiagnostics.Core/A.cs", "src/DotnetDiagnostics.Cli/A.cs",
                     "src/DotnetDiagnostics.Mcp/A.cs", "tests/anything.cs", "scripts/new.py",
                     ".github/workflows/ci.yml", "Directory.Packages.props", "global.json",
                     "new/path", "vscode-extension2/x.ts", "vscode-extension/../src/x.ts",
                     "vscode-extension//x.ts", "/vscode-extension/x.ts", "../x.md",
                     "vscode-extension\\x.ts", "vscode-extension/local.csproj",
                     "vscode-extension/sub/Directory.Build.props", "vscode-extension/a.TARGETS"):
            with self.subTest(path=path):
                self.assertFalse(SELECT.extension_only(["vscode-extension/x.ts", path]))

    def test_docs_and_empty_inputs_keep_existing_docs_behavior(self):
        for paths in ([], ["README.md"], ["vscode-extension/README.md", "docs/x.md"]):
            self.assertFalse(SELECT.extension_only(paths))


class DecisionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.previous = os.getcwd()
        os.chdir(self.directory.name)
        self.addCleanup(os.chdir, self.previous)
        self.run_git("init", "-q")
        self.run_git("config", "user.email", "tests@example.com")
        self.run_git("config", "user.name", "Tests")
        self.write("README.md", "base")
        self.base = self.commit("base")
        self.run_git("checkout", "-q", "-b", "feature")
        self.write("vscode-extension/x.ts", "one")
        self.head = self.commit("extension")
        self.run_git("checkout", "-q", "-b", "tested", self.base)
        self.run_git("merge", "-q", "--no-ff", "feature", "-m", "tested merge")
        self.tested = self.run_git("rev-parse", "HEAD")
        self.env = {
            "GITHUB_EVENT_NAME": "pull_request", "CI_TRUSTED_SOURCE": "true",
            "CI_BASE_SHA": self.base, "CI_HEAD_SHA": self.head, "GITHUB_SHA": self.tested,
            "GITHUB_REPOSITORY": "owner/repo", "GITHUB_RUN_ID": "42", "GITHUB_RUN_ATTEMPT": "1",
        }

    def run_git(self, *args):
        return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout.strip()

    def write(self, path, text):
        target = Path(path)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def commit(self, message):
        self.run_git("add", ".")
        self.run_git("commit", "-q", "-m", message)
        return self.run_git("rev-parse", "HEAD")

    def test_exact_merge_is_eligible_with_source_bound_report(self):
        report = SELECT.decide(self.env)
        self.assertTrue(report["extensionOnly"], report)
        self.assertEqual(report["shas"]["checkedOut"], self.tested)
        self.assertEqual(report["paths"], ["vscode-extension/x.ts"])

    def test_wrong_event_untrusted_disabled_missing_or_invalid_shas_keep_full(self):
        for key, value in (
                ("GITHUB_EVENT_NAME", "push"), ("GITHUB_EVENT_NAME", "pull_request_target"),
                ("CI_TRUSTED_SOURCE", ""), ("CI_TRUSTED_SOURCE", "false"),
                ("CI_EXTENSION_FAST_PATH_DISABLED", "true"), ("CI_EXTENSION_FAST_PATH_DISABLED", "typo"),
                ("CI_BASE_SHA", ""), ("CI_HEAD_SHA", "0" * 40), ("GITHUB_SHA", "HEAD"),
                ("GITHUB_SHA", "f" * 40), ("CI_HEAD_SHA", self.base)):
            with self.subTest(key=key, value=value):
                report = SELECT.decide({**self.env, key: value})
                self.assertFalse(report["extensionOnly"], report)
                self.assertTrue(report["reason"])

    def test_wrong_checkout_or_stale_base_retains_full(self):
        self.run_git("checkout", "-q", "feature")
        self.assertFalse(SELECT.decide(self.env)["extensionOnly"])
        self.run_git("checkout", "-q", "tested")
        self.write("Directory.Packages.props", "changed")
        newer = self.commit("shared change")
        self.assertFalse(SELECT.decide({**self.env, "GITHUB_SHA": newer})["extensionOnly"])

    def test_merge_resolution_cannot_hide_shared_changes(self):
        self.write("Directory.Packages.props", "unexpected merge edit")
        self.run_git("add", ".")
        self.run_git("commit", "--amend", "-q", "--no-edit")
        tested = self.run_git("rev-parse", "HEAD")
        report = SELECT.decide({**self.env, "GITHUB_SHA": tested})
        self.assertFalse(report["extensionOnly"])
        self.assertIn("Directory.Packages.props", report["paths"])

    def test_rename_out_of_shared_path_is_not_eligible(self):
        self.run_git("checkout", "-q", "-b", "rename-base", self.base)
        self.write("src/shared.ts", "shared")
        base = self.commit("shared file")
        self.run_git("checkout", "-q", "-b", "rename-head")
        Path("vscode-extension").mkdir(exist_ok=True)
        self.run_git("mv", "src/shared.ts", "vscode-extension/shared.ts")
        head = self.commit("move shared into extension")
        self.run_git("checkout", "-q", "-b", "rename-merge", base)
        self.run_git("merge", "-q", "--no-ff", "rename-head", "-m", "merge rename")
        tested = self.run_git("rev-parse", "HEAD")
        report = SELECT.decide({**self.env, "CI_BASE_SHA": base, "CI_HEAD_SHA": head, "GITHUB_SHA": tested})
        self.assertFalse(report["extensionOnly"])
        self.assertIn("src/shared.ts", report["paths"])

    def test_git_failure_explicitly_retains_full(self):
        with patch.object(SELECT, "git", side_effect=OSError("git unavailable")):
            report = SELECT.decide(self.env)
        self.assertFalse(report["extensionOnly"])
        self.assertIn("git unavailable", report["reason"])

    def test_cli_writes_decision_and_boolean_output(self):
        with patch.dict(os.environ, {**self.env, "GITHUB_OUTPUT": "outputs",
                                     "GITHUB_STEP_SUMMARY": "summary",
                                     "CI_EXTENSION_FAST_PATH_DISABLED": ""}):
            SELECT.main(["--output", "report"])
        report = json.loads(Path("report/selection.json").read_text())
        self.assertTrue(report["extensionOnly"], report)
        self.assertEqual(Path("outputs").read_text(), "extension_only=true\n")


def job_block(text, job):
    match = re.search(rf"^  {job}:\n(.*?)(?=^  [a-z0-9-]+:\n|\Z)", text, re.S | re.M)
    assert match, job
    return match.group(1)


def step_script(block, step):
    section = block.split(f"- name: {step}\n", 1)[1]
    script = section.split("        run: |\n", 1)[1].split("\n      - ", 1)[0]
    lines = []
    for line in script.splitlines():
        if line.strip() and not line.startswith("          "):
            break
        lines.append(line[10:])
    return "\n".join(lines)


class WorkflowTests(unittest.TestCase):
    ci = (ROOT / ".github/workflows/ci.yml").read_text()
    kind = (ROOT / ".github/workflows/kind-integration.yml").read_text()

    def test_both_workflows_use_the_same_gate_and_inputs(self):
        ci_changes = job_block(self.ci, "changes")
        kind_changes = job_block(self.kind, "changes")
        for block in (ci_changes, kind_changes):
            self.assertIn("steps.filter.outputs.code == 'true' && steps.extension.outputs.extension_only != 'true'", block)
            self.assertIn("run: python scripts/ci_extension_selection.py", block)
            self.assertIn("fetch-depth: 0", block)
            self.assertIn("vars.CI_EXTENSION_FAST_PATH_DISABLED", block)
            self.assertNotIn("secrets.", block)
        self.assertEqual(ci_changes.split("- name: Select bounded", 1)[1].split("- name: Upload", 1)[0],
                         kind_changes.split("- name: Select bounded", 1)[1].split("- name: Upload", 1)[0])

    def test_extension_job_has_both_platforms_real_cli_and_private_restore(self):
        block = job_block(self.ci, "extension-tests")
        self.assertIn("os: [ubuntu-latest, windows-latest]", block)
        self.assertIn("fail-fast: false", block)
        self.assertIn("if: needs.changes.outputs.extension_only == 'true'", block)
        self.assertIn('test -f "$DOTNET_DIAGNOSTICS_CLI_DLL"', block)
        self.assertIn("dotnet restore src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj --configfile", block)
        self.assertIn("DOTNET_DIAGNOSTICS_CLI_DLL:", block)
        self.assertIn("set -o pipefail", block)
        self.assertIn('tee "$RUNNER_TEMP/extension-tests.log"', block)
        self.assertIn("path: ${{ runner.temp }}/extension-tests.log", block)
        self.assertNotIn("path: vscode-extension/extension-tests.log", block)
        self.assertIn("npm test", block)
        self.assertIn("registry=https://packagefeedproxy.microsoft.io/npm/", block)
        self.assertIn("if: always()", block)

    def run_gate(self, job, env):
        step = {"ubuntu-gate": "Check Ubuntu test job results",
                "windows-gate": "Check split Windows job results",
                "kind": "Verify classification before allowing omitted Kind steps"}[job]
        text = self.kind if job == "kind" else self.ci
        script = step_script(job_block(text, job), step)
        return subprocess.run([BASH, "-c", script], env={**os.environ, **env},
                              capture_output=True, text=True)

    def test_required_gates_reject_failed_cancelled_missing_or_untrusted_extension_jobs(self):
        for job in ("ubuntu-gate", "windows-gate"):
            block = job_block(self.ci, job)
            self.assertIn("extension-tests", block.split("    steps:", 1)[0])
            env = {"CHANGES_RESULT": "success", "CODE": "false", "REQUESTED_CODE": "true",
                   "EXTENSION_ONLY": "true", "EXTENSION_RESULT": "success", "TRUSTED_SOURCE": "true",
                   "LIVE_RESULT": "success", "FAST_RESULT": "success" if job == "ubuntu-gate" else "skipped",
                   "BUILD_RESULT": "skipped", "CORE_RESULT": "skipped", "MCP_RESULT": "skipped"}
            with self.subTest(job=job):
                self.assertEqual(self.run_gate(job, env).returncode, 0)
                for key, values in {"EXTENSION_RESULT": ("failure", "cancelled", "skipped", ""),
                                    "TRUSTED_SOURCE": ("false", ""), "EXTENSION_ONLY": ("false", ""),
                                    "CHANGES_RESULT": ("failure", "cancelled", ""),
                                    "CODE": ("",)}.items():
                    for value in values:
                        result = self.run_gate(job, {**env, key: value})
                        self.assertNotEqual(result.returncode, 0, (job, key, value, result.stdout))

    def test_existing_full_docs_and_untrusted_behaviors_remain(self):
        for job in ("ubuntu-gate", "windows-gate"):
            env = {"CHANGES_RESULT": "success", "CODE": "true", "REQUESTED_CODE": "true",
                   "TRUSTED_SOURCE": "true", "EXTENSION_ONLY": "false", "EXTENSION_RESULT": "skipped",
                   "LIVE_RESULT": "success", "FAST_RESULT": "success", "BUILD_RESULT": "success",
                   "CORE_RESULT": "success", "MCP_RESULT": "success"}
            self.assertEqual(self.run_gate(job, env).returncode, 0)
            self.assertNotEqual(self.run_gate(job, {**env, "TRUSTED_SOURCE": "false"}).returncode, 0)
            env.update(CODE="false", REQUESTED_CODE="false")
            if job == "windows-gate":
                env.update(BUILD_RESULT="skipped", CORE_RESULT="skipped",
                           FAST_RESULT="skipped", MCP_RESULT="skipped")
            self.assertEqual(self.run_gate(job, env).returncode, 0)

    def test_policy_changes_exercise_extension_lane_without_skipping_full_ci(self):
        changes = job_block(self.ci, "changes")
        self.assertIn("extension_ci:", changes)
        # The filter uses "every" for code exclusions: alternatives must be one
        # brace pattern, not separate positive patterns no path can all match.
        patterns = changes.split("            extension_ci:\n", 1)[1].split("\n\n", 1)[0].splitlines()
        self.assertEqual(patterns, [
            "              - '{.github/workflows/ci.yml,.github/workflows/kind-integration.yml,"
            "scripts/ci_extension_selection.py,scripts/tests/test_ci_extension_selection.py}'"])
        block = job_block(self.ci, "extension-tests")
        self.assertIn("needs.changes.outputs.extension_ci == 'true'", block)
        for job in ("ubuntu-gate", "windows-gate"):
            env = {"CHANGES_RESULT": "success", "CODE": "true", "REQUESTED_CODE": "true",
                   "TRUSTED_SOURCE": "true", "EXTENSION_ONLY": "false", "EXTENSION_CI": "true",
                   "EXTENSION_RESULT": "success", "LIVE_RESULT": "success", "FAST_RESULT": "success",
                   "BUILD_RESULT": "success", "CORE_RESULT": "success", "MCP_RESULT": "success"}
            self.assertEqual(self.run_gate(job, env).returncode, 0)
            for result in ("failure", "cancelled", "skipped", ""):
                self.assertNotEqual(self.run_gate(job, {**env, "EXTENSION_RESULT": result}).returncode, 0)

    def test_kind_keeps_required_context_and_rejects_unknown_decision(self):
        self.assertIn("name: Kind Integration (ubuntu-latest)", self.kind)
        block = job_block(self.kind, "kind")
        self.assertIn("if: always()", block)
        env = {"CHANGES_RESULT": "success", "CODE": "false", "REQUESTED_CODE": "true",
               "EXTENSION_ONLY": "true", "TRUSTED_SOURCE": "true"}
        self.assertEqual(self.run_gate("kind", env).returncode, 0)
        for key in ("CHANGES_RESULT", "CODE", "EXTENSION_ONLY", "TRUSTED_SOURCE"):
            self.assertNotEqual(self.run_gate("kind", {**env, key: ""}).returncode, 0)


if __name__ == "__main__":
    unittest.main()
