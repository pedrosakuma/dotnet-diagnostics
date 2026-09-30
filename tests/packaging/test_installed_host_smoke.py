"""Source-bound checks for the installed-host portable worker smoke gate."""

from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "scripts/portable-installed-host-smoke.py").read_text()
DOC = (ROOT / "docs/portable-native-packaging.md").read_text()


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


if __name__ == "__main__":
    unittest.main(verbosity=2)
