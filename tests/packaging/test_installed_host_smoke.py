"""Source-bound checks for the installed-host portable worker smoke gate."""

import importlib.util
from pathlib import Path
import re
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = ROOT / "scripts/portable-installed-host-smoke.py"
SCRIPT = SCRIPT_PATH.read_text()
DOC = (ROOT / "docs/portable-native-packaging.md").read_text()
SPEC = importlib.util.spec_from_file_location("portable_installed_host_smoke", SCRIPT_PATH)
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class InstalledHostSmokeScriptTests(unittest.TestCase):
    def test_script_is_gated_and_records_dry_run_without_import(self):
        self.assertIn("--execute-import", SCRIPT)
        self.assertIn('"importRequested": bool(args.execute_import)', SCRIPT)
        self.assertIn('"importExecuted": False', SCRIPT)
        self.assertIn("if args.execute_import:", SCRIPT)
        self.assertIn("run_cli_import", SCRIPT)
        self.assertIn("run_mcp_import", SCRIPT)
        self.assertIn("importExecuted: false", DOC)

    def test_import_fixture_is_a_tracked_source_path(self):
        match = re.search(r'IMPORT_FIXTURE = ROOT / "([^"]+)"', SCRIPT)
        self.assertIsNotNone(match)
        fixture = ROOT / match.group(1)
        self.assertTrue(fixture.is_file() and fixture.stat().st_size > 0, fixture)
        self.assertIn("fixture = IMPORT_FIXTURE", SCRIPT)
        self.assertIn("import fixture missing or empty", SCRIPT)

    def test_pack_install_uses_required_worker_properties_and_private_config(self):
        self.assertIn("-p:PortableCaptureWorkerAssetsDir=", SCRIPT)
        self.assertIn("-p:RequirePortableCaptureWorkerAssets=true", SCRIPT)
        self.assertIn('parser.add_argument("--rid", choices=("linux-x64", "linux-arm64"), required=True)', SCRIPT)
        self.assertIn('f"-p:PortableCaptureWorkerRid={args.rid}"', SCRIPT)
        self.assertIn('parser.add_argument("--include-all-rids", action="store_true"', SCRIPT)
        self.assertIn('package_rids = ("linux-x64", "linux-arm64") if args.include_all_rids', SCRIPT)
        self.assertIn("--no-restore", SCRIPT)
        self.assertIn("dotnet\", \"restore", SCRIPT)
        self.assertIn("--configfile", SCRIPT)
        self.assertIn("dotnet\", \"tool\", \"install", SCRIPT)
        self.assertIn("--add-source", SCRIPT)
        self.assertIn("local-packages", SCRIPT)
        self.assertIn("require_no_public_source", SCRIPT)
        self.assertIn("nuget.org", SCRIPT)
        self.assertIn("refusing to continue", SCRIPT)

    def test_preflight_validates_both_installed_hosts(self):
        self.assertIn('"--installed-dir", str(item["nativeAssets"])', SCRIPT)
        self.assertIn("dotnet-diagnostics-cli", SCRIPT)
        self.assertIn("dotnet-diagnostics-mcp", SCRIPT)
        self.assertIn("portable-worker-preflight.py", SCRIPT)
        self.assertIn("same-owner", DOC)

    def test_documented_authorized_command_preserves_separate_import_gate(self):
        self.assertIn("A real native import requires separate human authorization", DOC)
        self.assertIn("--execute-import", DOC)
        self.assertGreaterEqual(len(re.findall(r"portable-installed-host-smoke\.py", DOC)), 2)

    def test_package_inventory_requires_exactly_the_selected_rid_assets(self):
        names = ("capture-worker", "libe_sqlite3.so", "provenance.xml",
                 "sqlite-LICENSE.txt", "worker-LICENSE.txt")
        with tempfile.TemporaryDirectory(prefix="portable package inventory ") as directory:
            package = Path(directory) / "tool.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                for rid in ("linux-x64", "linux-arm64"):
                    for name in names:
                        archive.writestr(
                            f"tools/net10.0/any/NativeAssets/portable-capture/{rid}/{name}",
                            "fixture",
                        )
            inventory = SMOKE.zip_inventory(package, ("linux-x64", "linux-arm64"))
            self.assertEqual(10, len(inventory["portableEntries"]))
            with zipfile.ZipFile(package, "a") as archive:
                archive.writestr(
                    "tools/net10.0/any/NativeAssets/portable-capture/linux-musl-x64/capture-worker",
                    "unexpected",
                )
            with self.assertRaisesRegex(ValueError, "unexpected portable assets"):
                SMOKE.zip_inventory(package, ("linux-x64", "linux-arm64"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
