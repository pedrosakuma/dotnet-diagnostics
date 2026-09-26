using DotnetDiagnostics.Core.Safety;

namespace DotnetDiagnostics.Core.Tests;

public sealed class DurableInvocationSafetyTests
{
    [Theory]
    [InlineData("collect_events", "kind", "counters")]
    [InlineData("collect_sample", "kind", "cpu")]
    [InlineData("collect_sample", "kind", "method-params")]
    [InlineData("inspect_heap", "source", "live")]
    [InlineData("collect_thread_snapshot", "dumpFilePath", "snapshot.dmp")]
    public void PersistenceAddsStorageWithoutReducingExistingRisk(string operation, string key, string value)
    {
        var baseline = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(operation, (key, value)));
        var persisted = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            operation, (key, value), ("persist", true)));
        Assert.True(persisted.RiskLevel >= baseline.RiskLevel);
        Assert.True(persisted.ApprovalPolicy >= baseline.ApprovalPolicy);
        Assert.Contains(InvocationSideEffect.WritesArtifact, persisted.SideEffects);
        Assert.All(baseline.TargetImpact, impact => Assert.Contains(impact, persisted.TargetImpact));
        Assert.All(baseline.DataExposure, exposure => Assert.Contains(exposure, persisted.DataExposure));
    }

    [Fact]
    public void DisabledPersistencePreservesExistingClassification()
    {
        var baseline = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "collect_events", ("kind", "counters")));
        var disabled = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "collect_events", ("kind", "counters"), ("persist", false)));
        Assert.Equal(baseline, disabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("list")]
    [InlineData("describe")]
    public void CaptureMetadataHasNoWriteOrTargetImpact(string? action)
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "get_bytes", ("kind", "captures"), ("captureAction", action)));
        Assert.Equal(InvocationRiskLevel.Low, safety.RiskLevel);
        Assert.Empty(safety.TargetImpact);
        Assert.Empty(safety.SideEffects);
    }

    [Theory]
    [InlineData("delete", InvocationRiskLevel.High, InvocationSideEffect.DeletesArtifact)]
    [InlineData("recover", InvocationRiskLevel.Moderate, InvocationSideEffect.WritesArtifact)]
    public void CaptureLifecycleHasExplicitSideEffects(
        string action, InvocationRiskLevel risk, InvocationSideEffect effect)
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "get_bytes", ("kind", "captures"), ("captureAction", action)));
        Assert.Equal(risk, safety.RiskLevel);
        Assert.Contains(effect, safety.SideEffects);
        Assert.Empty(safety.TargetImpact);
    }

    [Theory]
    [InlineData("records")]
    [InlineData("children")]
    public void DurableViewsReadExistingEvidenceWithoutAttach(string view)
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "query_snapshot", ("view", view), ("captureId", "historical")));
        Assert.Empty(safety.TargetImpact);
        Assert.Contains(DataExposure.PossibleConfidentialData, safety.DataExposure);
    }

    [Fact]
    public void CompositionHandleUsesItsOfflineViewProfile()
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "query_snapshot", ("view", "children"), ("handle", "opaque"), ("handleKind", "capture-group")));
        Assert.Empty(safety.TargetImpact);
        Assert.Equal(InvocationRiskLevel.Moderate, safety.RiskLevel);
    }

    [Fact]
    public void UnknownCaptureLifecycleFailsClosed()
    {
        Assert.Throws<InvocationSafetyResolutionException>(() =>
            InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
                "get_bytes", ("kind", "captures"), ("captureAction", "future-action"))));
    }

    [Theory]
    [InlineData("export", true)]
    [InlineData("export-start", true)]
    [InlineData("import", true)]
    [InlineData("import-start", true)]
    [InlineData("import-commit", true)]
    [InlineData("download-chunk", false)]
    [InlineData("upload-chunk", false)]
    [InlineData("transfer-status", false)]
    [InlineData("import-result", false)]
    [InlineData("transfer-cancel", false)]
    public void PortableActionsUseCanonicalRiskWithoutLiveTargetImpact(string action, bool initiation)
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "get_bytes", ("kind", "captures"), ("captureAction", action)));
        Assert.Equal(initiation ? InvocationRiskLevel.High : InvocationRiskLevel.Moderate, safety.RiskLevel);
        Assert.Equal(initiation ? InvocationApprovalPolicy.Acknowledge : InvocationApprovalPolicy.Warn, safety.ApprovalPolicy);
        Assert.Empty(safety.TargetImpact);
        Assert.Contains(DataExposure.PossibleConfidentialData, safety.DataExposure);
        Assert.Contains(action == "download-chunk" ? InvocationSideEffect.ExportsRawBytes : InvocationSideEffect.WritesArtifact,
            safety.SideEffects);
        if (action is "transfer-status" or "import-result" or "transfer-cancel")
            Assert.Contains(InvocationSideEffect.DeletesArtifact, safety.SideEffects);
        else
            Assert.Contains(DataExposure.PossibleSecrets, safety.DataExposure);
    }

    [Theory]
    [InlineData("cpu-efficiency-sample", "records")]
    [InlineData("requests-now", " RECORDS ")]
    public void RecordOnlySnapshotsHaveAnOfflineClassificationWithoutInventingSummarySupport(string kind, string view)
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "query_snapshot", ("handle", "opaque"), ("handleKind", kind), ("view", view)));
        Assert.Equal(InvocationRiskLevel.Moderate, safety.RiskLevel);
        Assert.Empty(safety.TargetImpact);
        Assert.Throws<InvocationSafetyResolutionException>(() =>
            InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
                "query_snapshot", ("handle", "opaque"), ("handleKind", kind), ("view", "summary"))));
    }

    [Fact]
    public void ParameterRecordsStillRequireTheirSensitiveClassification()
    {
        var safety = InvocationSafetyResolver.Resolve(InvocationSafetyRequest.Create(
            "query_snapshot", ("handle", "opaque"), ("handleKind", "method-params-capture"), ("view", "records")));
        Assert.Equal(InvocationRiskLevel.Critical, safety.RiskLevel);
        Assert.Equal(InvocationApprovalPolicy.HumanApproval, safety.ApprovalPolicy);
    }

    [Fact]
    public void PersistedBatchInheritsChildRisksAndStorageSideEffect()
    {
        var safety = InvocationSafetyResolver.Resolve(new InvocationSafetyRequest(
            "collect_batch", [KeyValuePair.Create<string, string?>("persist", "true")],
            [InvocationSafetyRequest.Create("collect_sample", ("kind", "cpu"),
                ("resolveMethodInstantiations", true))]));
        Assert.Equal(InvocationRiskLevel.High, safety.RiskLevel);
        Assert.Contains(TargetImpact.PtraceAttach, safety.TargetImpact);
        Assert.Contains(InvocationSideEffect.WritesArtifact, safety.SideEffects);
    }
}
