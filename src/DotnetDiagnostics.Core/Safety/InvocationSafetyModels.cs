using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Safety;

[JsonConverter(typeof(JsonStringEnumConverter<InvocationRiskLevel>))]
public enum InvocationRiskLevel
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("moderate")]
    Moderate,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("critical")]
    Critical,
}

[JsonConverter(typeof(JsonStringEnumConverter<InvocationApprovalPolicy>))]
public enum InvocationApprovalPolicy
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("warn")]
    Warn,

    [JsonStringEnumMemberName("acknowledge")]
    Acknowledge,

    [JsonStringEnumMemberName("human-approval")]
    HumanApproval,
}

[JsonConverter(typeof(JsonStringEnumConverter<TargetImpact>))]
public enum TargetImpact
{
    [JsonStringEnumMemberName("diagnostic-ipc-query")]
    DiagnosticIpcQuery,

    [JsonStringEnumMemberName("eventpipe-session")]
    EventPipeSession,

    [JsonStringEnumMemberName("bounded-runtime-overhead")]
    BoundedRuntimeOverhead,

    [JsonStringEnumMemberName("sampling-overhead")]
    SamplingOverhead,

    [JsonStringEnumMemberName("induced-gc")]
    InducedGc,

    [JsonStringEnumMemberName("process-suspension")]
    ProcessSuspension,

    [JsonStringEnumMemberName("ptrace-attach")]
    PtraceAttach,

    [JsonStringEnumMemberName("kernel-tracing")]
    KernelTracing,

    [JsonStringEnumMemberName("system-wide-tracing")]
    SystemWideTracing,

    [JsonStringEnumMemberName("profiler-attach")]
    ProfilerAttach,

    [JsonStringEnumMemberName("rejit")]
    Rejit,

    [JsonStringEnumMemberName("process-launch")]
    ProcessLaunch,

    [JsonStringEnumMemberName("process-termination")]
    ProcessTermination,
}

[JsonConverter(typeof(JsonStringEnumConverter<DataExposure>))]
public enum DataExposure
{
    [JsonStringEnumMemberName("process-metadata")]
    ProcessMetadata,

    [JsonStringEnumMemberName("runtime-configuration")]
    RuntimeConfiguration,

    [JsonStringEnumMemberName("aggregated-metrics")]
    AggregatedMetrics,

    [JsonStringEnumMemberName("stack-names")]
    StackNames,

    [JsonStringEnumMemberName("type-names")]
    TypeNames,

    [JsonStringEnumMemberName("method-names")]
    MethodNames,

    [JsonStringEnumMemberName("log-messages")]
    LogMessages,

    [JsonStringEnumMemberName("exception-messages")]
    ExceptionMessages,

    [JsonStringEnumMemberName("database-statements")]
    DatabaseStatements,

    [JsonStringEnumMemberName("activity-data")]
    ActivityData,

    [JsonStringEnumMemberName("eventsource-payloads")]
    EventSourcePayloads,

    [JsonStringEnumMemberName("network-data")]
    NetworkData,

    [JsonStringEnumMemberName("request-data")]
    RequestData,

    [JsonStringEnumMemberName("heap-metadata")]
    HeapMetadata,

    [JsonStringEnumMemberName("heap-values")]
    HeapValues,

    [JsonStringEnumMemberName("parameter-values")]
    ParameterValues,

    [JsonStringEnumMemberName("raw-process-memory")]
    RawProcessMemory,

    [JsonStringEnumMemberName("module-bytes")]
    ModuleBytes,

    [JsonStringEnumMemberName("raw-trace")]
    RawTrace,

    [JsonStringEnumMemberName("deployment-metadata")]
    DeploymentMetadata,

    [JsonStringEnumMemberName("possible-pii")]
    PossiblePii,

    [JsonStringEnumMemberName("possible-secrets")]
    PossibleSecrets,

    [JsonStringEnumMemberName("possible-confidential-data")]
    PossibleConfidentialData,
}

[JsonConverter(typeof(JsonStringEnumConverter<InvocationSideEffect>))]
public enum InvocationSideEffect
{
    [JsonStringEnumMemberName("writes-artifact")]
    WritesArtifact,

