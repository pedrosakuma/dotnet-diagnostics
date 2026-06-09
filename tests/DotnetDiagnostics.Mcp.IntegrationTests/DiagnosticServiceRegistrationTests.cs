using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Guards the #284 relocation of <c>AddDiagnosticCoreServices</c> from Server to Core. The
/// Server entry point must delegate the whole engine to the Core registration and then re-add
/// exactly the host-specific bits, without dropping any registration or reordering the
/// <see cref="IThreadSnapshotBackend"/> sequence that drilldown routing depends on.
/// </summary>
public class DiagnosticServiceRegistrationTests
{
    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddDiagnosticCoreServices(configuredSymbolPath: null, configuration: configuration);
        return services;
    }

    [Fact]
    public void RegistersThreadSnapshotBackendsInDeterministicOrder()
    {
        var services = BuildServices();

        var implementationTypes = services
            .Where(d => d.ServiceType == typeof(IThreadSnapshotBackend))
            .Select(d => d.ImplementationType)
            .ToList();

        implementationTypes.Should().Equal(
            typeof(ClrMdThreadSnapshotBackend),
            typeof(LinuxNativeThreadSnapshotBackend),
            typeof(EtwNativeThreadSnapshotBackend),
            typeof(PerfReplayThreadSnapshotBackend));
    }

    [Fact]
    public void RegistersCoreEngineServices()
    {
        var services = BuildServices();

        services.Should().Contain(d => d.ServiceType == typeof(ICounterCollector));
        services.Should().Contain(d => d.ServiceType == typeof(IDiagnosticHandleStore));
        services.Should().Contain(d => d.ServiceType == typeof(IThreadSnapshotInspector));

        // The Core registration must resolve into a working graph end to end.
        using var provider = services.BuildServiceProvider();
        provider.GetService<IDiagnosticHandleStore>().Should().NotBeNull();
        provider.GetService<ICounterCollector>().Should().NotBeNull();
    }

    [Fact]
    public void RegistersHostSpecificBitsExactlyOnce()
    {
        var services = BuildServices();

        // The three deliberately host-specific registrations that stay in Server (#284).
        services.Should().ContainSingle(d => d.ServiceType == typeof(LegacyDiagnosticsFlagDeprecation));
        services.Should().ContainSingle(d => d.ServiceType == typeof(ModelContextProtocol.IMcpTaskStore));
        services.Should().Contain(d => d.ServiceType == typeof(IHostedService));
    }
}
