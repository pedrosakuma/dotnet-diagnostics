using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliSafetyPreflightTests
{
    [Fact]
    public async Task LowRisk_ProceedsWithoutOutput()
    {
        var result = await RunPreflightAsync(new CliOptions { Command = "processes" });

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Proceed);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task ModerateRisk_WarnsOnStderrAndRemainsAutomatable()
    {
        var result = await RunPreflightAsync(new CliOptions
        {
            Command = "collect",
            Kind = "exceptions",
        });

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Proceed);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("SAFETY warning [moderate] collect:");
        result.Stderr.Should().Contain("--explain-risk");
    }

    [Theory]
    [InlineData(null, "Acknowledgement required.")]
    [InlineData("critical", "does not match")]
    public async Task NonInteractiveHighRisk_FailsClosedWithoutExactAcknowledgement(
        string? acknowledgement,
        string expectedError)
    {
        var result = await RunPreflightAsync(new CliOptions
        {
            Command = "inspect-heap",
            Sources = ["live"],
            AcknowledgeRisk = acknowledgement,
        });

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Rejected);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("SAFETY high operation 'inspect-heap'");
        result.Stderr.Should().Contain(expectedError);
        result.Stderr.Should().Contain("--acknowledge-risk high");
    }

    [Fact]
    public async Task NonInteractiveHighRisk_ExactAcknowledgementProceeds()
    {
        var result = await RunPreflightAsync(new CliOptions
        {
            Command = "inspect-heap",
            Sources = ["live"],
            AcknowledgeRisk = "high",
        });

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Proceed);
        result.Stderr.Should().Contain("targetImpact:");
        result.Stderr.Should().NotContain("Acknowledgement required");
    }

    [Fact]
    public async Task InteractiveCriticalRisk_PromptShowsRequiredFieldsAndArtifactPath()
    {
        var artifactPath = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", Guid.NewGuid().ToString("N"));
        var result = await RunPreflightAsync(
            new CliOptions
            {
                Command = "dump",
                Confirm = true,
                OutDir = artifactPath,
                PidName = "sensitive-target-name",
                AcknowledgeRisk = "sensitive-automation-value",
            },
            input: "no\n",
            context: CliExecutionContext.Session,
            interactive: true,
            artifactRoot: artifactPath);

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Rejected);
        result.Stderr.Should().Contain("targetImpact:");
        result.Stderr.Should().Contain("dataExposure:");
        result.Stderr.Should().Contain("sideEffects:");
        result.Stderr.Should().Contain($"artifactPath: {Path.GetFullPath(artifactPath)}");
        result.Stderr.Should().Contain("Type 'critical' to continue");
        result.Stderr.Should().Contain("operation was not executed");
        result.Stderr.Should().NotContain("sensitive-target-name");
        result.Stderr.Should().NotContain("sensitive-automation-value");
    }

    [Fact]
    public async Task InteractiveCriticalRisk_ExactTypedAcknowledgementProceeds()
    {
        var result = await RunPreflightAsync(
            new CliOptions { Command = "dump", Confirm = true },
            input: "critical\n",
            context: CliExecutionContext.Session,
            interactive: true);

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Proceed);
        result.Stderr.Should().Contain("Type 'critical' to continue");
    }

    [Fact]
    public async Task ExplainRisk_JsonIsMachineReadableAndDoesNotExecute()
    {
        var artifactPath = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", Guid.NewGuid().ToString("N"));
        var result = await RunPreflightAsync(
            new CliOptions
            {
                Command = "dump",
                Confirm = true,
                ExplainRisk = true,
                Json = true,
                OutDir = artifactPath,
            },
            artifactRoot: artifactPath);

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Explained);
        result.Stderr.Should().BeEmpty();
        using var json = JsonDocument.Parse(result.Stdout);
        json.RootElement.GetProperty("operation").GetString().Should().Be("collect_process_dump");
        json.RootElement.GetProperty("safety").GetProperty("riskLevel").GetString().Should().Be("critical");
        json.RootElement.GetProperty("artifactPath").GetString().Should().Be(Path.GetFullPath(artifactPath));
        json.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DumpPreview_DoesNotRequireRiskAcknowledgement()
    {
        var result = await RunPreflightAsync(new CliOptions { Command = "dump" });

        result.Disposition.Should().Be(CliSafetyPreflightDisposition.Proceed);
        result.Stderr.Should().Contain("SAFETY preview [critical] dump");
        result.Stderr.Should().Contain("--confirm and --acknowledge-risk critical");
    }

    [Fact]
    public async Task OneShotJson_ModerateWarningDoesNotCorruptStdout()
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var exit = await CliHost.RunAsync(
            ["collect", "--kind", "exceptions", "--pid", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"],
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(1);
        using var json = JsonDocument.Parse(stdout.ToString());
        json.RootElement.GetProperty("error").GetProperty("kind").GetString().Should().NotBeNullOrWhiteSpace();
        stderr.ToString().Should().Contain("SAFETY warning [moderate]");
    }

    [Fact]
    public async Task OneShotCritical_MissingAndWrongAcknowledgementsNeverReachExecution()
    {
        foreach (var acknowledgementArgs in new[]
                 {
                     Array.Empty<string>(),
                     new[] { "--acknowledge-risk", "high" },
                 })
        {
            var stdout = new StringWriter(new StringBuilder());
            var stderr = new StringWriter(new StringBuilder());
            var args = new[]
            {
                "dump",
                "--pid",
                int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--confirm",
            }.Concat(acknowledgementArgs).ToArray();

            var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);

            exit.Should().Be(2);
            stdout.ToString().Should().BeEmpty();
            stderr.ToString().Should().Contain("operation was not executed");
        }
    }

    [Fact]
    public async Task RedirectedSession_DoesNotPromptAndRequiresFlagAcknowledgement()
    {
        var result = await RunSessionAsync(
            "dump --pid 123 --confirm\ncritical\nexit\n",
            interactiveSafety: false);

        result.Exit.Should().Be(0);
        result.Stderr.Should().Contain("Acknowledgement required.");
        result.Stderr.Should().NotContain("Type 'critical' to continue");
        result.Stderr.Should().Contain("Unknown command 'critical'");
    }

    [Fact]
    public async Task InteractiveSession_PromptsAndConsumesTypedDecision()
    {
        var artifactPath = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", Guid.NewGuid().ToString("N"));
        var quotedPath = artifactPath.Replace("\"", string.Empty, StringComparison.Ordinal);
        var result = await RunSessionAsync(
            $"dump --pid 123 --confirm --out \"{quotedPath}\"\nno\nexit\n",
            interactiveSafety: true,
            artifactRoot: artifactPath);

        result.Exit.Should().Be(0);
        result.Stderr.Should().Contain("Type 'critical' to continue");
        result.Stderr.Should().Contain($"artifactPath: {Path.GetFullPath(artifactPath)}");
        result.Stderr.Should().Contain("SAFETY cancelled");
        result.Stderr.Should().NotContain("Unknown command 'no'");
    }

    private static async Task<(CliSafetyPreflightDisposition Disposition, string Stdout, string Stderr)> RunPreflightAsync(
        CliOptions options,
        string input = "",
        CliExecutionContext context = CliExecutionContext.OneShot,
        bool interactive = false,
        string? artifactRoot = null)
    {
        using var stdin = new StringReader(input);
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var disposition = await CliSafetyPreflight.RunAsync(
            options,
            handles: null,
            context,
            interactive,
            stdin,
            stdout,
            stderr,
            artifactRoot,
            CancellationToken.None);
        return (disposition, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunSessionAsync(
        string input,
        bool interactiveSafety,
        string? artifactRoot = null)
    {
        var root = artifactRoot
            ?? Path.Combine(Environment.CurrentDirectory, ".test-artifacts", Guid.NewGuid().ToString("N"));
        var store = new MemoryDiagnosticHandleStore();
        using var services = new ServiceCollection()
            .AddSingleton<IDiagnosticHandleStore>(store)
            .BuildServiceProvider();
        var provider = new MutableArtifactRootProvider(root);
        using var stdin = new StringReader(input);
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        try
        {
            var exit = await SessionRepl.RunAsync(
                services,
                provider,
                stdin,
                stdout,
                stderr,
                initialTargetPid: null,
                CancellationToken.None,
                interactiveSafety);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            var parent = Path.GetDirectoryName(root);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }
}
