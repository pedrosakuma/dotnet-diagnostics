using DotnetDiagnostics.Core.Security;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Db;

internal sealed class EfCoreBridgeEventParser(SensitiveDataRedactor redactor) : IEventPipeDbProviderParser
{
    public const string ProviderName = "Microsoft-Diagnostics-DiagnosticSource";

    private const string EfCoreSourceName = "Microsoft.EntityFrameworkCore";
    private const string EfCoreStopBridgeEvent = "Stop";

    public IReadOnlyCollection<string> ProviderNames { get; } = [ProviderName];

    public void Handle(TraceEvent traceEvent, DbEventAggregationState state)
    {
        if (!traceEvent.EventName.EndsWith(EfCoreStopBridgeEvent, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var arguments = DbEventPipeParsing.ExtractArguments(traceEvent.PayloadByName("Arguments"));
        var sourceName = DbEventPipeParsing.FirstNonEmpty(
            DbEventPipeParsing.ConvertToString(traceEvent.PayloadByName("ActivitySourceName")),
            DbEventPipeParsing.ConvertToString(traceEvent.PayloadByName("SourceName")));
        if (!string.Equals(sourceName, EfCoreSourceName, StringComparison.Ordinal))
        {
            return;
        }

        var tags = DbEventPipeParsing.ParseTagPairs(DbEventPipeParsing.GetArgument(arguments, "Tags"));
        var rawCommandText = DbEventPipeParsing.FirstNonEmpty(
            DbEventPipeParsing.GetTag(tags, "db.statement"),
            DbEventPipeParsing.GetTag(tags, "db.query.text"),
            DbEventPipeParsing.GetTag(tags, "db.command.text"));
        if (string.IsNullOrWhiteSpace(rawCommandText))
        {
            return;
        }

        var sanitizedCommandText = redactor.RedactSqlText(rawCommandText) ?? string.Empty;
        var rawConnectionString = DbEventPipeParsing.FirstNonEmpty(
            DbEventPipeParsing.GetTag(tags, "db.connection_string"),
            DbEventPipeParsing.BuildConnectionString(
                DbEventPipeParsing.GetTag(tags, "server.address"),
                DbEventPipeParsing.FirstNonEmpty(
                    DbEventPipeParsing.GetTag(tags, "db.name"),
                    DbEventPipeParsing.GetTag(tags, "db.namespace"))));
        var sanitizedConnectionString = redactor.Redact(rawConnectionString) ?? string.Empty;
        var stoppedAt = new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
        var duration = DbEventPipeParsing.ParseDuration(arguments);
        var startedAt = DbEventPipeParsing.ParseStartedAt(arguments) ?? (duration is { } observedDuration ? stoppedAt - observedDuration : stoppedAt);
        state.CompleteCommand(
            new PendingCommand(
                Provider: EfCoreSourceName,
                Key: DbEventPipeParsing.BuildAggregateKey(sanitizedCommandText, sanitizedConnectionString),
                CommandTextHash: DbEventPipeParsing.HashCommandText(sanitizedCommandText),
                CommandTextSanitized: sanitizedCommandText,
                ConnectionStringSanitized: sanitizedConnectionString,
                ScopeId: DbEventPipeParsing.BuildScopeId(
                    DbEventPipeParsing.GetArgument(arguments, "TraceId"),
                    DbEventPipeParsing.GetArgument(arguments, "ParentSpanId"),
                    traceEvent.RelatedActivityID,
                    traceEvent.ActivityID),
                StartedAt: startedAt),
            stoppedAt,
            Math.Max(0, (duration ?? (stoppedAt - startedAt)).TotalMilliseconds));
    }
}
