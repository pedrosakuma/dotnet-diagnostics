using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>Creates continuous EventPipe EventSource sessions.</summary>
public sealed class EventPipeEventSourceSessionFactory : IEventSourceSessionFactory
{
    private readonly ILogger<EventSourceSession>? _logger;

    /// <summary>Creates a factory with an optional session logger.</summary>
    public EventPipeEventSourceSessionFactory(ILogger<EventSourceSession>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public EventSourceSession CreateSession(int processId, EventSourceSessionOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var providers = options.Providers
            .Select(provider => new EventPipeProvider(
                provider.ProviderName,
                (EventLevel)provider.EventLevel,
                provider.Keywords))
            .ToArray();

        return new EventSourceSession(
            processId,
            options,
            cancellationToken => new DiagnosticsClient(processId).StartEventPipeSessionWithTimeoutAsync(
                providers,
                requestRundown: false,
                circularBufferMB: 64,
                TimeSpan.FromSeconds(30),
                cancellationToken),
            static _ => { },
            _logger);
    }

    internal static void ValidateOptions(EventSourceSessionOptions options)
    {
        if (options.Providers.Count is < 1 or > EventSourceSessionOptions.MaxProviderCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Provider count must be between 1 and {EventSourceSessionOptions.MaxProviderCount}.");
        }

        if (options.ObservationCapacity is < 1 or > EventSourceSessionOptions.MaxObservationCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Observation capacity must be between 1 and {EventSourceSessionOptions.MaxObservationCapacity}.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ProviderName)
                || provider.ProviderName.Length > EventSourceSessionOptions.MaxProviderNameLength)
            {
                throw new ArgumentException(
                    $"Provider names must be non-empty and no longer than {EventSourceSessionOptions.MaxProviderNameLength} characters.",
                    nameof(options));
            }

            if (provider.EventLevel is < 0 or > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Provider event level must be between 0 and 5.");
            }

            if (!names.Add(provider.ProviderName))
            {
                throw new ArgumentException("Provider names must be unique (case-insensitive).", nameof(options));
            }
        }
    }
}
