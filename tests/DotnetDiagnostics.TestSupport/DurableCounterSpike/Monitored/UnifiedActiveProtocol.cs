using System.Text;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class UnifiedActiveProtocol
{
    internal const string Policy = "explicit-unified-active-sampling/1";
    internal const string Path = "docs/design/durable-capture-unified-active-sampling-protocol.json";
    internal const string ProtocolSha256 = "23919c85adaf688db41a286cbe2bc90fb4687b3b3d3222d8372c276b40d03149";
    internal const string PrevalidationManifestSchema = "durable-unified-active-prevalidation-manifest/1";
    internal const string CampaignManifestSchema = "durable-unified-active-campaign-manifest/1";
    internal const string AdoptionSchema = "durable-unified-active-prevalidation-adoption/1";
    internal const string AcceptanceSchema = "durable-unified-active-prevalidation-implementation-acceptance/1";
    internal const string PrevalidationAuthorizationSchema = "durable-unified-active-prevalidation-authorization/1";
    internal const string CampaignAuthorizationSchema = "durable-unified-active-campaign-authorization/1";
    internal const string ReadinessSchema = "durable-unified-active-readiness/1";
    internal const string SummarySchema = "durable-unified-active-sweep/1";
    internal static string FieldMap { get; } = "v=3;" + ObservedUnlinkedProtocol.FieldMap["v=2;".Length..]
        + ";q=[knownFileCandidates,observedFiles,lostFilePaths,lostDirectoryBranches]"
        + ";root-loss=current-active-owned-mutable-only,positive-ENOENT-or-mapped-not-found"
        + ";branch-descendants=unknown-not-zero;populations=repeated-observations-not-census"
        + ";quiescent-controls-completed-retained=exact-no-loss-exemption";
    internal static string ContextMapSha256 { get; } = MonitoredFile.HashBytes(Encoding.UTF8.GetBytes(FieldMap));

    internal static bool RequiresExactRoots(string boundary)
        => boundary is "suite-final-enumeration" or "post-kill-quiescent-root-inventory"
            or "worker-exit-quiescent" or "harness-exit-quiescent" or "owned-cleanup-quiescent"
            or "post-monitor-abort-quiescent-root-inventory" or "before-ordinary-reopen"
            or "after-ordinary-reopen-and-first-query" or "geometry-539-rooted-32-descriptor-only";

    private static readonly string[] WorkerStreams =
        ["worker-monitor.jsonl", "worker-stdout.jsonl", "worker-stderr.log",
            "recovery-worker-monitor.jsonl", "recovery-worker-stdout.jsonl", "recovery-worker-stderr.log"];
    private static readonly string[] EntryStreams =
        ["coordinator-monitor.jsonl", "harness-stdout.log", "harness-stderr.log", "ownership.jsonl"];

    internal static string[] MutableWorkerStreams(string outputRoot)
        => WorkerStreams.Select(name => System.IO.Path.Combine(outputRoot, name)).ToArray();

    internal static string[] MutableEntryStreams(string contextRoot, string outputRoot)
        => MutableWorkerStreams(outputRoot).Concat(
            EntryStreams.Select(name => System.IO.Path.Combine(contextRoot, name))).ToArray();

    internal static MonitoredSweepSummary WorstCaseSummary()
    {
        var summary = ObservedUnlinkedProtocol.WorstCaseSummary();
        return summary with
        {
            ActiveStorageStage = true,
            SampledLoss = summary.SampledLoss! with { RootSampling = new(4_096, 2_048, 2_048, 4_096) },
        };
    }

    internal static PrevalidationMonitorReply WorstCaseAuthorityReply()
    {
        var summary = WorstCaseSummary();
        var totals = SampledLossMeasurement.Empty(unifiedActive: true);
        for (var index = 0; index < 2_048; index++)
            totals = SampledLossMeasurement.Merge(totals, summary.SampledLoss!);
        return new(false, new string('a', 64), int.MaxValue, long.MaxValue, int.MaxValue, long.MaxValue, summary)
            { SampledLoss = totals };
    }

    internal static void ValidateBinding(SampledLossBinding binding, string? repository = null)
    {
        PrevalidationProtocol.Require(binding.Policy == Policy && binding.ProtocolSha256 == ProtocolSha256
            && binding.ContextMapSha256 == ContextMapSha256
            && MonitoredFile.IsResolvedSha256(binding.RuntimeEnvironmentSha256)
            && binding.ManagedBinaries is { Count: > 0 and <= 256 }, "UnifiedActivePolicyMismatch");
        if (repository is null) return;
        foreach (var input in new[]
        {
            (Path, ProtocolSha256),
            (ObservedUnlinkedProtocol.Path, ObservedUnlinkedProtocol.ProtocolSha256),
            (SampledLossProtocol.Path, SampledLossProtocol.ProtocolSha256),
        })
            PrevalidationProtocol.Require(MonitoredFile.HashFile(
                MonitoredPathRules.ResolveRepositoryFile(repository, input.Item1)) == input.Item2,
                "UnifiedActiveProtocolChanged");
        PrevalidationProtocol.Require(binding.RuntimeEnvironmentSha256 == PrevalidationProtocol.RuntimeEnvironmentHash(),
            "UnifiedActiveEnvironmentChanged");
        foreach (var binary in binding.ManagedBinaries)
            MonitoredRunManifestValidator.ValidateBinary(binary, "UnifiedActiveManagedBinary");
    }

    internal static string CampaignBindingHash(MonitoredRunManifest manifest)
    {
        PrevalidationProtocol.Require(manifest.Schema == CampaignManifestSchema && manifest.SampledLoss is not null,
            "UnifiedActiveCampaignSchemaMismatch");
        ValidateBinding(manifest.SampledLoss!);
        MonitoredExecutionPlanner.ValidatePlan(manifest.Plan);
        return SampledLossProtocol.CampaignBindingHash(manifest);
    }

    internal static PrevalidationEvidenceValidation ValidateReadiness(MonitoredRunManifest campaign)
        => ObservedUnlinkedProtocol.ValidateReadinessCore(campaign, unifiedActive: true);
}
