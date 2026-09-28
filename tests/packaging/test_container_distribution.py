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
PORTABLE_WORKFLOW = (ROOT / ".github/workflows/portable-native-packaging.yml").read_text()
RELEASE_WORKFLOW = (ROOT / ".github/workflows/release.yml").read_text()
CI_WORKFLOW = (ROOT / ".github/workflows/ci.yml").read_text()
CRASH_COMPOSE = (ROOT / "deploy/docker-compose.crash-guard.yml").read_text()
EXTERNAL_COMPOSE = (ROOT / "deploy/docker-compose.external-investigation.yml").read_text()
WORKFLOW_JOBS = dict(re.findall(
    r"^  ([\w-]+):\n(.*?)(?=^  [\w-]+:\n|\Z)",
    WORKFLOW_TEXT.split("\njobs:\n", 1)[1],
    re.M | re.S,
))
ALL_WORKFLOWS = {
    path.name: path.read_text()
    for path in (ROOT / ".github/workflows").glob("*.yml")
}


class ContainerDistributionTests(unittest.TestCase):
    def test_restore_surfaces_disable_sdk_background_network_checks(self):
        expected = (
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
            "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE",
            "DOTNET_NOLOGO",
        )
        for name in expected:
            self.assertRegex(DOCKERFILE, rf"(?m)^(?:ENV )?\s*{name}=1(?:\s*\\)?$")
            for workflow in (
                WORKFLOW_TEXT,
                PORTABLE_WORKFLOW,
                RELEASE_WORKFLOW,
                CI_WORKFLOW,
                KIND_WORKFLOW,
                EXTERNAL_WORKFLOW,
            ):
                self.assertRegex(workflow, rf"(?m)^\s+{name}: true$")
            for dockerfile in SAMPLE_DOCKERFILES.values():
                self.assertIn(f"{name}=1", dockerfile)

        self.assertEqual(1, len(re.findall(r"(?m)^env:$", WORKFLOW_TEXT)))

    def test_required_ci_restores_use_private_config_and_block_untrusted_code(self):
        self.assertIn("trusted_source:", CI_WORKFLOW)
        self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", CI_WORKFLOW)
        self.assertIn("github.actor != 'dependabot[bot]'", CI_WORKFLOW)
        self.assertIn("code: ${{ steps.filter.outputs.code }}", CI_WORKFLOW)
        self.assertGreaterEqual(
            CI_WORKFLOW.count("Block untrusted code validation without private NuGet"),
            2,
        )
        self.assertIn(
            "Failing instead of restoring from public sources or reporting an unvalidated success.",
            CI_WORKFLOW,
        )
        self.assertIn(
            "Failing instead of accepting unvalidated Windows checks.",
            CI_WORKFLOW,
        )
        self.assertNotIn(
            "Untrusted code PR: secret-dependent Windows jobs skipped without public NuGet fallback.",
            CI_WORKFLOW,
        )
        self.assertEqual(5, CI_WORKFLOW.count("Stage caller-provided private NuGet configuration"))
        self.assertEqual(4, len(re.findall(r"\bdotnet restore --configfile ", CI_WORKFLOW)))
        self.assertNotRegex(CI_WORKFLOW, r"(?m)^\s*run: dotnet restore\s*$")
        self.assertEqual(5, CI_WORKFLOW.count("Remove staged private NuGet configuration"))

    def test_every_dotnet_workflow_disables_background_checks_and_configures_restore(self):
        for name, workflow in ALL_WORKFLOWS.items():
            if not re.search(r"(?m)^\s*(?:run:\s*)?dotnet\s", workflow):
                continue
            self.assertIn("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", workflow, name)
            normalized = re.sub(r"\\\n\s*", " ", workflow)
            for line in normalized.splitlines():
                command = line.strip()
                if "dotnet restore" not in command or command.startswith("#"):
                    continue
                self.assertIn("--configfile", command, f"{name}: {command}")

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
            "INCLUDE_PORTABLE_CAPTURE_WORKER=${{ matrix.platform == 'linux/amd64' && "
            "(github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') || "
            "github.event_name == 'workflow_dispatch' && inputs.include_portable_worker) }}",
            WORKFLOW_JOBS["build"],
        )
        self.assertIn(
            "github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')",
            WORKFLOW_JOBS["portable-native"],
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
        self.assertIn("tag images always include it", WORKFLOW_TEXT)
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
        self.assertIn("nugetconfig=${{ runner.temp }}/NuGet.Config", publish)
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
                    "nugetconfig=${{ runner.temp }}/NuGet.Config",
                    workflow,
                )
                self.assertIn("file: deploy/Dockerfile", workflow)
                workflow_lines = workflow.splitlines()
                action_blocks = []
                for index, line in enumerate(workflow_lines):
                    if "uses: docker/build-push-action@" not in line:
                        continue
                    end = index + 1
                    while end < len(workflow_lines) and not workflow_lines[end].startswith("      - "):
                        end += 1
                    action_blocks.append("\n".join(workflow_lines[index + 1:end]))
                self.assertTrue(action_blocks, name)
                for action in action_blocks:
                    self.assertRegex(
                        action,
                        r"(?m)^\s+secret-files:\s*\|\s*\n\s+nugetconfig="
                        r"\$\{\{ runner\.temp \}\}/NuGet\.Config\s*$",
                    )
                    self.assertNotRegex(action, r"(?m)^\s+secrets:\s*\|")
                if name in ("kind-integration", "docker-external-investigation"):
                    self.assertIn(
                        'dotnet restore --configfile "$RUNNER_TEMP/NuGet.Config"',
                        workflow,
                    )
                    self.assertIn("file: samples/CoreClrSample/Dockerfile", workflow)

    def test_reusable_portable_producer_fails_closed_and_restores_with_private_config(self):
        self.assertRegex(
            PORTABLE_WORKFLOW,
            r"(?s)workflow_call:\n\s+secrets:\n\s+NUGET_CONFIG:\n"
            r"\s+description:.*\n\s+required: true",
        )
        self.assertIn("PRIVATE_NUGET_CONFIG: ${{ secrets.NUGET_CONFIG }}", PORTABLE_WORKFLOW)
        self.assertIn('if [[ -z "${PRIVATE_NUGET_CONFIG:-}" ]]', PORTABLE_WORKFLOW)
        self.assertIn("no public NuGet fallback is permitted", PORTABLE_WORKFLOW)
        self.assertIn('printf \'%s\' "$PRIVATE_NUGET_CONFIG" > "$config"', PORTABLE_WORKFLOW)
        self.assertIn(
            'dotnet restore src/DotnetDiagnostics.Core/DotnetDiagnostics.Core.csproj '
            '--configfile "$RUNNER_TEMP/portable-worker-NuGet.Config"',
            PORTABLE_WORKFLOW,
        )
        self.assertIn('rm -f -- "$RUNNER_TEMP/portable-worker-NuGet.Config"', PORTABLE_WORKFLOW)
        self.assertIn("if: always()", PORTABLE_WORKFLOW)
        self.assertNotRegex(PORTABLE_WORKFLOW, r"(?m)^\s+path:.*NuGet\.Config")
        self.assertNotRegex(PORTABLE_WORKFLOW, r"(?m)^\s*(?:cat|tee)\s+.*NuGet\.Config")
        self.assertNotIn("echo \"$PRIVATE_NUGET_CONFIG\"", PORTABLE_WORKFLOW)
        self.assertNotIn("NuGet.Config", (ROOT / ".dockerignore").read_text())
        restore_index = PORTABLE_WORKFLOW.index("dotnet restore ")
        cleanup_index = PORTABLE_WORKFLOW.index('rm -f -- "$RUNNER_TEMP/portable-worker-NuGet.Config"')
        produce_index = PORTABLE_WORKFLOW.index("Produce once in the immutable Linux-x64 compiler container")
        self.assertLess(restore_index, cleanup_index)
        self.assertLess(cleanup_index, produce_index)
        artifact_steps = re.findall(
            r"(?ms)^\s+- uses: actions/upload-artifact@[^\n]+\n(.*?)(?=^\s+-|\Z)",
            PORTABLE_WORKFLOW,
        )
        self.assertTrue(
            all("NuGet.Config" not in step and "RUNNER_TEMP" not in step
                for step in artifact_steps),
            artifact_steps,
        )

    def test_every_reusable_portable_producer_caller_passes_private_config_explicitly(self):
        for name, workflow in (
            ("release", RELEASE_WORKFLOW),
            ("publish-container", WORKFLOW_TEXT),
        ):
            with self.subTest(workflow=name):
                producer_call = workflow.split("uses: ./.github/workflows/portable-native-packaging.yml", 1)[1]
                self.assertRegex(
                    producer_call,
                    r"(?s)secrets:\n\s+NUGET_CONFIG: \$\{\{ secrets\.NUGET_CONFIG \}\}",
                )

    def test_release_restore_uses_private_config_and_cleans_up_staging(self):
        self.assertIn("PRIVATE_NUGET_CONFIG: ${{ secrets.NUGET_CONFIG }}", RELEASE_WORKFLOW)
        self.assertIn('if [[ -z "${PRIVATE_NUGET_CONFIG:-}" ]]', RELEASE_WORKFLOW)
        self.assertIn("dotnet restore --configfile \"$RUNNER_TEMP/release-NuGet.Config\"",
                      RELEASE_WORKFLOW)
        self.assertIn('rm -f -- "$RUNNER_TEMP/release-NuGet.Config"', RELEASE_WORKFLOW)
        self.assertNotRegex(RELEASE_WORKFLOW, r"(?m)^\s+path:.*release-NuGet\.Config")
        upload_steps = re.findall(
            r"(?ms)^\s+- uses: actions/upload-artifact@[^\n]+\n(.*?)(?=^\s+-|\Z)",
            RELEASE_WORKFLOW,
        )
        self.assertTrue(
            all("release-NuGet.Config" not in step and "RUNNER_TEMP" not in step
                for step in upload_steps),
            upload_steps,
        )

    def test_release_binary_matrix_restores_privately_and_publishes_without_restore(self):
        binary_job = re.search(
            r"(?ms)^  publish-binaries:\n(.*?)(?=^  [\w-]+:\n|\Z)",
            RELEASE_WORKFLOW,
        ).group(1)
        self.assertIn("PRIVATE_NUGET_CONFIG: ${{ secrets.NUGET_CONFIG }}", binary_job)
        self.assertIn('if [[ -z "${PRIVATE_NUGET_CONFIG:-}" ]]', binary_job)
        self.assertIn('config="$RUNNER_TEMP/binaries-NuGet.Config"', binary_job)
        self.assertIn('unset PRIVATE_NUGET_CONFIG', binary_job)
        normalized_binary_job = re.sub(
            r"\s+", " ", re.sub(r"\\\n\s*", " ", binary_job)
        )
        self.assertIn(
            'dotnet restore src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj '
            '--runtime "${{ matrix.rid }}" -p:SelfContained=true '
            '--configfile "$RUNNER_TEMP/binaries-NuGet.Config"',
            normalized_binary_job,
        )
        self.assertIn(
            'dotnet restore src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj '
            '--runtime "${{ matrix.rid }}" -p:SelfContained=true '
            '--configfile "$RUNNER_TEMP/binaries-NuGet.Config"',
            normalized_binary_job,
        )
        self.assertEqual(2, binary_job.count("dotnet restore "))
        self.assertEqual(2, binary_job.count("dotnet publish "))
        publish_step = binary_job.split("- name: Publish self-contained single-file binaries", 1)[1]
        self.assertEqual(2, publish_step.count("--no-restore"))
        self.assertNotRegex(publish_step, r"dotnet publish[^\n]*--no-restore")
        self.assertIn('rm -f -- "$RUNNER_TEMP/binaries-NuGet.Config"', binary_job)
        self.assertRegex(
            binary_job,
            r"(?s)- name: Remove staged private NuGet configuration\n"
            r"\s+if: always\(\)\n\s+shell: bash\n\s+run: rm -f -- "
            r'"\$RUNNER_TEMP/binaries-NuGet\.Config"',
        )
        self.assertNotRegex(binary_job, r"(?m)^\s+path:.*binaries-NuGet\.Config")
        self.assertNotRegex(binary_job, r"(?m)^\s*(?:cat|tee)\s+.*binaries-NuGet\.Config")
        binary_upload_steps = re.findall(
            r"(?ms)^\s+- uses: actions/upload-artifact@[^\n]+\n(.*?)(?=^\s+-|\Z)",
            binary_job,
        )
        self.assertTrue(
            all("binaries-NuGet.Config" not in step and "RUNNER_TEMP" not in step
                for step in binary_upload_steps),
            binary_upload_steps,
        )

    def test_kind_required_context_fails_closed_for_untrusted_code_prs(self):
        changes_job = KIND_WORKFLOW.split("  changes:\n", 1)[1].split("\n  kind:\n", 1)[0]
        kind_job = KIND_WORKFLOW.split("  kind:\n", 1)[1]
        self.assertIn("trusted_source:", changes_job)
        self.assertIn("github.event_name != 'pull_request'", changes_job)
        self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", changes_job)
        self.assertIn("github.actor != 'dependabot[bot]'", changes_job)
        self.assertIn("name: Kind Integration (ubuntu-latest)", KIND_WORKFLOW)
        self.assertIn("needs: changes", kind_job)
        self.assertIn("Reject unvalidated code changes from untrusted pull request", kind_job)
        self.assertIn("Kind Integration cannot validate code changes because fork and Dependabot", kind_job)
        reject_step = kind_job.split(
            "- name: Reject unvalidated code changes from untrusted pull request",
            1,
        )[1].split("\n      - ", 1)[0]
        self.assertIn("exit 1", reject_step)
        for line in kind_job.splitlines():
            if "needs.changes.outputs.code == 'true'" in line and "trusted_source" not in line:
                self.fail(f"Kind code step lacks the trusted-source gate: {line}")
        self.assertNotIn("pull_request_target", KIND_WORKFLOW)

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
        fallback = '${NUGET_CONFIG:-${HOME}/.nuget/NuGet/NuGet.Config}'
        self.assertNotIn(fallback, health_smoke)
        self.assertNotIn(fallback, external_script)
        self.assertIn('if [[ -z "${NUGET_CONFIG:-}" ]]', health_smoke)
        self.assertIn('if [[ -z "${NUGET_CONFIG:-}" ]]', external_script)
        self.assertIn('nuget_config="$NUGET_CONFIG"', health_smoke)
        self.assertIn('nuget_config="$NUGET_CONFIG"', external_script)
        self.assertIn('if [[ ! -s "$nuget_config" ]]', health_smoke)
        self.assertIn('if [[ ! -s "$nuget_config" ]]', external_script)
        self.assertIn("api\\.nuget\\.org", health_smoke)
        self.assertIn("api\\.nuget\\.org", external_script)
        self.assertIn('--secret "id=nugetconfig,src=$nuget_config"', health_smoke)
        self.assertEqual(2, external_script.count('--secret "id=nugetconfig,src=$nuget_config"'))
        self.assertIn('dotnet restore DotnetDiagnostics.slnx --configfile "$nuget_config"',
                      external_script)


if __name__ == "__main__":
    unittest.main(verbosity=2)
