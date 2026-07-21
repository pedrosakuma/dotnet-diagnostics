namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Describes a target process to spawn-and-suspend-then-arm instead of attaching to an already-running
/// process id (issue #665 Part A — "unified ephemeral-process capture"). A pure DTO consumed via
/// <see cref="SuspendedColdStartLauncher.LaunchSuspendedAsync"/>; carries no MCP knowledge. In v1 this
/// is wired only into <c>collect_events(kind="startup")</c> — see
/// docs/design/ephemeral-process-capture-design.md for the full design and its v1 scope cuts
/// (single-kind, stdio-only, always-terminate-after-capture).
/// </summary>
/// <param name="FileName">Executable to spawn (e.g. a published apphost or <c>dotnet</c>). Must match
/// the same-process-as-runtime constraint documented on <see cref="ChildProcessLauncher"/> — a launcher
/// that forks a separate runtime child (notably <c>dotnet run</c>) is not supported.</param>
/// <param name="Arguments">Command-line arguments passed to <paramref name="FileName"/>.</param>
/// <param name="WorkingDirectory">Working directory for the spawned process. Null inherits the
/// server's own current directory.</param>
/// <param name="EnvironmentVariables">Extra environment variables layered onto the child's inherited
/// environment. <c>DOTNET_DiagnosticPorts</c> is reserved by the cold-start wiring and always wins over
/// any same-named entry supplied here.</param>
/// <param name="ConnectTimeoutSeconds">How long to wait for the spawned runtime to reverse-connect to
/// the diagnostic port before giving up. Must be in (0, 3600]. Defaults to 10.</param>
public sealed record LaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    double ConnectTimeoutSeconds = 10);
