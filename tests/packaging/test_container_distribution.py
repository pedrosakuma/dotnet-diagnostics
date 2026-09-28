"""Deterministic source-bound checks for container distribution policy."""

from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
DOCKERFILE = (ROOT / "deploy/Dockerfile").read_text()
SAMPLE_DOCKERFILES = {
    "CoreClrSample": (ROOT / "samples/CoreClrSample/Dockerfile").read_text(),
    "BadCodeSample": (ROOT / "samples/BadCodeSample/Dockerfile").read_text(),
}
WORKFLOW_TEXT = (ROOT / ".github/workflows/publish-container.yml").read_text()
DOCKERIGNORE = (ROOT / ".dockerignore").read_text()
KIND_WORKFLOW = (ROOT / ".github/workflows/kind-integration.yml").read_text()
EXTERNAL_WORKFLOW = (ROOT / ".github/workflows/docker-external-investigation.yml").read_text()
CRASH_COMPOSE = (ROOT / "deploy/docker-compose.crash-guard.yml").read_text()
EXTERNAL_COMPOSE = (ROOT / "deploy/docker-compose.external-investigation.yml").read_text()
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

    def test_every_docker_restore_requires_the_private_buildkit_secret(self):
        all_dockerfiles = {"server": DOCKERFILE, **SAMPLE_DOCKERFILES}
        restore_command_count = 0
        for name, dockerfile in all_dockerfiles.items():
            with self.subTest(dockerfile=name):
                restore_commands = re.findall(
                    r"dotnet restore ([^\s\\]+) \\\n\s+--configfile ([^\s\\]+)",
                    dockerfile,
                )
                self.assertEqual(
                    len(restore_commands),
                    len(re.findall(r"\bdotnet restore\b", dockerfile)),
                    restore_commands,
                )
                self.assertGreater(len(restore_commands), 0, dockerfile)
                self.assertTrue(all(config == "/run/secrets/NuGet.Config"
                                    for _, config in restore_commands), restore_commands)
                self.assertIn(
                    "RUN --mount=type=secret,id=nugetconfig,"
                    "target=/run/secrets/NuGet.Config,required=true",
                    dockerfile,
                )
                self.assertIn("--no-restore", dockerfile)
                self.assertNotRegex(dockerfile, r"(?m)^\s*COPY\s+[^#\n]*NuGet\.Config")
                restore_command_count += len(restore_commands)
        self.assertEqual(4, restore_command_count)
        self.assertNotRegex(DOCKERFILE, r"(?m)^\s*ARG\s+\w*(?:NUGET|NuGet)\w*")
        self.assertNotRegex(DOCKERFILE, r"(?m)^\s*COPY\s+[^#\n]*NuGet\.Config")
        self.assertIn("*\n", DOCKERIGNORE)
        self.assertNotRegex(DOCKERIGNORE, r"(?m)^!.*[Nn]u[Gg]et")

    def test_manual_and_registry_builds_fail_closed_and_pass_only_the_build_secret(self):
        for job_name in ("validate-container", "build"):
            job = WORKFLOW_JOBS[job_name]
            with self.subTest(job=job_name):
                self.assertIn("PRIVATE_NUGET_CONFIG: ${{ secrets.NUGET_CONFIG }}", job)
                self.assertIn('if [[ -z "${PRIVATE_NUGET_CONFIG:-}" ]]', job)
                self.assertIn("no public NuGet fallback is permitted", job)
                self.assertIn('printf \'%s\' "$PRIVATE_NUGET_CONFIG" > "$config"', job)
                self.assertIn("id: nuget", job)
                self.assertIn("if: always()", job)
                self.assertIn('rm -f -- "$RUNNER_TEMP/NuGet.Config"', job)
                self.assertNotRegex(job, r"(?m)^\s+NUGET_CONFIG=.*\$\{\{\s*secrets\.NUGET_CONFIG")
                self.assertNotIn("NUGET_CONFIG_PATH: ${{ secrets.NUGET_CONFIG }}", job)
                validation_gate = job.index('if [[ -z "${PRIVATE_NUGET_CONFIG:-}" ]]')
                restore_build = (
                    job.index("docker buildx build")
                    if job_name == "validate-container"
                    else job.index("docker/build-push-action@")
                )
                self.assertLess(validation_gate, restore_build)
        validation = WORKFLOW_JOBS["validate-container"]
        self.assertIn('--secret id=nugetconfig,src="$RUNNER_TEMP/NuGet.Config"', validation)
        publish = WORKFLOW_JOBS["build"]
        self.assertIn("id=nugetconfig,src=${{ runner.temp }}/NuGet.Config", publish)
        self.assertNotRegex(WORKFLOW_TEXT, r"(?m)^\s*(?:build-args:.*(?:NUGET|NuGet)|args:.*(?:NUGET|NuGet))")

    def test_private_config_is_not_written_to_actions_artifacts_or_logs(self):
        jobs = [WORKFLOW_JOBS["validate-container"], WORKFLOW_JOBS["build"]]
        image_workflows = [KIND_WORKFLOW, EXTERNAL_WORKFLOW]
        for job in [*jobs, *image_workflows]:
            self.assertNotRegex(job, r"(?m)^\s+path:.*(?:NuGet\.Config|config_path)")
            self.assertNotRegex(job, r"(?m)^\s*(?:cat|tee)\s+.*NuGet\.Config")
            self.assertNotIn("echo \"$PRIVATE_NUGET_CONFIG\"", job)
            upload_steps = re.findall(
                r"(?ms)^\s+- uses: actions/upload-artifact@[^\n]+\n(.*?)(?=^\s+-|\Z)",
                job,
            )
            self.assertTrue(all("NuGet.Config" not in step and "config_path" not in step
                                for step in upload_steps), upload_steps)

    def test_every_workflow_building_the_server_image_passes_the_required_secret(self):
        self.assertIn("file: deploy/Dockerfile", WORKFLOW_TEXT)
        for name, workflow in (
            ("publish-container", WORKFLOW_TEXT),
            ("kind-integration", KIND_WORKFLOW),
            ("docker-external-investigation", EXTERNAL_WORKFLOW),
        ):
            with self.subTest(workflow=name):
                self.assertIn("PRIVATE_NUGET_CONFIG: ${{ secrets.NUGET_CONFIG }}", workflow)
                self.assertIn('if [[ -z "${PRIVATE_NUGET_CONFIG:-}" ]]', workflow)
                self.assertIn(
                    "id=nugetconfig,src=${{ runner.temp }}/NuGet.Config",
                    workflow,
                )
                self.assertIn("file: deploy/Dockerfile", workflow)
                if name in ("kind-integration", "docker-external-investigation"):
                    self.assertIn(
                        'dotnet restore --configfile "$RUNNER_TEMP/NuGet.Config"',
                        workflow,
                    )
                    self.assertRegex(
                        workflow,
                        r"(?s)file: samples/CoreClrSample/Dockerfile.*?"
                        r"secrets:\s*\|\s*id=nugetconfig,src=\$\{\{ runner\.temp \}\}/NuGet\.Config",
                    )

    def test_registry_publish_permissions_and_digest_promotion_are_preserved(self):
        build = WORKFLOW_JOBS["build"]
        merge = WORKFLOW_JOBS["merge"]
        self.assertIn("packages: write", build)
        self.assertIn("outputs: type=image,", build)
        self.assertIn("push-by-digest=true", build)
        self.assertIn("docker/build-push-action@", build)
        self.assertIn("packages: write", merge)
        self.assertIn("docker buildx imagetools create", merge)
        self.assertIn("Promote attested digest to release tags", merge)
        self.assertIn("push-to-registry: true", merge)

    def test_compose_sample_builds_use_private_build_secrets(self):
        for compose in (CRASH_COMPOSE, EXTERNAL_COMPOSE):
            self.assertIn(
                "file: ${NUGET_CONFIG:?Set NUGET_CONFIG to a private NuGet.Config file}",
                compose,
            )
            self.assertEqual(
                compose.count("dockerfile:"),
                compose.count("secrets:\n        - nugetconfig"),
                compose,
            )

    def test_local_docker_build_callers_pass_private_config_as_secret(self):
        health_smoke = (ROOT / "tests/docker/health-smoke.sh").read_text()
        external_script = (ROOT / "scripts/test-docker-external-investigation.sh").read_text()
        fallback = 'nuget_config="${NUGET_CONFIG:-${HOME}/.nuget/NuGet/NuGet.Config}"'
        self.assertIn(fallback, health_smoke)
        self.assertIn(fallback, external_script)
        self.assertIn('if [[ ! -s "$nuget_config" ]]', health_smoke)
        self.assertIn('if [[ ! -s "$nuget_config" ]]', external_script)
        self.assertIn('--secret "id=nugetconfig,src=$nuget_config"', health_smoke)
        self.assertEqual(2, external_script.count('--secret "id=nugetconfig,src=$nuget_config"'))
        self.assertIn('dotnet restore DotnetDiagnostics.slnx --configfile "$nuget_config"',
                      external_script)


if __name__ == "__main__":
    unittest.main(verbosity=2)
