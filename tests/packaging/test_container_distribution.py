"""Deterministic source-bound checks for container distribution policy."""

from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
DOCKERFILE = (ROOT / "deploy/Dockerfile").read_text()
WORKFLOW_TEXT = (ROOT / ".github/workflows/publish-container.yml").read_text()
WORKFLOW_JOBS = dict(re.findall(
    r"^  ([\w-]+):\n(.*?)(?=^  [\w-]+:\n|\Z)",
    WORKFLOW_TEXT.split("\njobs:\n", 1)[1],
    re.M | re.S,
))


class ContainerDistributionTests(unittest.TestCase):
    def test_safe_loopback_default_has_no_global_insecure_http_override(self):
        self.assertIn("ENV ASPNETCORE_URLS=http://127.0.0.1:8080", DOCKERFILE)
        self.assertNotRegex(DOCKERFILE, r"(?m)^\s*MCP_ALLOW_INSECURE_HTTP=")
        self.assertIn("EXPOSE 8080", DOCKERFILE)
        self.assertIn('CMD ["dotnet", "DotnetDiagnostics.Mcp.dll", "--health-check"]', DOCKERFILE)
        self.assertNotIn('"--urls", "http://127.0.0.1:8080"', DOCKERFILE)

    def test_shipped_application_is_root_owned_read_only_except_profile_state(self):
        self.assertIn("chown -R root:root /app", DOCKERFILE)
        self.assertIn(
            "chown diagnosticsmcp:diagnosticsmcp /app/.dotnet-diagnostics/bootstrap-profiles",
            DOCKERFILE,
        )
        self.assertIn("chmod 0700 /app/.dotnet-diagnostics/bootstrap-profiles", DOCKERFILE)
        self.assertIn(
            "find /app -path /app/.dotnet-diagnostics/bootstrap-profiles -prune -o "
            "-type d -exec chmod 0555 {} +",
            DOCKERFILE,
        )
        self.assertIn(
            "find /app -path /app/.dotnet-diagnostics/bootstrap-profiles -prune -o "
            "-type f -exec chmod a-w {} +",
            DOCKERFILE,
        )

    def test_portable_assets_are_opt_in_and_rejected_outside_linux_x64(self):
        self.assertIn("ARG INCLUDE_PORTABLE_CAPTURE_WORKER=false", DOCKERFILE)
        self.assertIn(')" = "linux-x64" || exit 1', DOCKERFILE)
        self.assertIn("/src/artifacts/portable-worker/linux-x64", DOCKERFILE)
        self.assertRegex(
            WORKFLOW_TEXT,
            r"(?s)include_portable_worker:\n.*?default: false\n\s+type: boolean",
        )
        self.assertIn("matrix.platform == 'linux/amd64'", WORKFLOW_JOBS["build"])
        self.assertIn(
            "INCLUDE_PORTABLE_CAPTURE_WORKER=${{ github.event_name == 'workflow_dispatch' && "
            "inputs.include_portable_worker && matrix.platform == 'linux/amd64' }}",
            WORKFLOW_JOBS["build"],
        )
        self.assertIn(
            "uses: ./.github/workflows/portable-native-packaging.yml",
            WORKFLOW_JOBS["portable-native"],
        )

    def test_validation_mode_builds_locally_without_registry_publish_capabilities(self):
        dispatch = WORKFLOW_TEXT.split("  workflow_dispatch:\n", 1)[1].split(
            "\npermissions:\n", 1
        )[0]
        self.assertRegex(dispatch, r"(?s)mode:\n.*?default: publish\n\s+type: choice")
        validation = WORKFLOW_JOBS["validate-container"]
        self.assertIn("inputs.mode == 'validate'", validation)
        self.assertIn("docker buildx build", validation)
        self.assertIn("--load", validation)
        self.assertIn("container-validation.sh", validation)
        self.assertNotIn("docker/build-push-action", validation)
        self.assertNotIn("docker/login-action", validation)
        self.assertNotRegex(validation, r"(?m)^\s+packages: write$")

    def test_existing_push_and_tag_triggers_and_publish_mode_remain(self):
        self.assertIn("branches: [main]\n    tags: ['v*']", WORKFLOW_TEXT)
        self.assertIn("inputs.mode == 'publish'", WORKFLOW_JOBS["build"])
        self.assertIn("inputs.mode == 'publish'", WORKFLOW_JOBS["merge"])
        self.assertIn("needs: [portable-native, validate-inputs]", WORKFLOW_JOBS["build"])
        for platform in ("linux/amd64", "linux/arm64"):
            self.assertIn(f"platform: {platform}", WORKFLOW_JOBS["build"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
