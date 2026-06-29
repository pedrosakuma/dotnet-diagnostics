using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class RuntimeConfigInspectorTests
{
    [Fact]
    public void FilterAllowlistedEnvironmentEntries_DropsSecrets_And_Keeps_RuntimePrefixes()
    {
        var filtered = RuntimeConfigInspector.FilterAllowlistedEnvironmentEntries(
        [
            "SECRET_TOKEN=abc",
            "MY_KEY=xyz",
            "DOTNET_gcServer=1",
            "ASPNETCORE_URLS=http://localhost",
        ]);

        filtered.Should().BeEquivalentTo(
        [
            new EnvVarEntry("ASPNETCORE_URLS", "http://localhost"),
            new EnvVarEntry("DOTNET_gcServer", "1"),
        ], options => options.WithStrictOrdering());
        filtered.Should().OnlyContain(entry => RuntimeConfigInspector.IsAllowlistedEnvironmentVariable(entry.Name));
        filtered.Should().NotContain(entry => entry.Name == "SECRET_TOKEN" || entry.Name == "MY_KEY");
    }

    [Fact]
    public void FilterAllowlistedEnvironmentEntries_HandlesEdgeCases()
    {
        var filtered = RuntimeConfigInspector.FilterAllowlistedEnvironmentEntries(
        [
            "dotnet_gcServer=1",           // lowercase prefix
            "DOTNET=no_underscore",        // prefix without underscore
            "DOTNET_SYSTEM_FOO=bar",       // DOTNET_SYSTEM_ prefix
            "COMPlus_TieredCompilation=1", // COMPlus_ prefix
            "=EMPTY_NAME",                 // missing name
            "NO_EQUALS",                   // missing equals
            "DOTNET_Multi=Val=ue",         // multiple equals in value
            "",                            // empty string
            "   ",                         // whitespace only
            "AWS_SECRET_KEY=supersecret",  // non-allowlisted prefix
        ]);

        filtered.Should().BeEquivalentTo(
        [
            new EnvVarEntry("COMPlus_TieredCompilation", "1"),
            new EnvVarEntry("dotnet_gcServer", "1"),       // case-insensitive matching
            new EnvVarEntry("DOTNET_Multi", "Val=ue"),     // equals in value preserved
            new EnvVarEntry("DOTNET_SYSTEM_FOO", "bar"),
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public void IsAllowlistedEnvironmentVariable_CaseInsensitive()
    {
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("DOTNET_gcServer").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("dotnet_gcServer").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("DoTnEt_gcServer").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("ASPNETCORE_URLS").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("aspnetcore_urls").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("SECRET_KEY").Should().BeFalse();
    }

    [Fact]
    public void IsAllowlistedEnvironmentVariable_RejectsMalformed()
    {
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("").Should().BeFalse();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("   ").Should().BeFalse();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("DOTNET").Should().BeFalse(); // no underscore
    }

    [Fact]
    public void ParseConfigProperties_ReadsSwitchesAndKnobs_NameSorted_ValuesNormalised()
    {
        const string json = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "configProperties": {
              "System.GC.Server": true,
              "System.Net.Http.EnableActivityPropagation": false,
              "System.Net.SocketsHttpHandler.Http3Support": true,
              "Switch.System.Net.DontEnableSystemDefaultTlsVersions": false,
              "System.Threading.ThreadPool.MinThreads": 8,
              "Custom.Connection": "Server=local"
            }
          }
        }
        """;

        var switches = RuntimeConfigInspector.ParseConfigProperties(json);

        switches.Should().BeEquivalentTo(
        [
            new AppContextSwitchEntry("Custom.Connection", "Server=local"),
            new AppContextSwitchEntry("Switch.System.Net.DontEnableSystemDefaultTlsVersions", "false"),
            new AppContextSwitchEntry("System.GC.Server", "true"),
            new AppContextSwitchEntry("System.Net.Http.EnableActivityPropagation", "false"),
            new AppContextSwitchEntry("System.Net.SocketsHttpHandler.Http3Support", "true"),
            new AppContextSwitchEntry("System.Threading.ThreadPool.MinThreads", "8"),
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public void ParseConfigProperties_ReturnsEmpty_WhenNoConfigProperties()
    {
        RuntimeConfigInspector.ParseConfigProperties("{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}").Should().BeEmpty();
        RuntimeConfigInspector.ParseConfigProperties("{}").Should().BeEmpty();
        RuntimeConfigInspector.ParseConfigProperties("").Should().BeEmpty();
    }
}
