namespace DotnetDiagnostics.Core.Counters;

/// <summary>Creates Core-owned live EventCounter sessions.</summary>
public interface ICounterSessionFactory
{
    /// <summary>Creates a session. Attach event handlers before calling <see cref="CounterSession.StartAsync"/>.</summary>
    CounterSession CreateSession(
        int processId,
        CounterSessionOptions? options = null);
}

/// <summary>Options for a live EventCounter session.</summary>
public sealed record CounterSessionOptions
{
    /// <summary>Maximum queue capacity allowed for one live session.</summary>
    public const int MaxAllowedObservationCapacity = 16_384;

    /// <summary>Maximum number of EventSource providers allowed for one live session.</summary>
    public const int MaxAllowedProviderCount = 64;

    /// <summary>Maximum length of an EventSource provider name.</summary>
    public const int MaxProviderNameLength = 256;

    /// <summary>EventSource providers to subscribe to. Defaults to the standard runtime and ASP.NET providers.</summary>
    public IReadOnlyList<string>? Providers { get; init; }

    /// <summary>Counter reporting interval, in seconds.</summary>
    public int IntervalSeconds { get; init; } = 1;

    /// <summary>Maximum number of pending observations retained for the single consumer.</summary>
    public int ObservationCapacity { get; init; } = 256;
}
