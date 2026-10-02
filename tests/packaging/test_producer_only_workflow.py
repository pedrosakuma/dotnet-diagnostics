"""Source-bound workflow policy checks; no Actions dispatch or native execution."""
import ast
import itertools
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github/workflows"
TEXT = (WORKFLOWS / "release.yml").read_text()
JOBS = dict(re.findall(r"^  ([\w-]+):\n(.*?)(?=^  [\w-]+:\n|\Z)",
                       TEXT.split("\njobs:\n", 1)[1], re.M | re.S))
PRODUCTS = ("pack-tool", "publish-binaries", "release", "publish-nuget")


def condition(job):
    return re.search(r"^    if: (.+)$", JOBS[job], re.M)[1]


def evaluate(expression, values):
    expression = expression.removeprefix("${{").removesuffix("}}").strip()
    expression = expression.replace("&&", " and ").replace("||", " or ")
    expression = expression.replace("!", " not ").strip()

    def visit(node):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Attribute):
            return values[node.value.id + "." + node.attr]
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            return not visit(node.operand)
        if isinstance(node, ast.BoolOp):
            items = [visit(item) for item in node.values]
            return all(items) if isinstance(node.op, ast.And) else any(items)
        if isinstance(node, ast.Compare) and len(node.ops) == 1 and isinstance(node.ops[0], ast.Eq):
            return visit(node.left) == visit(node.comparators[0])
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name):
            if node.func.id in ("success", "failure", "cancelled") and not node.args:
                return values[node.func.id]
            if node.func.id == "startsWith" and len(node.args) == 2:
                return visit(node.args[0]).startswith(visit(node.args[1]))
        raise AssertionError("Unexpected workflow expression: " + ast.dump(node))

    return visit(ast.parse(expression, mode="eval").body)


class ProducerOnlyWorkflowTests(unittest.TestCase):
    def test_complete_job_inventory_and_explicit_gates(self):
        self.assertEqual(set(JOBS), {"portable-native", *PRODUCTS})
        for job in PRODUCTS:
            self.assertIn("!inputs.producer_only", condition(job))
        self.assertEqual(
            condition("portable-native"),
            "github.event_name == 'workflow_dispatch' && "
            "(inputs.producer_only || inputs.include_portable_worker) || "
            "github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')",
        )

    def test_mode_matrix_preserves_normal_conditions_and_disables_products(self):
        scenarios = (("push", "refs/tags/v1.2.3"), ("push", "refs/heads/main"),
                     ("workflow_dispatch", "refs/heads/feature/1054-producer-only-validation"))
        for (event, ref), producer, include, state in itertools.product(
                scenarios, (None, False, True), (None, False, True),
                ("success", "failure", "cancelled", "skipped")):
            with self.subTest(event=event, ref=ref, producer=producer, include=include, state=state):
                values = {"github.event_name": event, "github.ref": ref,
                          "inputs.producer_only": producer, "inputs.include_portable_worker": include,
                          "success": state == "success", "failure": state == "failure",
                          "cancelled": state == "cancelled"}
                active = {job: bool(evaluate(condition(job), values)) for job in JOBS}
                self.assertEqual(
                    active["portable-native"],
                    (event == "workflow_dispatch" and bool(producer or include))
                    or (event == "push" and ref.startswith("refs/tags/v")),
                )
                if producer:
                    self.assertFalse(any(active[job] for job in PRODUCTS))
                else:
                    self.assertEqual(active["pack-tool"], state not in ("failure", "cancelled"))
                    self.assertEqual(active["publish-binaries"], state == "success")
                    for job in ("release", "publish-nuget"):
                        self.assertEqual(active[job], state == "success" and event == "push" and ref.startswith("refs/tags/v"))

    def test_permissions_and_reusable_call_are_least_privilege(self):
        global_permissions = TEXT.split("\npermissions:\n", 1)[1].split("\njobs:\n", 1)[0].strip()
        self.assertEqual(global_permissions, "contents: read")
        expected = {
            "portable-native": {"contents": "read"},
            "pack-tool": {"contents": "read", "attestations": "write", "id-token": "write"},
            "publish-binaries": {"contents": "read", "attestations": "write", "id-token": "write"},
            "release": {"contents": "write"},
            "publish-nuget": {"contents": "read"},
        }
        for job, permissions in expected.items():
            block = re.search(r"^    permissions:\n(.*?)(?=^    \S|\Z)", JOBS[job], re.M | re.S)[1]
            actual = dict(re.findall(r"^      ([\w-]+): (read|write|none)\b", block, re.M))
            self.assertEqual(actual, permissions)
        producer = JOBS["portable-native"]
        self.assertIn("uses: ./.github/workflows/portable-native-packaging.yml", producer)
        secrets = re.search(r"^    secrets:\n(.*?)(?=^    \S|\Z)", producer, re.M | re.S)[1]
        self.assertEqual(
            {"NUGET_CONFIG": "${{ secrets.NUGET_CONFIG }}"},
            dict(re.findall(r"^      ([\w-]+): (.+)$", secrets, re.M)),
        )
        self.assertNotIn("steps:", producer)
        reusable = (WORKFLOWS / "portable-native-packaging.yml").read_text()
        self.assertIn("\npermissions:\n  contents: read\n", reusable)
        self.assertRegex(
            reusable,
            r"(?s)workflow_call:\n\s+secrets:\n\s+NUGET_CONFIG:\n"
            r"\s+description:.*\n\s+required: true",
        )
        self.assertNotRegex(reusable, r"contents: write|packages: write|id-token: write")

    def test_portable_workflow_uses_private_restore_only_and_cleans_secret(self):
        reusable = (WORKFLOWS / "portable-native-packaging.yml").read_text()
        self.assertIn("PRIVATE_NUGET_CONFIG: ${{ secrets.NUGET_CONFIG }}", reusable)
        self.assertIn("no public NuGet fallback is permitted", reusable)
        self.assertIn(
            'dotnet restore src/DotnetDiagnostics.Core/DotnetDiagnostics.Core.csproj '
            '--configfile "$RUNNER_TEMP/portable-worker-NuGet.Config"',
            reusable,
        )
        self.assertNotRegex(reusable, r"dotnet restore(?![^\n]*--configfile)")
        cleanup = reusable.index("Remove staged private NuGet configuration")
        production = reusable.index("Produce once in the native immutable compiler container")
        upload = reusable.index("actions/upload-artifact@")
        self.assertLess(production, cleanup)
        self.assertLess(cleanup, upload)
        self.assertIn("if: always()", reusable[cleanup:upload])

    def test_input_defaults_and_required_version_remain_explicit(self):
        inputs = TEXT.split("\npermissions:\n", 1)[0]
        self.assertRegex(inputs, r"(?s)producer_only:\n.*?required: false\n        default: false\n        type: boolean")
        self.assertRegex(inputs, r"(?s)version:\n.*?required: true\n        type: string")
        self.assertIn("tags:\n      - 'v*'", inputs)
        self.assertIn("tag releases always include it", inputs)

    def test_no_downstream_workflow_run_trigger(self):
        for path in WORKFLOWS.glob("*.y*ml"):
            with self.subTest(path=path.name):
                self.assertNotRegex(path.read_text(), r"\bworkflow_run\b",
                                    "A completion trigger requires explicit downstream publishing review")


if __name__ == "__main__":
    unittest.main(verbosity=2)
