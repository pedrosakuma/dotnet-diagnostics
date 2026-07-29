using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliDockerBootstrapTests
{
    [Fact]
    public async Task DockerBootstrap_BuildsExpectedDockerCommandsAndConfig()
    {
        var fake = new FakeDockerBootstrapPlatform(
            commandResults:
            [
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
                new CliCommands.DockerCliResult(0, "Name:\tapp\nUid:\t1234\t1234\t1234\t1234\nGid:\t1234\t1234\t1234\t1234\nNSpid:\t4321\t7\n", string.Empty),
                new CliCommands.DockerCliResult(0, "sidecar-id\n", string.Empty),
                new CliCommands.DockerCliResult(0, """[{"Id":"sidecar-id","Name":"/api-dotnet-diagnostics","State":{"Running":true,"Pid":5678,"Status":"running","Health":{"Status":"healthy"}}}]""", string.Empty),
            ]);

        using var _ = CliCommands.PushDockerBootstrapPlatformForCurrentAsyncFlow(fake);

        var options = CliOptions.Parse(
            ["docker-bootstrap", "--target-container", "api", "--host-port", "18892"],
            out var error)!;
        error.Should().BeNull();

        var result = await CliCommands.DockerBootstrapAsync(options, CancellationToken.None);

        result.IsError.Should().BeFalse();
        fake.Invocations.Should().HaveCount(4);
        fake.Invocations[0].Arguments.Should().Equal("inspect", "--type", "container", "api");
        fake.Invocations[1].Arguments.Should().Equal(
            "run",
            "--rm",
            "--network", "none",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pid", "host",
            "--entrypoint", "/bin/cat",
            "ghcr.io/pedrosakuma/dotnet-diagnostics:edge",
            "/proc/4321/status");
        fake.Invocations[2].Arguments.Should().ContainInOrder(
            "run",
            "-d",
            "--name", "api-dotnet-diagnostics",
            "--pid", "container:api",
            "--user", "1234:1234");
        fake.Invocations[2].Arguments.Should().Contain("--cap-add");
        fake.Invocations[2].Arguments.Should().Contain("SYS_PTRACE");
        fake.Invocations[2].Arguments.Should().Contain("--publish");
        fake.Invocations[2].Arguments.Should().Contain("127.0.0.1:18892:8080");
        fake.Invocations[2].Arguments.Should().NotContain("--mount");
        fake.Invocations[2].Arguments.Should().Contain("TMPDIR=/proc/7/root/tmp");
        fake.Invocations[2].Arguments.Should().Contain("ghcr.io/pedrosakuma/dotnet-diagnostics:edge");
        fake.Invocations[3].Arguments.Should().Equal("inspect", "--type", "container", "api-dotnet-diagnostics");

        var envelope = (DiagnosticResult<CliCommands.DockerBootstrapReport>)result.Envelope;
        envelope.Data.Should().NotBeNull();
        envelope.Data!.ProfileName.Should().Be("api");
        envelope.Data.TargetNamespacePid.Should().Be(7);
        envelope.Data.ProfileUrl.Should().Be("http://127.0.0.1:18892/mcp");
        envelope.Data.AllowedCidrs.Should().Equal("127.0.0.1/32");
        envelope.Data.AllowedPorts.Should().Equal(18892);
        envelope.Data.CentralEnvLines.Should().Contain("Orchestrator__ExternalMcpProfiles__api__Url=http://127.0.0.1:18892/mcp");
        envelope.Data.CentralJson.Should().Contain("\"BearerToken\"");
        result.Human.Should().Contain("does not register the profile dynamically");
    }

    [Fact]
    public async Task DockerBootstrap_ProfileUrlHostnameWithoutAllowCidr_ReturnsUsageErrorEnvelope()
    {
        var fake = new FakeDockerBootstrapPlatform(
            commandResults:
            [
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
            ]);

        using var _ = CliCommands.PushDockerBootstrapPlatformForCurrentAsyncFlow(fake);

        var options = CliOptions.Parse(
            ["docker-bootstrap", "--target-container", "api", "--profile-url", "http://host.docker.internal:18892/mcp"],
            out var error)!;
        error.Should().BeNull();

        var result = await CliCommands.DockerBootstrapAsync(options, CancellationToken.None);

        result.IsError.Should().BeTrue();
        fake.Invocations.Should().HaveCount(1);
        var envelope = (DiagnosticResult<CliCommands.DockerBootstrapReport>)result.Envelope;
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArgument");
        result.Human.Should().Contain("--allow-cidr");
    }

    [Fact]
    public async Task DockerBootstrap_ProcStatusProbeFailureWhileContainerStillRunning_ReturnsHostProcNotAccessible()
    {
        var fake = new FakeDockerBootstrapPlatform(
            commandResults:
            [
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
                new CliCommands.DockerCliResult(125, string.Empty, "cannot join PID namespace"),
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
            ]);

        using var _ = CliCommands.PushDockerBootstrapPlatformForCurrentAsyncFlow(fake);

        var options = CliOptions.Parse(
            ["docker-bootstrap", "--target-container", "api"],
            out var error)!;
        error.Should().BeNull();

        var result = await CliCommands.DockerBootstrapAsync(options, CancellationToken.None);

        result.IsError.Should().BeTrue();
        fake.Invocations.Should().HaveCount(3);
        fake.Invocations[2].Arguments.Should().Equal("inspect", "--type", "container", "api");
        var envelope = (DiagnosticResult<CliCommands.DockerBootstrapReport>)result.Envelope;
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("HostProcNotAccessible");
        envelope.Error.Message.Should().Contain("transient PID-namespace probe failed");
        result.Human.Should().Contain("still running");
        envelope.Error.Message.Should().Contain("cannot join PID namespace");
    }

    [Fact]
    public async Task DockerBootstrap_ProcStatusProbeImageNotFound_ReturnsExternalDependencyFailedNotHostProcNotAccessible()
    {
        var fake = new FakeDockerBootstrapPlatform(
            commandResults:
            [
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
                new CliCommands.DockerCliResult(125, string.Empty, "Unable to find image 'ghcr.io/pedrosakuma/dotnet-diagnostics:edge' locally\ndocker: Error response from daemon: manifest unknown"),
            ]);

        using var _ = CliCommands.PushDockerBootstrapPlatformForCurrentAsyncFlow(fake);

        var options = CliOptions.Parse(
            ["docker-bootstrap", "--target-container", "api"],
            out var error)!;
        error.Should().BeNull();

        var result = await CliCommands.DockerBootstrapAsync(options, CancellationToken.None);

        result.IsError.Should().BeTrue();
        fake.Invocations.Should().HaveCount(2);
        var envelope = (DiagnosticResult<CliCommands.DockerBootstrapReport>)result.Envelope;
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("ExternalDependencyFailed");
        envelope.Error.Message.Should().Contain("manifest unknown");
        envelope.Error.Message.Should().Contain("ghcr.io/pedrosakuma/dotnet-diagnostics:edge");
        result.Human.Should().Contain("ghcr.io/pedrosakuma/dotnet-diagnostics:edge");
        result.Human.Should().Contain("could not be resolved");
    }

    [Theory]
    [InlineData("0.20.0", "ghcr.io/pedrosakuma/dotnet-diagnostics:0.20.0")]
    [InlineData("0.21.0-rc.1", "ghcr.io/pedrosakuma/dotnet-diagnostics:0.21.0-rc.1")]
    public void DockerBootstrapImageResolver_ReleasedVersionUsesExactTag(string version, string expected)
    {
        DockerBootstrapImageResolver.Resolve(null, version).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void DockerBootstrapImageResolver_DevelopmentBuildUsesEdge(string? embeddedTag)
    {
        DockerBootstrapImageResolver.Resolve(null, embeddedTag)
            .Should().Be("ghcr.io/pedrosakuma/dotnet-diagnostics:edge");
    }

    [Fact]
    public void DockerBootstrapImageResolver_ExplicitOverrideAlwaysWins()
    {
        DockerBootstrapImageResolver.Resolve("registry.example/diagnostics:custom", "0.20.0")
            .Should().Be("registry.example/diagnostics:custom");
    }

    [Fact]
    public async Task DockerBootstrap_ProcStatusProbeFailureAfterRestart_ReturnsTargetNotRunning()
    {
        var fake = new FakeDockerBootstrapPlatform(
            commandResults:
            [
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
                new CliCommands.DockerCliResult(125, string.Empty, "cannot join PID namespace"),
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":5432,"Status":"running"}}]""", string.Empty),
            ]);

        using var _ = CliCommands.PushDockerBootstrapPlatformForCurrentAsyncFlow(fake);

        var options = CliOptions.Parse(
            ["docker-bootstrap", "--target-container", "api"],
            out var error)!;
        error.Should().BeNull();

        var result = await CliCommands.DockerBootstrapAsync(options, CancellationToken.None);

        result.IsError.Should().BeTrue();
        fake.Invocations.Should().HaveCount(3);
        var envelope = (DiagnosticResult<CliCommands.DockerBootstrapReport>)result.Envelope;
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("TargetNotRunning");
        envelope.Error.Message.Should().Contain("first reported pid 4321, then pid 5432");
        result.Human.Should().Contain("restarted");
    }

    [Fact]
    public async Task DockerBootstrap_CancellationDuringHealthWait_RemovesStartedSidecar()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeDockerBootstrapPlatform(
            commandResults: [],
            runAsync: (invocation, callIndex, cancellationToken) =>
            {
                if (callIndex == 0)
                {
                    return Task.FromResult(new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty));
                }

                if (callIndex == 1)
                {
                    return Task.FromResult(new CliCommands.DockerCliResult(0, "Uid:\t1234\t1234\t1234\t1234\nGid:\t1234\t1234\t1234\t1234\nNSpid:\t4321\t1\n", string.Empty));
                }

                if (callIndex == 2)
                {
                    cts.Cancel();
                    return Task.FromResult(new CliCommands.DockerCliResult(0, "sidecar-id\n", string.Empty));
                }

                if (callIndex == 3)
                {
                    return Task.FromCanceled<CliCommands.DockerCliResult>(cancellationToken);
                }

                if (callIndex == 4)
                {
                    return Task.FromResult(new CliCommands.DockerCliResult(0, "api-dotnet-diagnostics\n", string.Empty));
                }

                throw new InvalidOperationException($"Unexpected docker invocation #{callIndex}: {invocation.ToDisplayString()}");
            });

        using var _ = CliCommands.PushDockerBootstrapPlatformForCurrentAsyncFlow(fake);

        var options = CliOptions.Parse(
            ["docker-bootstrap", "--target-container", "api"],
            out var error)!;
        error.Should().BeNull();

        await FluentActions.Awaiting(() => CliCommands.DockerBootstrapAsync(options, cts.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        fake.Invocations.Should().HaveCount(5);
        fake.Invocations[3].Arguments.Should().Equal("inspect", "--type", "container", "api-dotnet-diagnostics");
        fake.Invocations[4].Arguments.Should().Equal("rm", "-f", "api-dotnet-diagnostics");
    }

    private sealed class FakeDockerBootstrapPlatform : CliCommands.IDockerBootstrapPlatform
    {
        private readonly Queue<CliCommands.DockerCliResult> _results;
        private readonly Func<CliCommands.DockerCliInvocation, int, CancellationToken, Task<CliCommands.DockerCliResult>>? _runAsync;

        public FakeDockerBootstrapPlatform(
            IEnumerable<CliCommands.DockerCliResult> commandResults,
            Func<CliCommands.DockerCliInvocation, int, CancellationToken, Task<CliCommands.DockerCliResult>>? runAsync = null)
        {
            _results = new Queue<CliCommands.DockerCliResult>(commandResults);
            _runAsync = runAsync;
        }

        public List<CliCommands.DockerCliInvocation> Invocations { get; } = [];

        public Task<CliCommands.DockerCliResult> RunAsync(CliCommands.DockerCliInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return _runAsync is not null
                ? _runAsync(invocation, Invocations.Count - 1, cancellationToken)
                : Task.FromResult(_results.Dequeue());
        }
    }
}
