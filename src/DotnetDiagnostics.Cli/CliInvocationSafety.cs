using System.Collections.Immutable;
using System.Globalization;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Maps CLI syntax to the same canonical Core safety request used by MCP invocations.
/// <see cref="CliSafetyPreflight"/> applies the CLI interaction policy to the resolved descriptor.
/// </summary>
internal static class CliInvocationSafety
{
    internal static InvocationSafetyDescriptor Resolve(
        CliOptions options,
        IDiagnosticHandleStore? handles = null)
        => InvocationSafetyResolver.Resolve(CreateRequest(options, handles));

    internal static InvocationSafetyDescriptor ResolveForPreflight(
        InvocationSafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return InvocationSafetyResolver.Resolve(request);
        }
        catch (InvocationSafetyResolutionException)
        {
            var safety = InvocationSafetyRegistry.Get(request.Operation).MaximumSafety;
            foreach (var child in request.Children)
            {
                safety = Merge(safety, ResolveForPreflight(child));
            }

            return safety;
        }
    }

    internal static InvocationSafetyRequest CreateRequest(
        CliOptions options,
        IDiagnosticHandleStore? handles = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = CreateDiagnosticRequest(options, handles);
        return options.Launch
            ? new InvocationSafetyRequest(
                DiagnosticOperationCatalog.LaunchProcess,
                children: [request])
            : request;
    }

    private static InvocationSafetyRequest CreateDiagnosticRequest(
        CliOptions options,
        IDiagnosticHandleStore? handles)
        => options.Command switch
        {
            "docker-bootstrap" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.DockerBootstrap,
                ("apply", options.ApplyBootstrapProfile),
                ("replace", options.ReplaceBootstrapProfile)),
            "processes" => InspectProcess(DiagnosticOperationCatalog.InspectProcessViews.List),
            "capabilities" => InspectProcess(DiagnosticOperationCatalog.InspectProcessViews.Capabilities),
            "doctor" => InspectProcess(DiagnosticOperationCatalog.InspectProcessViews.Preflight),
            "collect" => Collect(options),
            "inspect" => InspectProcess(options.View),
            "inspect-heap" => InspectHeap(options),
            "dump" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectProcessDump,
                ("dumpType", options.DumpType),
                ("outputDirectory", options.OutDir)),
            "query" => Query(options, handles),
            "get-bytes" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.GetBytes,
                ("kind", options.Kind)),
            "compare" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CompareToBaseline,
                ("savePath", options.SavePath)),
            "investigate" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.StartInvestigation),
            "export-summary" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.ExportInvestigationSummary),
            "completion" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.ShellCompletion),
            "session" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.Session),
            _ => throw new InvocationSafetyResolutionException(
                options.Command ?? string.Empty,
                $"CLI command '{options.Command}' has no safety mapping."),
        };

    private static InvocationSafetyRequest InspectProcess(string? view)
        => InvocationSafetyRequest.Create(
            DiagnosticOperationCatalog.InspectProcess,
            ("view", view));

    private static InvocationSafetyRequest Collect(CliOptions options)
    {
        var kind = options.Kind?.Trim().ToLowerInvariant();
        if (DiagnosticOperationCatalog.CollectEventsKinds.Cli.Contains(kind, StringComparer.Ordinal))
        {
            return InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectEvents,
                ("kind", kind),
                ("depth", options.Depth),
                ("unsafeProvider", options.UnsafeProvider),
                ("triggerWhen", options.CaptureWhen),
                ("captureKind", options.CaptureKind),
                ("launch", options.SuspendStartup ? "present" : null),
                ("savePath", options.SavePath));
        }

        if (DiagnosticOperationCatalog.CollectSampleKinds.Cli.Contains(kind, StringComparer.Ordinal))
        {
            return InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectSample,
                ("kind", kind),
                ("resolveMethodInstantiations", options.ResolveMethodInstantiations),
                ("resolveSourceLines", options.ResolveSourceLines),
                ("exportTrace", options.ExportTrace),
                ("symbolPath", options.SymbolPath));
        }

        if (kind == DiagnosticOperationCatalog.ThreadSnapshotCliKind)
        {
            return InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectThreadSnapshot,
                ("dumpFile", options.DumpFile),
                ("symbolPath", options.SymbolPath));
        }

        throw new InvocationSafetyResolutionException(
            DiagnosticOperationCatalog.CollectEvents,
            $"CLI collect kind '{options.Kind}' has no safety mapping.");
    }

    private static InvocationSafetyRequest InspectHeap(CliOptions options)
    {
        var source = options.Sources.Count switch
        {
            1 => options.Sources[0],
            _ when options.DumpFile is not null => DiagnosticOperationCatalog.HeapSources.Dump,
            _ => DiagnosticOperationCatalog.HeapSources.Live,
        };
        return InvocationSafetyRequest.Create(
            DiagnosticOperationCatalog.InspectHeap,
            ("source", source),
            ("includeRetentionPaths", options.IncludeRetentionPaths),
            ("includeStaticFields", options.IncludeStaticFields),
            ("includeDelegateTargets", options.IncludeDelegateTargets),
            ("includeDuplicateStrings", options.IncludeDuplicateStrings),
            ("exportTrace", options.ExportTrace),
            ("symbolPath", options.SymbolPath));
    }

    private static InvocationSafetyRequest Query(
        CliOptions options,
        IDiagnosticHandleStore? handles)
    {
        var handleKind = options.Handle is { Length: > 0 } handle
            ? handles?.LookupWithKind(handle).Lookup?.Kind
            : null;
        return InvocationSafetyRequest.Create(
            DiagnosticOperationCatalog.QuerySnapshot,
            ("handle", options.Handle),
            ("handleKind", handleKind),
            ("view", options.View));
    }

    private static InvocationSafetyDescriptor Merge(
        InvocationSafetyDescriptor left,
        InvocationSafetyDescriptor right)
        => new(
            (InvocationRiskLevel)Math.Max((int)left.RiskLevel, (int)right.RiskLevel),
            Union(left.TargetImpact, right.TargetImpact),
            Union(left.DataExposure, right.DataExposure),
            Union(left.SideEffects, right.SideEffects),
            (InvocationApprovalPolicy)Math.Max((int)left.ApprovalPolicy, (int)right.ApprovalPolicy),
            string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
                ? left.Reason
                : $"{left.Reason} {right.Reason}",
            left.Mitigations.Concat(right.Mitigations).Distinct(StringComparer.Ordinal).ToImmutableArray());

    private static ImmutableArray<T> Union<T>(
        ImmutableArray<T> left,
        ImmutableArray<T> right)
        where T : struct, Enum
        => left.Concat(right)
            .Distinct()
            .OrderBy(static value => Convert.ToInt32(value, CultureInfo.InvariantCulture))
            .ToImmutableArray();
}
