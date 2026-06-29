using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// A child .NET process launched with its runtime suspended on a reverse-connect diagnostic port (see
/// <see cref="SuspendedColdStartLauncher"/>). Owns the child process, the listening
/// <see cref="DiagnosticsClientConnector"/>, and the on-disk socket; disposing tears all three down.
/// The caller starts an EventPipe session on <see cref="Client"/> while the runtime is still suspended,
/// then calls <see cref="ResumeAsync"/> exactly once so the captured trace begins before any managed
/// code executes.
/// </summary>
public sealed class SuspendedTarget : IAsyncDisposable
{
    private readonly DiagnosticsClientConnector _connector;
    private readonly LaunchedTarget _owner;
    private readonly string _portPath;
    private bool _resumed;
    private bool _disposed;

    internal SuspendedTarget(DiagnosticsClientConnector connector, LaunchedTarget owner, int processId, string portPath)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _portPath = portPath;
        ProcessId = processId;
    }

    /// <summary>Diagnostics client for the suspended runtime; use it to arm the session before resume.</summary>
    public DiagnosticsClient Client => _connector.Instance;

    /// <summary>Operating-system process id of the launched, suspended target.</summary>
    public int ProcessId { get; }

    /// <summary>True once the launched target has exited.</summary>
    public bool HasExited => _owner.HasExited;

    /// <summary>
    /// Resumes the suspended runtime so managed code begins executing. Idempotent — calling it more than
    /// once is a no-op. Must be invoked only after the EventPipe session is armed.
    /// </summary>
    public ValueTask ResumeAsync()
    {
        if (!_resumed)
        {
            _resumed = true;
            Client.ResumeRuntime();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Terminates the child, disposes the listening connector, and removes the socket. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { await _connector.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        await _owner.DisposeAsync().ConfigureAwait(false);
        SuspendedColdStartLauncher.TryDeletePort(_portPath);
    }
}
