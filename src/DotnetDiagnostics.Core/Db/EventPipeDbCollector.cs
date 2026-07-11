using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Db;

public sealed class EventPipeDbCollector : IDbCollector
{
    private const string FilterArgumentName = "FilterAndPayloadSpecs";
    private const long DiagnosticSourceKeywords = 0x1 | 0x2;
    private const string ActivityTransformSuffix = ":-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate";

    private readonly ILogger<EventPipeDbCollector> _logger;
    private readonly Dictionary<string, IEventPipeDbProviderParser> _parsersByProvider;

    public EventPipeDbCollector(SensitiveDataRedactor redactor, ILogger<EventPipeDbCollector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(redactor);

        _logger = logger ?? NullLogger<EventPipeDbCollector>.Instance;
        _parsersByProvider = BuildParserMap(
            new EfCoreBridgeEventParser(redactor),
            new SqlClientEventParser(redactor));
    }

    public async Task<DbSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "intervalSeconds must be >= 1.");
        }

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(
                CreateProviders(intervalSeconds),
                requestRundown: false,
                circularBufferMB: 128,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var state = new DbEventAggregationState();
        var processingTask = Task.Run(() => ProcessEvents(processId, session, state), cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        return state.BuildSnapshot(processId, startedAt, duration);
    }

    private void ProcessEvents(int processId, EventPipeSession session, DbEventAggregationState state)
    {
        try
        {
            using var source = new EventPipeEventSource(session.EventStream);
            source.Dynamic.All += traceEvent =>
            {
                try
                {
                    if (_parsersByProvider.TryGetValue(traceEvent.ProviderName, out var parser))
                    {
                        parser.Handle(traceEvent, state);
                    }
                }
                catch (Exception ex)
                {
                    state.AddNote($"Warning: failed to parse {traceEvent.ProviderName}/{traceEvent.EventName}: {ex.GetType().Name}.");
                }
            };

            source.Process();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DB EventPipe source ended for pid {Pid}.", processId);
        }
    }

    private static Dictionary<string, IEventPipeDbProviderParser> BuildParserMap(params IEventPipeDbProviderParser[] parsers)
    {
        var map = new Dictionary<string, IEventPipeDbProviderParser>(StringComparer.Ordinal);
        foreach (var parser in parsers)
        {
            foreach (var providerName in parser.ProviderNames)
            {
                map[providerName] = parser;
            }
        }

        return map;
    }

    private static EventPipeProvider[] CreateProviders(int intervalSeconds) =>
    [
        new(
            EfCoreBridgeEventParser.ProviderName,
            EventLevel.Verbose,
            DiagnosticSourceKeywords,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FilterArgumentName] = $"[AS]Microsoft.EntityFrameworkCore/Stop{ActivityTransformSuffix}",
            }),
        new(
            SqlClientEventParser.MicrosoftProviderName,
            EventLevel.Verbose,
            (long)EventKeywords.All,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EventCounterIntervalSec"] = intervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
        new(
            SqlClientEventParser.SystemProviderName,
            EventLevel.Verbose,
            (long)EventKeywords.All,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EventCounterIntervalSec"] = intervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
    ];
}
