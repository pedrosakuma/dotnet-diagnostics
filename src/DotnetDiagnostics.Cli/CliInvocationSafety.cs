using System.Collections.Immutable;
using System.Globalization;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Maps CLI syntax to Core safety requests, with explicit local portable-file policy.
/// <see cref="CliSafetyPreflight"/> applies the CLI interaction policy to the resolved descriptor.
/// </summary>
internal static class CliInvocationSafety
{
    internal static InvocationSafetyDescriptor Resolve(
        CliOptions options,
        IDiagnosticHandleStore? handles = null)
    {
        var request = CreateRequest(options, handles);
        return PortableFileSafety(request) ?? InvocationSafetyResolver.Resolve(request);
    }

    internal static InvocationSafetyDescriptor ResolveForPreflight(
        InvocationSafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (PortableFileSafety(request) is { } portable) return portable;
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

    private static InvocationSafetyDescriptor? PortableFileSafety(InvocationSafetyRequest request)
    {
        // Core's catalog predates these CLI actions; do not misclassify them as live module exports.
        if (request.Operation != DiagnosticOperationCatalog.GetBytes ||
            !request.Arguments.TryGetValue("kind", out var kind) || kind != "captures" ||
            !request.Arguments.TryGetValue("captureAction", out var action))
            return null;
        return action switch
        {
            "export" => new(InvocationRiskLevel.High, [],
                [DataExposure.PossibleConfidentialData, DataExposure.PossibleSecrets],
                [InvocationSideEffect.WritesArtifact, InvocationSideEffect.ExportsRawBytes],
                InvocationApprovalPolicy.Acknowledge,
                "Export copies every artifact in the selected owned captures into a portable local file.",
                ["Authorize disclosure of the whole capture and protect the destination file."]),
            "import" => new(InvocationRiskLevel.High, [],
                [DataExposure.PossibleConfidentialData, DataExposure.PossibleSecrets],
                [InvocationSideEffect.WritesArtifact], InvocationApprovalPolicy.Acknowledge,
                "Import validates foreign evidence in a confined worker and publishes new locally owned captures; partial publication can remain.",
                ["Configure only trusted worker assets; inspect per-entry results before retrying."]),
            "import-result" => new(InvocationRiskLevel.Moderate, [],
                [DataExposure.PossibleConfidentialData], [InvocationSideEffect.WritesArtifact],
                InvocationApprovalPolicy.Warn,
                "Receipt lookup reads owned import mappings and may reconcile interrupted publication or clean private staging.",
                ["Retain the original operation ID and timestamp; source provenance grants no authority."]),
            _ => null,
        };
    }

    internal static InvocationSafetyRequest CreateRequest(
        CliOptions options,
        IDiagnosticHandleStore? handles = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = CreateDiagnosticRequest(options, handles);
        if (options.Persist && options.Command != "session")
        {
            request = new InvocationSafetyRequest(request.Operation,
                request.Arguments.Select(static pair => KeyValuePair.Create<string, string?>(pair.Key, pair.Value))
                    .Append(KeyValuePair.Create<string, string?>("persist", "true")), request.Children);
        }
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
            "captures" => InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.GetBytes,
                ("kind", DiagnosticOperationCatalog.ByteKinds.Captures),
                ("captureAction", options.CaptureAction == "show" ? "describe" : options.CaptureAction)),
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
        if (kind == "gc-activities")
        {
            // Both streams have the existing runtime-event safety posture; this is not an MCP kind.
            return InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectEvents,
                ("kind", "gc"),
                ("depth", options.Depth),
                ("savePath", options.SavePath));
        }
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
        if (options.CaptureId is not null)
        {
            return InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.QuerySnapshot,
                ("captureId", options.CaptureId),
                ("view", options.View));
        }
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
