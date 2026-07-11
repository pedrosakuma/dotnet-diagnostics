using DotnetDiagnostics.Core.Security;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Db;

internal sealed class SqlClientEventParser(SensitiveDataRedactor redactor) : IEventPipeDbProviderParser
{
    public const string MicrosoftProviderName = "Microsoft.Data.SqlClient.EventSource";
    public const string SystemProviderName = "System.Data.SqlClient.EventSource";

    public IReadOnlyCollection<string> ProviderNames { get; } = [MicrosoftProviderName, SystemProviderName];

    public void Handle(TraceEvent traceEvent, DbEventAggregationState state)
    {
        if (string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
        {
            var payload = DbEventPipeParsing.ExtractCounterPayload(traceEvent);
            if (payload is not null)
            {
                var stats = state.GetOrAddPoolStats(traceEvent.ProviderName);
                stats.ObserveCounter(payload.Name, payload.Value);
            }

            return;
        }

        if (string.Equals(traceEvent.EventName, "BeginExecute", StringComparison.Ordinal))
        {
            var objectId = DbEventPipeParsing.PayloadInt32(traceEvent, 0);
            if (objectId == 0)
            {
                return;
            }

            var sanitizedCommandText = redactor.RedactSqlText(DbEventPipeParsing.PayloadString(traceEvent, 3)) ?? string.Empty;
            var sanitizedConnectionString = redactor.Redact(
                DbEventPipeParsing.BuildConnectionString(
                    DbEventPipeParsing.PayloadString(traceEvent, 1),
                    DbEventPipeParsing.PayloadString(traceEvent, 2))) ?? string.Empty;
            state.SetPendingCommand(
                DbEventPipeParsing.BuildProviderObjectKey(traceEvent.ProviderName, objectId),
                new PendingCommand(
                    Provider: traceEvent.ProviderName,
                    Key: DbEventPipeParsing.BuildAggregateKey(sanitizedCommandText, sanitizedConnectionString),
                    CommandTextHash: DbEventPipeParsing.HashCommandText(sanitizedCommandText),
                    CommandTextSanitized: sanitizedCommandText,
                    ConnectionStringSanitized: sanitizedConnectionString,
                    ScopeId: DbEventPipeParsing.BuildScopeId(null, null, traceEvent.RelatedActivityID, traceEvent.ActivityID),
                    StartedAt: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero)));
            return;
        }

        if (string.Equals(traceEvent.EventName, "EndExecute", StringComparison.Ordinal))
        {
            var objectId = DbEventPipeParsing.PayloadInt32(traceEvent, 0);
            state.TryCompletePendingCommand(
                DbEventPipeParsing.BuildProviderObjectKey(traceEvent.ProviderName, objectId),
                new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero));
            return;
        }

        var message = DbEventPipeParsing.ExtractFreeformMessage(traceEvent);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (DbEventPipeParsing.LooksLikePoolExhausted(message))
        {
            var stats = state.GetOrAddPoolStats(traceEvent.ProviderName);
            stats.PoolExhaustedCount++;
            state.AddNote($"Connection pool exhaustion signalled by {traceEvent.ProviderName}/{traceEvent.EventName}.");
        }
    }
}
