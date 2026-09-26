"""Deterministic build-policy tests; fixtures are not native executables."""

import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
BUILD = ROOT / "src/DotnetDiagnostics.Core/Build"
SOURCE = ROOT / "src/DotnetDiagnostics.Core/Captures/Isolation/native/capture_worker.c"
SPEC = importlib.util.spec_from_file_location("producer", BUILD / "produce_portable_worker.py")
PRODUCER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PRODUCER)


class PortableWorkerPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="portable packaging ")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.assets = self.directory / "fixture assets"
        self.assets.mkdir()
        (self.assets / "capture-worker").write_bytes(b"TEST FIXTURE ONLY - NOT EXECUTABLE")
        (self.assets / "libe_sqlite3.so").write_bytes(b"TEST FIXTURE ONLY - NOT A LIBRARY")
        (self.assets / "sqlite-LICENSE.txt").write_text("Fixture")
        (self.assets / "worker-LICENSE.txt").write_text("Fixture")
        self.manifest = ET.Element("PortableCaptureWorker", version="1", kind="fixture", rid="linux-x64")
        values = {
            "WorkerSha256": PRODUCER.digest(self.assets / "capture-worker"),
            "SqliteSha256": PRODUCER.digest(self.assets / "libe_sqlite3.so"),
            "SourceSha256": PRODUCER.digest(SOURCE),
            "AdmissionSha256": PRODUCER.digest(SOURCE.with_name("sqlite_admission.h")),
            "RebuildSha256": PRODUCER.digest(SOURCE.with_name("sqlite_rebuild.h")),
            "SqlitePackage": PRODUCER.SQLITE_PACKAGE,
        }
        for key, value in values.items():
            ET.SubElement(self.manifest, key).text = value
        self.save_manifest()
        project = ET.Element("Project")
        ET.SubElement(project, "Import", Project=str(BUILD / "PortableCaptureWorker.targets"))
        self.project = self.directory / "fixture.proj"
        ET.ElementTree(project).write(self.project)

    def save_manifest(self):
        ET.ElementTree(self.manifest).write(self.assets / "provenance.xml")

    def check(self, *properties, assets=True, target="ValidatePortableCaptureWorkerAssets"):
        command = [os.environ.get("DOTNET_HOST_PATH", "dotnet")]
        if sdk := os.environ.get("PORTABLE_PACKAGING_TEST_SDK"):
            command.append(sdk)
        command += ["msbuild", str(self.project), "-nologo", "-t:" + target]
        if assets:
            command.append(f"-p:PortableCaptureWorkerAssetsDir={self.assets}")
        command += ["-p:" + p for p in properties]
        return subprocess.run(command, text=True, capture_output=True, timeout=30)

    def assert_error(self, result, message):
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn(message, result.stdout + result.stderr)

    def test_no_configuration_keeps_ordinary_builds_unchanged(self):
        result = self.check(assets=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_release_requires_assets(self):
        self.assert_error(self.check("RequirePortableCaptureWorkerAssets=true", assets=False), "assets are required")

    def test_fixture_cannot_be_a_release_asset(self):
        self.assert_error(self.check("AllowPortableCaptureWorkerFixtureAssets=true",
                                     "RequirePortableCaptureWorkerAssets=true"), "Fixture assets are test-only")

    def test_fixture_requires_explicit_test_opt_in(self):
        self.assert_error(self.check(), "Fixture assets are test-only")

    def test_opted_in_fixture_validates_without_execution(self):
        result = self.check("AllowPortableCaptureWorkerFixtureAssets=true")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_hash_mismatch_fails(self):
        (self.assets / "capture-worker").write_bytes(b"changed")
        self.assert_error(self.check("AllowPortableCaptureWorkerFixtureAssets=true"), "hash mismatch")

    def test_stale_source_fails(self):
        self.manifest.find("SourceSha256").text = "0" * 64
        self.save_manifest()
        self.assert_error(self.check("AllowPortableCaptureWorkerFixtureAssets=true"), "hash mismatch")

    def test_missing_library_fails(self):
        (self.assets / "libe_sqlite3.so").unlink()
        self.assert_error(self.check("AllowPortableCaptureWorkerFixtureAssets=true"), "Missing required")

    def test_stale_native_headers_fail(self):
        for field in ("AdmissionSha256", "RebuildSha256"):
            with self.subTest(field=field):
                element = self.manifest.find(field)
                original = element.text
                element.text = "0" * 64
                self.save_manifest()
                self.assert_error(self.check("AllowPortableCaptureWorkerFixtureAssets=true"), "hash mismatch")
                element.text = original

    def test_unapproved_image_fails(self):
        self.manifest.set("kind", "container")
        ET.SubElement(self.manifest, "Image").text = "gcc:latest"
        self.save_manifest()
        self.assert_error(self.check(), "digest differs")

    def test_oversized_provenance_fails_before_xml_read(self):
        (self.assets / "provenance.xml").write_bytes(b"x" * 32769)
        self.assert_error(self.check("AllowPortableCaptureWorkerFixtureAssets=true"), "exceeds 32 KiB")

    def test_wrong_compiler_identity_fails(self):
        self.manifest.set("kind", "container")
        image = ET.parse(BUILD / "PortableCaptureWorker.props").findtext("./PropertyGroup/PortableCaptureWorkerProducerImage")
        ET.SubElement(self.manifest, "Image").text = image
        ET.SubElement(self.manifest, "Compiler").text = "unknown"
        ET.SubElement(self.manifest, "Libc").text = "glibc 2.36"
        self.save_manifest()
        self.assert_error(self.check(), "compiler/libc identity differs")

    def test_matching_container_metadata_passes_identity_checks_only(self):
        self.manifest.set("kind", "container")
        props = ET.parse(BUILD / "PortableCaptureWorker.props")
        ET.SubElement(self.manifest, "Image").text = props.findtext("./PropertyGroup/PortableCaptureWorkerProducerImage")
        ET.SubElement(self.manifest, "Compiler").text = props.findtext("./PropertyGroup/PortableCaptureWorkerCompilerVersion")
        ET.SubElement(self.manifest, "Libc").text = "glibc 2.36"
        self.save_manifest()
        result = self.check("RequirePortableCaptureWorkerAssets=true")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_unsupported_rid_does_not_require_linux_assets(self):
        result = self.check("RuntimeIdentifier=win-x64", "RequirePortableCaptureWorkerAssets=true", assets=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_stale_publish_sidecars_require_fresh_output(self):
        publish = self.directory / "publish"
        (publish / "NativeAssets/portable-capture/linux-x64").mkdir(parents=True)
        result = self.check(f"PublishDir={publish}/", assets=False,
                            target="RejectStalePortableCapturePublishAssets")
        self.assert_error(result, "Stale portable capture publish assets")
        self.assertTrue((publish / "NativeAssets/portable-capture/linux-x64").exists())

    def test_sqlite_resolution_uses_pinned_shared_library(self):
        package = self.directory / "cache/sqlite/3.53.3"
        shared = package / PRODUCER.SQLITE_MEMBER
        shared.parent.mkdir(parents=True)
        shared.write_bytes(b"fixture")
        (package / "LICENSE.txt").write_text("fixture")
        graph = self.directory / "project.assets.json"
        graph.write_text(json.dumps({
            "libraries": {PRODUCER.SQLITE_PACKAGE: {"path": "sqlite/3.53.3",
                          "files": [PRODUCER.SQLITE_MEMBER], "sha512": "fixture"}},
            "packageFolders": {str(self.directory / "cache"): {}},
        }))
        self.assertEqual((shared, package / "LICENSE.txt", "fixture"), PRODUCER.sqlite_assets(graph))
        graph.write_text('{"libraries": {}, "packageFolders": {}}')
        with self.assertRaisesRegex(ValueError, "3.53.3"):
            PRODUCER.sqlite_assets(graph)

    def test_flags_match_existing_hardened_recipe(self):
        recipe = (ROOT / "tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj").read_text()
        for flag in PRODUCER.FLAGS:
            self.assertIn(flag, recipe)
        self.assertIn("-Wl,--no-as-needed -lm -Wl,--as-needed", recipe)

    def test_pinned_image_and_host_wiring(self):
        props = ET.parse(BUILD / "PortableCaptureWorker.props")
        image = props.findtext("./PropertyGroup/PortableCaptureWorkerProducerImage")
        self.assertRegex(image, r"^gcc@sha256:[0-9a-f]{64}$")
        for host in ["Cli", "Mcp"]:
            project = ET.parse(ROOT / f"src/DotnetDiagnostics.{host}/DotnetDiagnostics.{host}.csproj")
            self.assertTrue(any(i.attrib["Project"].endswith("PortableCaptureWorker.targets")
                                for i in project.findall("Import")))
        targets = ET.parse(BUILD / "PortableCaptureWorker.targets")
        content = targets.find("./ItemGroup/Content")
        self.assertEqual("true", content.attrib["ExcludeFromSingleFile"])
        self.assertEqual("false", content.attrib["Pack"])


@unittest.skipUnless(platform.system() == "Linux" and platform.machine() == "x86_64",
                     "The native worker supports Linux x86-64 only")
class NativeHeaderCompatibilityTests(unittest.TestCase):
    def compile_with_truncate_definition(self, definition):
        compiler = shutil.which("gcc")
        self.assertIsNotNone(compiler, "GCC is required for native header compatibility checks")
        with tempfile.TemporaryDirectory(prefix="portable headers ") as directory:
            include = Path(directory)
            (include / "linux").mkdir()
            header = "#include_next <linux/landlock.h>\n#undef LANDLOCK_ACCESS_FS_TRUNCATE\n"
            if definition is not None:
                header += f"#define LANDLOCK_ACCESS_FS_TRUNCATE {definition}\n"
            (include / "linux/landlock.h").write_text(header)
            return subprocess.run(
                [compiler, *PRODUCER.FLAGS, "-fsyntax-only", "-I", str(include), str(SOURCE)],
                text=True, capture_output=True, timeout=30)

    def test_full_worker_compiles_without_truncate_header_definition(self):
        result = self.compile_with_truncate_definition(None)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_full_worker_compiles_with_native_truncate_definition(self):
        result = self.compile_with_truncate_definition("(1ULL << 14)")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_conflicting_truncate_header_definition_fails_closed(self):
        result = self.compile_with_truncate_definition("(1ULL << 15)")
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Unexpected Landlock truncate UAPI value", result.stderr)


if __name__ == "__main__":
    unittest.main()
