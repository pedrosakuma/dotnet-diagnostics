namespace DotnetDiagnostics.Core.Counters;

/// <summary>Starts Core-owned live EventCounter sessions.</summary>
public interface ICounterSessionFactory
{
    /// <summary>Starts a live counter session and returns after the EventPipe session is attached.</summary>
    Task<CounterSession> StartAsync(
        int processId,
        CounterSessionOptions? options = null,
        CancellationToken cancellationToken = default);
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
