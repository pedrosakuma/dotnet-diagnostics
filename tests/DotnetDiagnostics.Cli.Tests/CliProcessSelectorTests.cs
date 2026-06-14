using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliProcessSelectorTests
{
    [Fact]
    public void TryResolveName_ExactName_ReturnsPid()
    {
        var processes = new[]
        {
            Process(101, "Orders.Api"),
            Process(202, "Billing.Worker"),
        };

        var ok = CliProcessSelector.TryResolveName("Orders.Api", processes, out var pid, out var error);

        ok.Should().BeTrue();
        pid.Should().Be(101);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolveName_Prefix_ReturnsOnlyMatch()
    {
        var processes = new[]
        {
            Process(101, "Orders.Api"),
            Process(202, "Billing.Worker"),
        };

        var ok = CliProcessSelector.TryResolveName("Bill", processes, out var pid, out var error);

        ok.Should().BeTrue();
        pid.Should().Be(202);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolveName_FileStemPrefix_ReturnsOnlyMatch()
    {
        var processes = new[]
        {
            Process(303, "/apps/Checkout.Service.dll"),
        };

        var ok = CliProcessSelector.TryResolveName("Checkout", processes, out var pid, out var error);

        ok.Should().BeTrue();
        pid.Should().Be(303);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolveName_AmbiguousPrefix_ReturnsPidAndNameList()
    {
        var processes = new[]
        {
            Process(101, "Orders.Api"),
            Process(202, "Orders.Worker"),
            Process(303, "Billing.Worker"),
        };

        var ok = CliProcessSelector.TryResolveName("Orders", processes, out var pid, out var error);

        ok.Should().BeFalse();
        pid.Should().Be(0);
        error.Should().Contain("ambiguous");
        error.Should().Contain("pid 101 (Orders.Api)");
        error.Should().Contain("pid 202 (Orders.Worker)");
        error.Should().NotContain("Billing.Worker");
    }

    [Fact]
    public void TryResolveName_NoMatch_ReturnsVisibleProcessList()
    {
        var processes = new[]
        {
            Process(101, "Orders.Api"),
        };

        var ok = CliProcessSelector.TryResolveName("Missing", processes, out var pid, out var error);

        ok.Should().BeFalse();
        pid.Should().Be(0);
        error.Should().Contain("No .NET process name starts with 'Missing'");
        error.Should().Contain("pid 101 (Orders.Api)");
    }

    [Fact]
    public void TryResolveName_ExactNameWinsOverLongerPrefixSibling_ReturnsExactPid()
    {
        var processes = new[]
        {
            Process(101, "Api"),
            Process(202, "ApiGateway"),
        };

        var ok = CliProcessSelector.TryResolveName("Api", processes, out var pid, out var error);

        ok.Should().BeTrue();
        pid.Should().Be(101);
        error.Should().BeNull();
    }

    private static DotnetProcess Process(int pid, string name)
        => new(pid, CommandLine: name, OperatingSystem: "linux", ProcessArchitecture: "x64", RuntimeVersion: "10.0.0", name);
}
