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
            isLinux: true,
            fileExists: true,
            directoryExists: true,
            procStatus: "Name:\tapp\nUid:\t1234\t1234\t1234\t1234\nGid:\t1234\t1234\t1234\t1234\n",
            commandResults:
            [
                new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty),
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
        fake.Invocations.Should().HaveCount(3);
        fake.Invocations[0].Arguments.Should().Equal("inspect", "--type", "container", "api");
        fake.Invocations[1].Arguments.Should().ContainInOrder(
            "run",
            "-d",
            "--name", "api-dotnet-diagnostics",
            "--pid", "container:api",
            "--user", "1234:1234");
        fake.Invocations[1].Arguments.Should().Contain("--cap-add");
        fake.Invocations[1].Arguments.Should().Contain("SYS_PTRACE");
        fake.Invocations[1].Arguments.Should().Contain("--publish");
        fake.Invocations[1].Arguments.Should().Contain("127.0.0.1:18892:8080");
        fake.Invocations[1].Arguments.Should().Contain("--mount");
        fake.Invocations[1].Arguments.Should().Contain("type=bind,src=/proc/4321/root/tmp,dst=/tmp");
        fake.Invocations[1].Arguments.Should().Contain("dotnet-diagnostics-mcp:dev");
        fake.Invocations[2].Arguments.Should().Equal("inspect", "--type", "container", "api-dotnet-diagnostics");

        var envelope = (DiagnosticResult<CliCommands.DockerBootstrapReport>)result.Envelope;
        envelope.Data.Should().NotBeNull();
        envelope.Data!.ProfileName.Should().Be("api");
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
            isLinux: true,
            fileExists: true,
            directoryExists: true,
            procStatus: "Uid:\t0\t0\t0\t0\nGid:\t0\t0\t0\t0\n",
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
    public async Task DockerBootstrap_CancellationDuringHealthWait_RemovesStartedSidecar()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeDockerBootstrapPlatform(
            isLinux: true,
            fileExists: true,
            directoryExists: true,
            procStatus: "Uid:\t1234\t1234\t1234\t1234\nGid:\t1234\t1234\t1234\t1234\n",
            commandResults: [],
            runAsync: (invocation, callIndex, cancellationToken) =>
            {
                if (callIndex == 0)
                {
                    return Task.FromResult(new CliCommands.DockerCliResult(0, """[{"Id":"target-id","Name":"/api","State":{"Running":true,"Pid":4321,"Status":"running"}}]""", string.Empty));
                }

                if (callIndex == 1)
                {
                    cts.Cancel();
                    return Task.FromResult(new CliCommands.DockerCliResult(0, "sidecar-id\n", string.Empty));
                }

                if (callIndex == 2)
                {
                    return Task.FromCanceled<CliCommands.DockerCliResult>(cancellationToken);
                }

                if (callIndex == 3)
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

        fake.Invocations.Should().HaveCount(4);
        fake.Invocations[2].Arguments.Should().Equal("inspect", "--type", "container", "api-dotnet-diagnostics");
        fake.Invocations[3].Arguments.Should().Equal("rm", "-f", "api-dotnet-diagnostics");
    }

    private sealed class FakeDockerBootstrapPlatform : CliCommands.IDockerBootstrapPlatform
    {
        private readonly Queue<CliCommands.DockerCliResult> _results;
        private readonly bool _fileExists;
        private readonly bool _directoryExists;
        private readonly string _procStatus;
        private readonly Func<CliCommands.DockerCliInvocation, int, CancellationToken, Task<CliCommands.DockerCliResult>>? _runAsync;

        public FakeDockerBootstrapPlatform(
            bool isLinux,
            bool fileExists,
            bool directoryExists,
            string procStatus,
            IEnumerable<CliCommands.DockerCliResult> commandResults,
            Func<CliCommands.DockerCliInvocation, int, CancellationToken, Task<CliCommands.DockerCliResult>>? runAsync = null)
        {
            IsLinux = isLinux;
            _fileExists = fileExists;
            _directoryExists = directoryExists;
            _procStatus = procStatus;
            _results = new Queue<CliCommands.DockerCliResult>(commandResults);
            _runAsync = runAsync;
        }

        public bool IsLinux { get; }

        public List<CliCommands.DockerCliInvocation> Invocations { get; } = [];

        public Task<CliCommands.DockerCliResult> RunAsync(CliCommands.DockerCliInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return _runAsync is not null
                ? _runAsync(invocation, Invocations.Count - 1, cancellationToken)
                : Task.FromResult(_results.Dequeue());
        }

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(_procStatus);

        public bool FileExists(string path) => _fileExists;

        public bool DirectoryExists(string path) => _directoryExists;
    }
}