    [JsonStringEnumMemberName("deletes-artifact")]
    DeletesArtifact,

    [JsonStringEnumMemberName("exports-raw-bytes")]
    ExportsRawBytes,

    [JsonStringEnumMemberName("contacts-remote-symbol-server")]
    ContactsRemoteSymbolServer,

    [JsonStringEnumMemberName("contacts-cloud-api")]
    ContactsCloudApi,

    [JsonStringEnumMemberName("mutates-kubernetes-pod")]
    MutatesKubernetesPod,

    [JsonStringEnumMemberName("injects-ephemeral-container")]
    InjectsEphemeralContainer,

    [JsonStringEnumMemberName("starts-container")]
    StartsContainer,

    [JsonStringEnumMemberName("writes-configuration")]
    WritesConfiguration,

    [JsonStringEnumMemberName("restarts-container")]
    RestartsContainer,
}

public sealed record InvocationSafetyDescriptor(
    [property: JsonPropertyName("riskLevel")] InvocationRiskLevel RiskLevel,
    [property: JsonPropertyName("targetImpact")] ImmutableArray<TargetImpact> TargetImpact,
    [property: JsonPropertyName("dataExposure")] ImmutableArray<DataExposure> DataExposure,
    [property: JsonPropertyName("sideEffects")] ImmutableArray<InvocationSideEffect> SideEffects,
    [property: JsonPropertyName("approvalPolicy")] InvocationApprovalPolicy ApprovalPolicy,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("mitigations")] ImmutableArray<string> Mitigations);

public sealed record InvocationSafetyProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("arguments")] ImmutableDictionary<string, string> Arguments,
    [property: JsonPropertyName("safety")] InvocationSafetyDescriptor Safety);

public sealed record InvocationSafetyRegistration(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("discriminatorArgument")] string? DiscriminatorArgument,
    [property: JsonPropertyName("defaultDiscriminator")] string? DefaultDiscriminator,
    [property: JsonPropertyName("discriminatorValues")] ImmutableArray<string> DiscriminatorValues,
    [property: JsonPropertyName("conditionalArguments")] ImmutableArray<string> ConditionalArguments,
    [property: JsonPropertyName("profiles")] ImmutableArray<InvocationSafetyProfile> Profiles,
    [property: JsonPropertyName("maximumSafety")] InvocationSafetyDescriptor MaximumSafety)
{
    [JsonPropertyName("hasConditionalSafety")]
    public bool HasConditionalSafety =>
        DiscriminatorValues.Length > 1 || ConditionalArguments.Length > 0;
}

public sealed record InvocationSafetyRequest
{
    public InvocationSafetyRequest(
        string operation,
        IEnumerable<KeyValuePair<string, string?>>? arguments = null,
        IEnumerable<InvocationSafetyRequest>? children = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        Operation = operation.Trim().ToLowerInvariant();
        Arguments = NormalizeArguments(arguments);
        Children = children?.ToImmutableArray() ?? ImmutableArray<InvocationSafetyRequest>.Empty;
    }

    [JsonPropertyName("operation")]
    public string Operation { get; }

    [JsonPropertyName("arguments")]
    public ImmutableDictionary<string, string> Arguments { get; }

    [JsonPropertyName("children")]
    public ImmutableArray<InvocationSafetyRequest> Children { get; }

    public static InvocationSafetyRequest Create(
        string operation,
        params (string Name, object? Value)[] arguments)
        => new(
            operation,
            arguments.Select(static pair =>
                KeyValuePair.Create(pair.Name, NormalizeValue(pair.Value))));

    private static ImmutableDictionary<string, string> NormalizeArguments(
        IEnumerable<KeyValuePair<string, string?>>? arguments)
    {
        if (arguments is null)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                continue;
            }

            builder[pair.Key.Trim()] = pair.Value.Trim();
        }

        return builder.ToImmutable();
    }

    private static string? NormalizeValue(object? value)
        => value switch
        {
            null => null,
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
}

public sealed class InvocationSafetyResolutionException : InvalidOperationException
{
    public InvocationSafetyResolutionException(string operation, string message)
        : base(message)
    {
        Operation = operation;
    }

    public string Operation { get; }
}
