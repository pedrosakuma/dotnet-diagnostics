using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Issue #665 part B — <c>commandLineContains</c> filter on
/// <see cref="ProcessInspectionUseCases.ListProcesses"/>, used to disambiguate among several
/// candidates spawned by a wrapper the caller doesn't control (e.g. several
/// <c>testhost.exe</c> processes under <c>dotnet test</c>).
/// </summary>
public sealed class ProcessInspectionUseCasesTests
{
    private static DotnetProcess MakeProcess(int pid, string commandLine, string? entrypoint = null) =>
        new(pid, commandLine, "linux", "x64", "10.0.0", entrypoint);

    [Fact]
    public void ListProcesses_WithCommandLineContains_ReturnsOnlyMatchingSubset()
    {
        var discovery = new StubDiscovery(
            MakeProcess(101, "/usr/bin/dotnet /app/testhost.exe --assembly Foo.Tests.dll", "testhost"),
            MakeProcess(102, "/usr/bin/dotnet /app/testhost.exe --assembly Bar.Tests.dll", "testhost"),
            MakeProcess(103, "/usr/bin/dotnet /app/CoreClrSample.dll", "CoreClrSample"));

        var result = ProcessInspectionUseCases.ListProcesses(discovery, commandLineContains: "testhost");

        result.Data.Should().HaveCount(2);
        result.Data!.Select(p => p.ProcessId).Should().BeEquivalentTo(new[] { 101, 102 });
        result.Summary.Should().Contain("commandLineContains='testhost'");
    }

    [Fact]
    public void ListProcesses_WithCommandLineContains_IsCaseInsensitive()
    {
        var discovery = new StubDiscovery(MakeProcess(101, "/usr/bin/dotnet /app/TestHost.exe"));

        var result = ProcessInspectionUseCases.ListProcesses(discovery, commandLineContains: "testhost");

        result.Data.Should().ContainSingle(p => p.ProcessId == 101);
    }

    [Fact]
    public void ListProcesses_WithCommandLineContains_NoMatch_ReturnsEmptyWithFilterNamedInSummary()
    {
        var discovery = new StubDiscovery(MakeProcess(101, "/usr/bin/dotnet /app/CoreClrSample.dll"));

        var result = ProcessInspectionUseCases.ListProcesses(discovery, commandLineContains: "testhost");

        result.Data.Should().BeEmpty();
        result.Summary.Should().Contain("commandLineContains='testhost'");
        result.IsError.Should().BeFalse("an empty filtered result is not itself an error");
    }

    [Fact]
    public void ListProcesses_WithoutCommandLineContains_MatchesLegacyBehaviorExactly()
    {
        var discovery = new StubDiscovery(
            MakeProcess(101, "/usr/bin/dotnet /app/CoreClrSample.dll", "CoreClrSample"),
            MakeProcess(102, "/usr/bin/dotnet /app/testhost.exe", "testhost"));

        var withDefaultArgument = ProcessInspectionUseCases.ListProcesses(discovery);
        var withExplicitNull = ProcessInspectionUseCases.ListProcesses(discovery, commandLineContains: null);

        withDefaultArgument.Data.Should().BeEquivalentTo(withExplicitNull.Data, options => options.WithStrictOrdering());
        withDefaultArgument.Summary.Should().Be(withExplicitNull.Summary);
        withDefaultArgument.Data.Should().HaveCount(2);
        withDefaultArgument.Summary.Should().NotContain("commandLineContains");
    }

    private sealed class StubDiscovery : IProcessDiscovery
    {
        private readonly IReadOnlyList<DotnetProcess> _processes;
        public StubDiscovery(params DotnetProcess[] processes) => _processes = processes;
        public IReadOnlyList<DotnetProcess> ListProcesses() => _processes;
        public DotnetProcess? TryGetProcess(int processId) => _processes.FirstOrDefault(p => p.ProcessId == processId);
    }
}
