using System.Collections.Immutable;

namespace DotnetDiagnostics.Core.Safety;

/// <summary>
/// Resolves operational impact and data-exposure risk for one concrete invocation.
/// Authorization is intentionally not represented here and remains a separate host concern.
/// </summary>
public static class InvocationSafetyResolver
{
    public static InvocationSafetyDescriptor Resolve(InvocationSafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registration = InvocationSafetyRegistry.Get(request.Operation);

        if (request.Operation == DiagnosticOperationCatalog.CollectBatch)
        {
            return ResolveBatch(request);
        }
        if (request.Operation == DiagnosticOperationCatalog.LaunchProcess)
        {
            return ResolveLaunchProcess(request);
        }

        var profileId = ResolveBaseProfileId(registration, request);
        var safety = InvocationSafetyRegistry.GetProfile(request.Operation, profileId).Safety;

        return request.Operation switch
        {
            DiagnosticOperationCatalog.CollectEvents => ResolveCollectEvents(request, safety),
            DiagnosticOperationCatalog.CollectSample => ResolveCollectSample(request, safety),
            DiagnosticOperationCatalog.InspectHeap => ResolveInspectHeap(request, safety),
            DiagnosticOperationCatalog.QuerySnapshot => ResolveQuerySnapshot(request, safety),
            DiagnosticOperationCatalog.CollectThreadSnapshot => ResolveThreadSnapshot(request),
            DiagnosticOperationCatalog.CompareToBaseline => ResolveSavedOutput(request, safety),
            DiagnosticOperationCatalog.ListOrchestrator => ResolveListOrchestrator(request, safety),
            DiagnosticOperationCatalog.AttachToPod => ResolveAttach(request, safety),
            DiagnosticOperationCatalog.DiscoverAzure => ResolveDiscoverAzure(request, safety),
            DiagnosticOperationCatalog.DockerBootstrap => ResolveDockerBootstrap(request, safety),
            _ => safety,
        };
    }

    private static InvocationSafetyDescriptor ResolveBatch(InvocationSafetyRequest request)
    {
        if (request.Children.Length == 0)
        {
            throw new InvocationSafetyResolutionException(
                request.Operation,
                "collect_batch safety resolution requires every normalized child invocation.");
        }

        var safety = InvocationSafetyRegistry.GetProfile(request.Operation, "default").Safety;
        foreach (var child in request.Children)
        {
            if (child.Operation is not (DiagnosticOperationCatalog.CollectEvents or DiagnosticOperationCatalog.CollectSample))
            {
                throw new InvocationSafetyResolutionException(
                    request.Operation,
                    $"collect_batch child operation '{child.Operation}' is not registered for batching.");
            }

            safety = Merge(safety, Resolve(child));
        }

        return safety;
    }

    private static InvocationSafetyDescriptor ResolveLaunchProcess(InvocationSafetyRequest request)
    {
        if (request.Children.Length != 1)
        {
            throw new InvocationSafetyResolutionException(
                request.Operation,
                "launch_process safety resolution requires exactly one nested diagnostic invocation.");
        }

        return Merge(
            InvocationSafetyRegistry.GetProfile(request.Operation, "default").Safety,
            Resolve(request.Children[0]));
    }

