namespace DotnetDiagnostics.Core.Startup;

public sealed record StartupSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalAssemblyLoads,
    int TotalModuleLoads,
    int TotalTimelineEvents,
    int TotalDiEvents,
    int DiServiceProviderBuiltCount,
    int DiServiceProviderDescriptorsCount,
    int DiCallSiteBuiltCount,
    int DiServiceResolvedCount,
    int DiExpressionTreeGeneratedCount,
    int DiDynamicMethodBuiltCount,
    int DiServiceRealizationFailedCount,
    TimeSpan ObservedDiActivityDuration,
    IReadOnlyList<StartupAssemblyLoad> AssemblyLoads,
    IReadOnlyList<StartupLoadAggregate> AssemblyAggregates,
    IReadOnlyList<StartupModuleLoad> ModuleLoads,
    IReadOnlyList<StartupLoadAggregate> ModuleAggregates,
    IReadOnlyList<StartupDiEvent> DiEvents,
    IReadOnlyList<StartupTimelineEvent> Timeline,
    bool Truncated,
    IReadOnlyList<string> Notes);

public sealed record StartupAssemblyLoad(
    DateTimeOffset Timestamp,
    string EventName,
    string AssemblyName,
    long? AssemblyId);

public sealed record StartupModuleLoad(
    DateTimeOffset Timestamp,
    string EventName,
    string ModuleName,
    string? ModulePath,
    long? ModuleId,
    long? AssemblyId);

public sealed record StartupDiEvent(
    DateTimeOffset Timestamp,
    string EventName,
    int? ServiceProviderHashCode,
    string? ServiceType,
    int? SingletonServices,
    int? ScopedServices,
    int? TransientServices,
    int? ClosedGenericServices,
    int? OpenGenericServices,
    int? NodeCount,
    int? MethodSize,
    int? ChunkIndex,
    int? ChunkCount);

public sealed record StartupTimelineEvent(
    DateTimeOffset Timestamp,
    string Category,
    string EventName,
    string Name);

public sealed record StartupLoadAggregate(
    string Name,
    int Count,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record StartupDiAggregate(
    int TotalEvents,
    int ServiceProviderBuiltCount,
    int ServiceProviderDescriptorsCount,
    int CallSiteBuiltCount,
    int ServiceResolvedCount,
    int ExpressionTreeGeneratedCount,
    int DynamicMethodBuiltCount,
    int ServiceRealizationFailedCount,
    TimeSpan ObservedActivityDuration);
