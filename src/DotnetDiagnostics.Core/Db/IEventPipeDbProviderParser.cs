using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Db;

internal interface IEventPipeDbProviderParser
{
    IReadOnlyCollection<string> ProviderNames { get; }

    void Handle(TraceEvent traceEvent, DbEventAggregationState state);
}
