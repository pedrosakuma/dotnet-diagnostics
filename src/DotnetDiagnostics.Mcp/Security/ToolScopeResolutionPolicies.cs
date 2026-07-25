using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// Deployment policies that are documented alternatives to literal modifier scopes.
/// </summary>
internal sealed record ToolScopeResolutionPolicies(
    SymbolServerAllowlist? SymbolServerAllowlist,
    EventSourceAllowlist? EventSourceAllowlist,
    SensitiveValueGate? SensitiveValueGate,
    OrchestratorOptions? OrchestratorOptions)
{
    public static ToolScopeResolutionPolicies FromServices(IServiceProvider? services)
        => new(
            services?.GetService<SymbolServerAllowlist>(),
            services?.GetService<EventSourceAllowlist>(),
            services?.GetService<SensitiveValueGate>(),
            services?.GetService<OrchestratorOptions>());
}
