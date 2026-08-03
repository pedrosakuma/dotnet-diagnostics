using System.Xml.Linq;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class SupervisorScopedAuthenticationTests
{
    private const string PrincipalName = "local-observer";
    private const string DefaultScope = "read-counters";

    [Fact]
    public void LinuxSystemdUnit_UsesNamedReadCountersBearer()
    {
        var unit = ReadRepoFile(
            "deploy", "supervisors", "linux", "dotnet-diagnostics-mcp.service");

        unit.Should().Contain("Environment=Auth__BearerTokens__0__Name=local-observer");
        unit.Should().Contain("Environment=Auth__BearerTokens__0__Token={{AUTH_BEARER_TOKEN}}");
        unit.Should().Contain("Environment=Auth__BearerTokens__0__Scopes__0=read-counters");
        unit.Should().NotContain("Environment=MCP_BEARER_TOKEN=");
    }

    [Fact]
    public void MacLaunchAgent_UsesNamedReadCountersBearer()
    {
        var path = RepoFile(
            "deploy", "supervisors", "macos",
            "io.github.pedrosakuma.dotnet-diagnostics-mcp.plist");
        var document = XDocument.Load(path);
        var environment = document
            .Descendants("key")
            .Single(element => element.Value == "EnvironmentVariables")
            .ElementsAfterSelf("dict")
            .First();
        var values = environment.Elements()
            .Chunk(2)
            .ToDictionary(pair => pair[0].Value, pair => pair[1].Value, StringComparer.Ordinal);

        values.Should().Contain("Auth__BearerTokens__0__Name", PrincipalName);
        values.Should().Contain("Auth__BearerTokens__0__Token", "{{AUTH_BEARER_TOKEN}}");
        values.Should().Contain("Auth__BearerTokens__0__Scopes__0", DefaultScope);
        values.Keys.Should().NotContain("MCP_BEARER_TOKEN");
    }

    [Fact]
    public void WindowsInstaller_UsesIndexedScopedEnvironmentAndPreservesUpdateSettings()
    {
        var installer = ReadRepoFile(
            "deploy", "supervisors", "windows", "Install-Service.ps1");

        installer.Should().Contain("$configurationPrefix = 'Auth__BearerTokens__0__'");
        installer.Should().Contain("\"${configurationPrefix}Scopes__$index\"");
        installer.Should().Contain("'local-observer'");
        installer.Should().Contain("@('read-counters')");
        installer.Should().Contain("[switch]$RotateToken");
        installer.Should().Contain("[string[]]$AddScopes");
        installer.Should().Contain("[switch]$Uninstall");
        installer.Should().Contain("$statePath = Join-Path $launcherDirectory 'install-state.json'");
        installer.Should().Contain("[int]$state.Port");
        installer.Should().Contain("[string]$state.TokenName");
        installer.Should().Contain("@($state.Scopes)");
        installer.Should().Contain("$installedTaskName");
        installer.Should().Contain("elseif (-not [string]::IsNullOrWhiteSpace($existingToken))");
        installer.Should().Contain("$installedScopes");
        installer.Should().Contain("$scopeCandidates += @($AddScopes)");
        installer.Should().Contain("Stop-ScheduledTask -TaskName $Name");
        installer.Should().Contain(
            "[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'User')");
        installer.Should().Contain(
            "[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'Process')");
        installer.Should().NotContain(
            "[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $Token, 'User')");
        var retainedState = installer[
            installer.IndexOf("[pscustomobject]@{", StringComparison.Ordinal)..
            installer.IndexOf("} | ConvertTo-Json", StringComparison.Ordinal)];
        retainedState.Should().Contain("Port = $resolvedPort");
        retainedState.Should().Contain("TaskName = $resolvedTaskName");
        retainedState.Should().Contain("TokenName = $resolvedTokenName");
        retainedState.Should().Contain("Scopes = @($resolvedScopes)");
        retainedState.Should().NotContain("Token =");
    }

    [Fact]
    public void SupervisorAndAuthorizationDocs_AgreeOnDefaultPrincipalAndScope()
    {
        var consumer = ReadRepoFile("docs", "consumer-install.md");
        var authorization = ReadRepoFile("docs", "authorization.md");

        foreach (var document in new[] { consumer, authorization })
        {
            document.Should().Contain(PrincipalName);
            document.Should().Contain(DefaultScope);
            document.Should().Contain("Auth__BearerTokens__0__Scopes__0");
        }

        consumer.Should().Contain("Scope expansion");
        consumer.Should().Contain("Rotate");
        consumer.Should().Contain("Uninstall");
        consumer.Should().Contain("Troubleshooting");
        consumer.Should().Contain("inspect_process(view=\"triage\")");
        consumer.Should().Contain("-RotateToken");
        consumer.Should().Contain("-AddScopes");
        consumer.Should().Contain("install-state.json");
        consumer.Should().Contain("the token is not written");
        authorization.Should().Contain("Local supervisor default");
        authorization.Should().Contain("It cannot use `inspect_process(view=\"runtime-config\")`");
    }

    private static string ReadRepoFile(params string[] segments)
        => File.ReadAllText(RepoFile(segments));

    private static string RepoFile(params string[] segments)
        => Path.Combine(new[] { FindRepoRoot() }.Concat(segments).ToArray());

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
