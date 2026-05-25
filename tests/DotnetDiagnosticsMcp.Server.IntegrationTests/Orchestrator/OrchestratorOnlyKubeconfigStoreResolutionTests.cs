using System;
using System.Collections.Generic;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// FIX 4 (#234 review): the kubeconfig handle store must be resolvable from a
/// container that wires ONLY the orchestrator (no Azure discovery). The store
/// ctor takes <see cref="DotnetDiagnosticsMcp.Server.Azure.AzureDiscoveryOptions"/>?
/// but MS.DI does not honor the nullable annotation, so registration must go
/// through a factory that calls <c>GetService&lt;AzureDiscoveryOptions&gt;()</c>.
/// </summary>
public sealed class OrchestratorOnlyKubeconfigStoreResolutionTests
{
    [Fact]
    public void OrchestratorOnly_Container_Resolves_KubeconfigHandleStore_WithoutAzureDiscovery()
    {
        var services = new ServiceCollection();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "true",
            })
            .Build();

        var enabled = services.AddOrchestratorServices(config);
        enabled.Should().BeTrue("Orchestrator:Enabled=true must register the orchestrator surface");

        using var sp = services.BuildServiceProvider(validateScopes: true);

        // The store, context and time provider all resolve cleanly even though
        // AddAzureDiscoveryServices was never called and AzureDiscoveryOptions was
        // never bound.
        var store = sp.GetService<IKubeconfigHandleStore>();
        store.Should().NotBeNull("IKubeconfigHandleStore must resolve from an orchestrator-only container (#234 FIX 4)");

        var context = sp.GetService<IKubeconfigContext>();
        context.Should().NotBeNull();

        var clock = sp.GetService<TimeProvider>();
        clock.Should().NotBeNull();
    }
}