    private static string ResolveBaseProfileId(
        InvocationSafetyRegistration registration,
        InvocationSafetyRequest request)
    {
        if (registration.DiscriminatorArgument is null)
        {
            return request.Operation == DiagnosticOperationCatalog.CollectThreadSnapshot
                ? HasValue(request, "dumpFilePath") || HasValue(request, "dumpFile") ? "dump" : "live"
                : "default";
        }

        var discriminator = Get(request, registration.DiscriminatorArgument)
            ?? registration.DefaultDiscriminator
            ?? throw new InvocationSafetyResolutionException(
                request.Operation,
                $"Operation '{request.Operation}' requires normalized argument '{registration.DiscriminatorArgument}' for safety resolution.");
        discriminator = NormalizeDiscriminator(request.Operation, registration.DiscriminatorArgument, discriminator);

        var canonical = registration.DiscriminatorValues.FirstOrDefault(
            allowed => string.Equals(allowed, discriminator, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            throw new InvocationSafetyResolutionException(
                request.Operation,
                $"Operation '{request.Operation}' has no safety classification for {registration.DiscriminatorArgument}='{discriminator}'.");
        }

        return canonical;
    }

    private static InvocationSafetyDescriptor ResolveCollectEvents(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
    {
        var kind = NormalizeDiscriminator(
            request.Operation,
            "kind",
            Get(request, "kind") ?? DiagnosticOperationCatalog.CollectEventsKinds.Counters);
        if (kind == DiagnosticOperationCatalog.CollectEventsKinds.EventSource
            && IsTrue(request, "unsafeProvider"))
        {
            safety = Merge(safety, Profile(request.Operation, "unsafe-provider"));
        }

        var triggerPresent = HasValue(request, "triggerWhen");
        var captureKind = Get(request, "captureKind");
        if (kind == DiagnosticOperationCatalog.CollectEventsKinds.Counters
            && (triggerPresent || captureKind is not null))
        {
            captureKind = NormalizeCaptureKind(captureKind)
                ?? throw new InvocationSafetyResolutionException(
                    request.Operation,
                    "Threshold-gated collection requires a classified captureKind.");
            safety = Merge(safety, Profile(request.Operation, $"capture-{captureKind}"));
        }

        if (kind == DiagnosticOperationCatalog.CollectEventsKinds.Startup
            && HasValue(request, "launch"))
        {
            safety = Merge(safety, Profile(request.Operation, "startup-launch"));
        }
        if (HasValue(request, "savePath"))
        {
            safety = Merge(safety, Profile(request.Operation, "save-output"));
        }

        return safety;
    }

    private static InvocationSafetyDescriptor ResolveCollectSample(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
    {
        var kind = NormalizeDiscriminator(
            request.Operation,
            "kind",
            Get(request, "kind") ?? DiagnosticOperationCatalog.CollectSampleKinds.Cpu);
        if (kind == DiagnosticOperationCatalog.CollectSampleKinds.Cpu
            && IsTrue(request, "resolveMethodInstantiations"))
        {
            safety = Merge(safety, Profile(request.Operation, "resolve-method-instantiations"));
        }

        if (kind == DiagnosticOperationCatalog.CollectSampleKinds.Cpu
            && IsTrue(request, "exportTrace"))
        {
            safety = Merge(safety, Profile(request.Operation, "export-trace"));
        }

        var resolvesSymbols = kind == DiagnosticOperationCatalog.CollectSampleKinds.OffCpu
            || kind == DiagnosticOperationCatalog.CollectSampleKinds.Cpu
                && !string.Equals(Get(request, "resolveSourceLines"), "false", StringComparison.OrdinalIgnoreCase);
        if (resolvesSymbols && ContainsRemoteUrl(Get(request, "symbolPath")))
        {
            safety = Merge(safety, Profile(request.Operation, "remote-symbols"));
        }

        return safety;
    }

    private static InvocationSafetyDescriptor ResolveInspectHeap(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
    {
        var source = NormalizeDiscriminator(
            request.Operation,
            "source",
            Get(request, "source") ?? string.Empty);
        if (IsTrue(request, "includeRetentionPaths"))
        {
            safety = Merge(safety, Profile(request.Operation, "retention-paths"));
        }
        if (IsTrue(request, "includeStaticFields"))
        {
            safety = Merge(safety, Profile(request.Operation, "static-fields"));
        }
        if (IsTrue(request, "includeDelegateTargets"))
        {
            safety = Merge(safety, Profile(request.Operation, "delegate-targets"));
        }
        if (IsTrue(request, "includeDuplicateStrings"))
        {
            safety = Merge(safety, Profile(request.Operation, "duplicate-strings"));
        }
        if (source == DiagnosticOperationCatalog.HeapSources.GcDump
            && IsTrue(request, "exportTrace"))
        {
            safety = Merge(safety, Profile(request.Operation, "export-trace"));
        }
        if (source != DiagnosticOperationCatalog.HeapSources.GcDump
            && ContainsRemoteUrl(Get(request, "symbolPath")))
        {
            safety = Merge(safety, Profile(request.Operation, "remote-symbols"));
        }

        return safety;
    }

    private static InvocationSafetyDescriptor ResolveQuerySnapshot(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
    {
        var handleKind = Get(request, "handleKind");
        if (handleKind is null && HasValue(request, "handle"))
        {
            return InvocationSafetyRegistry.Get(request.Operation).MaximumSafety;
        }

        if (handleKind is not null)
        {
            var canonicalHandleKind = DiagnosticOperationCatalog.QuerySnapshotHandleKinds.All.FirstOrDefault(
                candidate => string.Equals(candidate, handleKind, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvocationSafetyResolutionException(
                    request.Operation,
                    $"query_snapshot has no safety classification for handleKind='{handleKind}'.");
            var handleSafety = Profile(request.Operation, $"handle:{canonicalHandleKind}");
            safety = safety.RiskLevel >= InvocationRiskLevel.High
                ? Merge(handleSafety, safety)
                : handleSafety;
            handleKind = canonicalHandleKind;
        }

        if (!IsTrue(request, "includeSensitiveValues"))
        {
            return safety;
        }

        return Get(request, "view")?.Trim().ToLowerInvariant() switch
        {
            "events" when handleKind is null
                || string.Equals(
                    handleKind,
                    DotnetDiagnostics.Core.UseCases.MethodParameterCaptureUseCases.HandleKind,
                    StringComparison.Ordinal) =>
                Merge(safety, Profile(request.Operation, "sensitive-parameter-values")),
            "duplicate-strings" or "object" or "frame-vars" =>
                Merge(safety, Profile(request.Operation, "sensitive-heap-values")),
            _ => safety,
        };
    }

    private static InvocationSafetyDescriptor ResolveThreadSnapshot(InvocationSafetyRequest request)
    {
        var profileId = HasValue(request, "dumpFilePath") || HasValue(request, "dumpFile")
            ? "dump"
            : "live";
        var safety = Profile(request.Operation, profileId);
        return ContainsRemoteUrl(Get(request, "symbolPath"))
            ? Merge(safety, Profile(request.Operation, "remote-symbols"))
            : safety;
    }

    private static InvocationSafetyDescriptor ResolveListOrchestrator(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
        => IsTrue(request, "includeAllSessions")
            ? Merge(safety, Profile(request.Operation, "all-sessions"))
            : safety;

    private static InvocationSafetyDescriptor ResolveDockerBootstrap(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
        => IsTrue(request, "apply")
            ? Merge(safety, Profile(request.Operation, "apply"))
            : safety;

    private static InvocationSafetyDescriptor ResolveSavedOutput(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
        => HasValue(request, "savePath")
            ? Merge(safety, Profile(request.Operation, "save-output"))
            : safety;

    private static InvocationSafetyDescriptor ResolveAttach(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
        => HasValue(request, "profileName")
            ? Profile(request.Operation, "external-profile")
            : safety;

    private static InvocationSafetyDescriptor ResolveDiscoverAzure(
        InvocationSafetyRequest request,
        InvocationSafetyDescriptor safety)
        => string.Equals(
                Get(request, "kind") ?? DiagnosticOperationCatalog.DiscoverAzureKinds.WebApps,
                DiagnosticOperationCatalog.DiscoverAzureKinds.AksClusters,
                StringComparison.OrdinalIgnoreCase)
            && IsTrue(request, "includeKubeconfig")
            ? Merge(safety, Profile(request.Operation, "kubeconfig-handle"))
            : safety;

    private static InvocationSafetyDescriptor Profile(string operation, string profileId)
        => InvocationSafetyRegistry.GetProfile(operation, profileId).Safety;

    private static InvocationSafetyDescriptor Merge(
        InvocationSafetyDescriptor left,
        InvocationSafetyDescriptor right)
        => new(
            Max(left.RiskLevel, right.RiskLevel),
            Union(left.TargetImpact, right.TargetImpact),
            Union(left.DataExposure, right.DataExposure),
            Union(left.SideEffects, right.SideEffects),
            Max(left.ApprovalPolicy, right.ApprovalPolicy),
            string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
                ? left.Reason
                : $"{left.Reason} {right.Reason}",
            Union(left.Mitigations, right.Mitigations));

    private static TEnum Max<TEnum>(TEnum left, TEnum right)
        where TEnum : struct, Enum
        => Comparer<TEnum>.Default.Compare(left, right) >= 0 ? left : right;

    private static ImmutableArray<T> Union<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        where T : notnull
        => left.Concat(right).Distinct().ToImmutableArray();

    private static string? Get(InvocationSafetyRequest request, string name)
        => request.Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool HasValue(InvocationSafetyRequest request, string name)
        => Get(request, name) is not null;

    private static bool IsTrue(InvocationSafetyRequest request, string name)
        => bool.TryParse(Get(request, name), out var value) && value;

    private static bool ContainsRemoteUrl(string? value)
        => value?.Contains("http://", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true;

    private static string NormalizeDiscriminator(string operation, string argument, string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (operation == DiagnosticOperationCatalog.CollectSample
            && argument == "kind"
            && normalized == DiagnosticOperationCatalog.CollectSampleKinds.OffCpuCliAlias)
        {
            return DiagnosticOperationCatalog.CollectSampleKinds.OffCpu;
        }

        return normalized;
    }

    private static string? NormalizeCaptureKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "cpu" or "cpusample" or "cpu-sample" => "cpu-sample",
            "heap" or "heap-snapshot" => "heap",
            "thread-snapshot" or "threadsnapshot" or "threads" => "thread-snapshot",
            "dump" => "dump",
            _ => null,
        };
}
