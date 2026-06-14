using DotnetDiagnostics.Core.Symbols;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SymbolPathBuilderTests : IDisposable
{
    private readonly string? _originalNtSymbolPath;

    public SymbolPathBuilderTests()
    {
        _originalNtSymbolPath = Environment.GetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable);
    }

    [Fact]
    public void Build_UsesExplicitPathBeforeConfiguredEnvNtEnvAndMainModuleDirectory()
    {
        Environment.SetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable, "nt-cache");
        var builder = new SymbolPathBuilder("mcp-cache");

        var path = builder.Build("explicit-cache", ["/app"]);

        path.Should().Be("explicit-cache;mcp-cache;nt-cache;/app");
    }

    [Fact]
    public void Build_FallsBackToConfiguredMcpSymbolPathBeforeNtEnvAndMainModuleDirectory()
    {
        Environment.SetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable, "nt-cache");
        var builder = new SymbolPathBuilder("mcp-cache");

        var path = builder.Build(explicitSymbolPath: null, ["/app"]);

        path.Should().Be("mcp-cache;nt-cache;/app");
    }

    [Fact]
    public void Build_FallsBackToNtSymbolPathBeforeMainModuleDirectory()
    {
        Environment.SetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable, "nt-cache");
        var builder = new SymbolPathBuilder();

        var path = builder.Build(explicitSymbolPath: null, ["/app"]);

        path.Should().Be("nt-cache;/app");
    }

    [Fact]
    public void Build_FallsBackToMainModuleDirectoryWhenNoExplicitOrEnvironmentPathsExist()
    {
        Environment.SetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable, null);
        var builder = new SymbolPathBuilder();

        var path = builder.Build(explicitSymbolPath: null, ["/app"]);

        path.Should().Be("/app");
    }

    [Fact]
    public void Build_DeduplicatesEquivalentEntriesAcrossTiers()
    {
        Environment.SetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable, "shared");
        var builder = new SymbolPathBuilder("shared");

        var path = builder.Build("shared", ["shared", "/app"]);

        path.Should().Be("shared;/app");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SymbolPathBuilder.NtSymbolPathEnvironmentVariable, _originalNtSymbolPath);
    }
}
