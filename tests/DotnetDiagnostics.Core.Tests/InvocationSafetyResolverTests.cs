using System.Text.Json;
using DotnetDiagnostics.Core.Safety;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class InvocationSafetyResolverTests
{
    [Fact]
    public void Registry_CoversEveryCanonicalOperation()
    {
        var expected = DiagnosticOperationCatalog.McpOperations
            .Concat(DiagnosticOperationCatalog.CliOnlyOperations);

        InvocationSafetyRegistry.Operations
            .Select(static registration => registration.Operation)
            .Should()
            .BeEquivalentTo(expected);
    }

    [Fact]
    public void Registry_EveryDiscriminatorValueHasResolvableProfile()
    {
        foreach (var registration in InvocationSafetyRegistry.Operations)
        {
            foreach (var discriminator in registration.DiscriminatorValues)
            {
                var request = InvocationSafetyRequest.Create(
                    registration.Operation,
                    (registration.DiscriminatorArgument!, discriminator));

                var safety = InvocationSafetyResolver.Resolve(request);

                safety.Reason.Should().NotBeNullOrWhiteSpace(
                    $"{registration.Operation} {registration.DiscriminatorArgument}={discriminator} must be classified");
            }
        }
    }

    [Fact]
    public void UnknownOperationAndDiscriminator_FailClosed()
    {
        var unknownOperation = () => InvocationSafetyResolver.Resolve(
            InvocationSafetyRequest.Create("future_tool"));
        var unknownKind = () => InvocationSafetyResolver.Resolve(
            InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectEvents,
                ("kind", "future-kind")));

        unknownOperation.Should().Throw<InvocationSafetyResolutionException>();
        unknownKind.Should().Throw<InvocationSafetyResolutionException>();
    }

    [Fact]
    public void Registry_EveryQueryHandleKindHasResolvableProfile()
    {
        foreach (var handleKind in DiagnosticOperationCatalog.QuerySnapshotHandleKinds.All)
        {
            var safety = Resolve(
                DiagnosticOperationCatalog.QuerySnapshot,
                ("view", "summary"),
                ("handleKind", handleKind));

            safety.Reason.Should().NotBeNullOrWhiteSpace(
                $"query_snapshot handle kind '{handleKind}' must be classified");
        }
    }

    [Fact]
    public void QuerySnapshotViews_AreImmutableAndMatchRegistryCoverage()
    {
        var views = DiagnosticOperationCatalog.QuerySnapshotViews.All;
        var mutableView = (IList<string>)views;
        var mutate = () => mutableView.Add("future-mutated-view");

        mutableView.IsReadOnly.Should().BeTrue();
        mutate.Should().Throw<NotSupportedException>();
        InvocationSafetyRegistry.Get(DiagnosticOperationCatalog.QuerySnapshot)
            .DiscriminatorValues.Should().Equal(views);
    }

    [Fact]
    public void UnknownOpaqueHandleKind_UsesMaximumSafetyEnvelope()
    {
        var safety = Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            ("handle", "opaque-handle"),
            ("view", "summary"));

        safety.Should().Be(InvocationSafetyRegistry.Get(
            DiagnosticOperationCatalog.QuerySnapshot).MaximumSafety);
        safety.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        safety.DataExposure.Should().Contain(DataExposure.ParameterValues);
    }

    [Fact]
    public void MaximumSafety_MergesEveryConditionalImpactAndSideEffect()
    {
        var maximum = InvocationSafetyRegistry.Get(
            DiagnosticOperationCatalog.CollectSample).MaximumSafety;

        maximum.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        maximum.TargetImpact.Should().Contain(TargetImpact.ProfilerAttach);
        maximum.DataExposure.Should().Contain(DataExposure.RawTrace);
        maximum.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        maximum.SideEffects.Should().Contain(InvocationSideEffect.ContactsRemoteSymbolServer);
    }

    [Fact]
    public void StableModel_SerializesContractNamesAndEnumTokens()
    {
        var safety = InvocationSafetyResolver.Resolve(
            InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectSample,
                ("kind", DiagnosticOperationCatalog.CollectSampleKinds.MethodParameters)));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(safety));
        var root = json.RootElement;

        root.GetProperty("riskLevel").GetString().Should().Be("critical");
        root.GetProperty("approvalPolicy").GetString().Should().Be("human-approval");
        root.GetProperty("targetImpact").EnumerateArray().Select(static item => item.GetString())
            .Should().Contain(["profiler-attach", "rejit"]);
        root.GetProperty("dataExposure").EnumerateArray().Select(static item => item.GetString())
            .Should().Contain(["parameter-values", "possible-pii", "possible-secrets", "possible-confidential-data"]);
        root.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("mitigations").GetArrayLength().Should().BeGreaterThan(0);
        root.TryGetProperty("authorization", out _).Should().BeFalse(
            "authorization scopes are intentionally separate from invocation safety");
    }

    [Theory]
    [InlineData("logs", DataExposure.LogMessages)]
    [InlineData("exceptions", DataExposure.ExceptionMessages)]
    [InlineData("db", DataExposure.DatabaseStatements)]
    [InlineData("activities", DataExposure.ActivityData)]
    [InlineData("event_source", DataExposure.EventSourcePayloads)]
    [InlineData("networking", DataExposure.NetworkData)]
    [InlineData("requests", DataExposure.RequestData)]
    public void EventPayloadKinds_ExplicitlyClassifySensitiveTargetData(
        string kind,
        DataExposure specificExposure)
    {
        var safety = InvocationSafetyResolver.Resolve(
            InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectEvents,
                ("kind", kind)));

        safety.RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        safety.DataExposure.Should().Contain(specificExposure);
        safety.DataExposure.Should().Contain(DataExposure.PossiblePii);
        safety.DataExposure.Should().Contain(DataExposure.PossibleConfidentialData);
        safety.Mitigations.Should().Contain(
            mitigation => mitigation.Contains("redaction", StringComparison.OrdinalIgnoreCase)
                && mitigation.Contains("defense in depth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StackAndTypeCollectors_ExplicitlyClassifyApplicationNames()
    {
        var cpu = InvocationSafetyResolver.Resolve(
            InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectSample,
                ("kind", "cpu")));
        var allocation = InvocationSafetyResolver.Resolve(
            InvocationSafetyRequest.Create(
                DiagnosticOperationCatalog.CollectSample,
                ("kind", "allocation")));

        foreach (var safety in new[] { cpu, allocation })
        {
            safety.DataExposure.Should().Contain(DataExposure.StackNames);
            safety.DataExposure.Should().Contain(DataExposure.TypeNames);
            safety.DataExposure.Should().Contain(DataExposure.MethodNames);
            safety.DataExposure.Should().Contain(DataExposure.PossibleConfidentialData);
        }
    }

    [Fact]
    public void EveryStackTypeOrMethodProfile_ClassifiesPossiblePiiAndConfidentialData()
    {
        var incomplete = InvocationSafetyRegistry.Operations
            .SelectMany(registration => registration.Profiles.Select(
                profile => (registration.Operation, Profile: profile)))
            .Where(static entry =>
                entry.Profile.Safety.DataExposure.Contains(DataExposure.StackNames)
                || entry.Profile.Safety.DataExposure.Contains(DataExposure.TypeNames)
                || entry.Profile.Safety.DataExposure.Contains(DataExposure.MethodNames))
            .Where(static entry =>
                !entry.Profile.Safety.DataExposure.Contains(DataExposure.PossiblePii)
                || !entry.Profile.Safety.DataExposure.Contains(DataExposure.PossibleConfidentialData))
            .Select(static entry => $"{entry.Operation}:{entry.Profile.Id}")
            .ToArray();

        incomplete.Should().BeEmpty(
            "target-controlled stack, type, and method names may contain PII or confidential identifiers. Incomplete: {0}",
            string.Join(", ", incomplete));
    }

    [Fact]
    public void SensitiveModifiers_EscalateConcreteInvocation()
    {
        var counters = Resolve(DiagnosticOperationCatalog.CollectEvents, ("kind", "counters"));
        var gatedDump = Resolve(
            DiagnosticOperationCatalog.CollectEvents,
            ("kind", "counters"),
            ("triggerWhen", "cpu>85"),
            ("captureKind", "dump"));
        var cpu = Resolve(DiagnosticOperationCatalog.CollectSample, ("kind", "cpu"));
        var enrichedCpu = Resolve(
            DiagnosticOperationCatalog.CollectSample,
            ("kind", "cpu"),
            ("resolveMethodInstantiations", true),
            ("exportTrace", true),
            ("symbolPath", "srv*https://symbols.example.test"));
        var sensitiveQuery = Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            ("view", "object"),
            ("includeSensitiveValues", true));

        counters.RiskLevel.Should().Be(InvocationRiskLevel.Low);
        gatedDump.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        gatedDump.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.HumanApproval);
        cpu.RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        enrichedCpu.RiskLevel.Should().Be(InvocationRiskLevel.High);
        enrichedCpu.TargetImpact.Should().Contain(TargetImpact.PtraceAttach);
        enrichedCpu.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        enrichedCpu.SideEffects.Should().Contain(InvocationSideEffect.ContactsRemoteSymbolServer);
        sensitiveQuery.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        sensitiveQuery.DataExposure.Should().Contain(DataExposure.HeapValues);
    }

    [Fact]
    public void SourceAndAttachModes_ResolvePerInvocation()
    {
        Resolve(DiagnosticOperationCatalog.InspectHeap, ("source", "dump"))
            .RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        Resolve(DiagnosticOperationCatalog.InspectHeap, ("source", "live"))
            .TargetImpact.Should().Contain(TargetImpact.ProcessSuspension);
        Resolve(DiagnosticOperationCatalog.InspectHeap, ("source", "gcdump"))
            .TargetImpact.Should().Contain(TargetImpact.InducedGc);
        Resolve(DiagnosticOperationCatalog.CollectThreadSnapshot, ("dumpFilePath", "capture.dmp"))
            .RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        Resolve(DiagnosticOperationCatalog.CollectThreadSnapshot)
            .RiskLevel.Should().Be(InvocationRiskLevel.High);
        Resolve(DiagnosticOperationCatalog.AttachToPod, ("profileName", "external-sidecar"))
            .RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        Resolve(DiagnosticOperationCatalog.AttachToPod, ("podName", "api-0"))
            .SideEffects.Should().Contain(InvocationSideEffect.InjectsEphemeralContainer);
    }

    [Fact]
    public void KindSpecificModifiers_AreIgnoredWhenTheOperationDoesNotUseThem()
    {
        var allocation = Resolve(
            DiagnosticOperationCatalog.CollectSample,
            ("kind", "allocation"),
            ("resolveMethodInstantiations", true),
            ("exportTrace", true),
            ("symbolPath", "srv*https://symbols.example.test"));
        var logs = Resolve(
            DiagnosticOperationCatalog.CollectEvents,
            ("kind", "logs"),
            ("unsafeProvider", true),
            ("captureKind", "dump"));
        var liveHeap = Resolve(
            DiagnosticOperationCatalog.InspectHeap,
            ("source", "live"),
            ("exportTrace", true));

        allocation.RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        allocation.TargetImpact.Should().NotContain(TargetImpact.PtraceAttach);
        allocation.SideEffects.Should().BeEmpty();
        logs.RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        liveHeap.SideEffects.Should().NotContain(InvocationSideEffect.WritesArtifact);
    }

    [Fact]
    public void Batch_InheritsHighestChildRisk()
    {
        var request = new InvocationSafetyRequest(
            DiagnosticOperationCatalog.CollectBatch,
            children:
            [
                InvocationSafetyRequest.Create(
                    DiagnosticOperationCatalog.CollectEvents,
                    ("kind", "counters")),
                InvocationSafetyRequest.Create(
                    DiagnosticOperationCatalog.CollectSample,
                    ("kind", "method-params")),
            ]);

        var safety = InvocationSafetyResolver.Resolve(request);

        safety.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        safety.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.HumanApproval);
        safety.DataExposure.Should().Contain(DataExposure.ParameterValues);
    }

    [Fact]
    public void RedactionMitigations_NeverClaimGuaranteedRemoval()
    {
        var mitigations = InvocationSafetyRegistry.Operations
            .SelectMany(static registration => registration.Profiles)
            .SelectMany(static profile => profile.Safety.Mitigations)
            .Where(mitigation => mitigation.Contains("redaction", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        mitigations.Should().NotBeEmpty();
        mitigations.Should().OnlyContain(
            mitigation => mitigation.Contains("defense in depth", StringComparison.OrdinalIgnoreCase)
                || mitigation.Contains("never as a guarantee", StringComparison.OrdinalIgnoreCase));
    }

    private static InvocationSafetyDescriptor Resolve(
        string operation,
        params (string Name, object? Value)[] arguments)
        => InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(operation, arguments));
}
