using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>Creates Core-owned live EventPipe EventSource sessions.</summary>
public interface IEventSourceSessionFactory
{
    /// <summary>Creates a session. Attach handlers before starting it.</summary>
    EventSourceSession CreateSession(int processId, EventSourceSessionOptions options);
}
