using System.Text;
using System.Diagnostics;
using System.Reflection;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class ObservedUnlinkedProtocol
{
    internal const string Policy = "explicit-observed-unlinked-accounting/1";
    internal const string Path = "docs/design/durable-capture-observed-unlinked-protocol.json";
    internal const string ProtocolSha256 = "72e057c9f7f7125c6d176d5ae2ea2c62767f76b6d459917ddb3538f6d96f349f";
    internal const string PrevalidationManifestSchema = "durable-observed-unlinked-prevalidation-manifest/1";
    internal const string CampaignManifestSchema = "durable-observed-unlinked-campaign-manifest/1";
    internal const string AdoptionSchema = "durable-observed-unlinked-prevalidation-adoption/1";
    internal const string AcceptanceSchema = "durable-observed-unlinked-prevalidation-implementation-acceptance/1";
    internal const string PrevalidationAuthorizationSchema = "durable-observed-unlinked-prevalidation-authorization/1";
    internal const string CampaignAuthorizationSchema = "durable-observed-unlinked-campaign-authorization/1";
    internal const string ReadinessSchema = "durable-observed-unlinked-readiness/1";
    internal const string SummarySchema = "durable-observed-unlinked-sweep/1";
    internal static string FieldMap { get; } = "v=2;" + SampledLossProtocol.FieldMap["v=1;".Length..]
        + ";a=[classifiedUnlinkedIdentities,nativeTemporaryIdentities,nativeTemporaryBytes]"
        + ";unlinked=two-coherent-zero-link-regular-snapshots,exact-live-owner,max-observed-length"
        + ";native-temporary=unknown-native-path,writable-diagnostic,conservative-context-attribution"
        + ";native-temporary-bytes=workspace-partition-and-shared-package-recovery-budget";
    internal static string ContextMapSha256 { get; } = MonitoredFile.HashBytes(Encoding.UTF8.GetBytes(FieldMap));

    internal static MonitoredSweepSummary WorstCaseSummary()
    {
        var existing = SampledLossProtocol.WorstCaseSummary();
        return existing with
        {
            NonAtomic = true,
            SampledLoss = existing.SampledLoss! with
            {
                ObservedUnlinked = new(4_096, 1_000, long.MaxValue),
            },
        };
    }

    internal static void ValidateBinding(SampledLossBinding binding, string? repository = null)
    {
        PrevalidationProtocol.Require(binding.Policy == Policy && binding.ProtocolSha256 == ProtocolSha256
            && binding.ContextMapSha256 == ContextMapSha256
            && MonitoredFile.IsResolvedSha256(binding.RuntimeEnvironmentSha256)
            && binding.ManagedBinaries.Count is > 0 and <= 256, "ObservedUnlinkedPolicyMismatch");
        if (repository is null) return;
        PrevalidationProtocol.Require(MonitoredFile.HashFile(
            MonitoredPathRules.ResolveRepositoryFile(repository, Path)) == ProtocolSha256,
            "ObservedUnlinkedProtocolChanged");
        PrevalidationProtocol.Require(MonitoredFile.HashFile(
            MonitoredPathRules.ResolveRepositoryFile(repository, SampledLossProtocol.Path))
            == SampledLossProtocol.ProtocolSha256, "ObservedUnlinkedInheritedProtocolChanged");
        PrevalidationProtocol.Require(binding.RuntimeEnvironmentSha256 == PrevalidationProtocol.RuntimeEnvironmentHash(),
            "ObservedUnlinkedEnvironmentChanged");
        foreach (var binary in binding.ManagedBinaries)
            MonitoredRunManifestValidator.ValidateBinary(binary, "ObservedUnlinkedManagedBinary");
    }

    internal static string CampaignBindingHash(MonitoredRunManifest manifest)
    {
        PrevalidationProtocol.Require(manifest.Schema == CampaignManifestSchema && manifest.SampledLoss is not null,
            "ObservedUnlinkedCampaignSchemaMismatch");
        ValidateBinding(manifest.SampledLoss!);
        MonitoredExecutionPlanner.ValidatePlan(manifest.Plan);
        return SampledLossProtocol.CampaignBindingHash(manifest);
    }

    internal static PrevalidationEvidenceValidation ValidateReadiness(MonitoredRunManifest campaign)
    {
        var binding = campaign.SampledLoss ?? throw PrevalidationProtocol.Error(
            "ObservedUnlinkedReadinessMissing", "A prospective observed-unlinked campaign requires a policy binding.");
        var campaignHash = CampaignBindingHash(campaign);
        PrevalidationProtocol.Require(binding.RuntimeEnvironmentSha256 == PrevalidationProtocol.RuntimeEnvironmentHash(),
            "ObservedUnlinkedEnvironmentChanged");
        var artifact = binding.Readiness ?? throw PrevalidationProtocol.Error(
            "ObservedUnlinkedReadinessMissing", "Fresh sealed all-eight coverage and independent review are required.");
        PrevalidationProtocol.VerifyArtifact(artifact);
        var receipt = PrevalidationProtocol.Read<SampledLossReadiness>(artifact.Path);
        PrevalidationProtocol.Require(receipt.Schema == ReadinessSchema
            && receipt.CampaignBindingSha256 == campaignHash
            && !string.IsNullOrWhiteSpace(receipt.Reviewer) && receipt.ReviewedAt != default,
            "ObservedUnlinkedReadinessBindingMismatch");
        PrevalidationProtocol.VerifyArtifact(receipt.PrevalidationManifest);
        PrevalidationProtocol.VerifyArtifact(receipt.Seal);
        var manifest = PrevalidationProtocol.Read<PrevalidationManifest>(receipt.PrevalidationManifest.Path);
        PrevalidationProtocol.ValidateShape(manifest);
        PrevalidationProtocol.Require(manifest.SampledLoss is { Policy: Policy } accepted
            && accepted.ProtocolSha256 == binding.ProtocolSha256
            && accepted.ContextMapSha256 == binding.ContextMapSha256
            && manifest.SourceCommits == campaign.SourceCommits
            && manifest.RuntimeBinary == campaign.RuntimeBinary && manifest.ToolBinary == campaign.ToolBinary
            && manifest.SampleBinary == campaign.SampleBinary
            && manifest.NativeBinaries.SequenceEqual(campaign.NativeBinaries)
            && manifest.ManagedBinaries.SequenceEqual(binding.ManagedBinaries)
            && manifest.RuntimeEnvironmentSha256 == binding.RuntimeEnvironmentSha256
            && manifest.FixtureManifest.Sha256 == campaign.FixtureManifestSha256 && manifest.Clock == campaign.Clock
            && MonitoredRunManifestValidator.StableHostFactsMatch(manifest.Host, campaign.Host)
            && MonitoredPathRules.PathsEqual(receipt.Seal.Path, System.IO.Path.Combine(manifest.PrivateRoot, "seal.json")),
            "ObservedUnlinkedReadinessForeignBuildOrHost");
        MonitoredRunManifestValidator.ValidateCommits(manifest.SourceCommits);
        ValidateCommittedBuild(manifest.SourceCommits);
        MonitoredRunManifestValidator.ValidateClock(manifest.Clock);
        MonitoredRunManifestValidator.ValidateCurrentHost(campaign.Host, campaign.EvidenceRoot);
        foreach (var binary in PrevalidationProtocol.Binaries(manifest))
            MonitoredRunManifestValidator.ValidateBinary(binary, "ObservedUnlinkedReadinessBinary");
        PrevalidationProtocol.VerifyArtifact(manifest.FixtureManifest);
        PrevalidationProtocol.VerifyArtifact(manifest.Adoption);
        PrevalidationProtocol.VerifyArtifact(manifest.ImplementationAcceptance);
        PrevalidationProtocol.ValidateAdoption(manifest,
            PrevalidationProtocol.Read<PrevalidationAdoption>(manifest.Adoption.Path));
        PrevalidationProtocol.ValidateAcceptance(manifest,
            PrevalidationProtocol.Read<PrevalidationAcceptance>(manifest.ImplementationAcceptance.Path));
        var validation = PrevalidationReportValidation.Validate(receipt.PrevalidationManifest.Path);
        PrevalidationProtocol.Require(validation.Status == "coverage-observed-awaiting-independent-review"
            && validation.Outcomes == 8 && validation.SealedArtifacts > 0 && !validation.CampaignAdmissionGranted,
            "ObservedUnlinkedReadinessCoverageMissing");
        PrevalidationProtocol.ValidateManagedInventory(manifest);
        PrevalidationProtocol.Require(Environment.ProcessPath == manifest.RuntimeBinary.Path
            && DotnetDiagnostics.TestSupport.SampleLocator.LocateSampleDll() == manifest.SampleBinary.Path,
            "ObservedUnlinkedReadinessExecutingBinaryMismatch");
        return validation;
    }

    internal static void ValidateCommittedBuild(MonitoredSourceCommits commits)
    {
        MonitoredRunManifestValidator.ValidateCommits(commits);
        var directory = System.IO.Path.GetDirectoryName(typeof(ObservedUnlinkedProtocol).Assembly.Location)!;
        var head = Git(directory, "rev-parse", "--verify", "HEAD").Trim();
        var status = Git(directory, "status", "--porcelain", "--untracked-files=all");
        ValidateBuildIdentity(commits, head, status,
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            typeof(ObservedUnlinkedProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        foreach (var commit in new[] { commits.ProtocolCommit, commits.PipelineCommit, commits.AdapterCommit }.Distinct())
            Git(directory, "merge-base", "--is-ancestor", commit, head);
    }

    internal static void ValidateBuildIdentity(MonitoredSourceCommits commits, string head, string status,
        string? toolVersion, string? supportVersion)
    {
        PrevalidationProtocol.Require(status.Length == 0, "ObservedUnlinkedSourceNotCommitted");
        PrevalidationProtocol.Require(commits.RunnerCommit == head && commits.MonitorCommit == head
            && toolVersion is not null && toolVersion.EndsWith("+" + head, StringComparison.Ordinal)
            && supportVersion is not null && supportVersion.EndsWith("+" + head, StringComparison.Ordinal),
            "ObservedUnlinkedBuildRevisionMismatch");
    }

    private static string Git(string directory, params string[] arguments)
        => GitAsync(directory, arguments).GetAwaiter().GetResult();

    private static async Task<string> GitAsync(string directory, string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw PrevalidationProtocol.Error("ObservedUnlinkedSourceProofUnavailable",
                "Git is required to verify the committed source underlying this build.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            PrevalidationProtocol.Require(process.ExitCode == 0, "ObservedUnlinkedSourceProofUnavailable");
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw PrevalidationProtocol.Error("ObservedUnlinkedSourceProofTimeout",
                "Committed-source verification exceeded its five-second bound.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            timeout.Cancel();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4_097];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        PrevalidationProtocol.Require(count <= 4_096, "ObservedUnlinkedSourceProofOutputLimit");
        return new string(buffer, 0, count);
    }
}

// Dispatch is explicit: the frozen v5 validator and readiness route remain v5-only.
internal static class DescriptorObservationPolicy
{
    internal static bool IsObservedUnlinked(SampledLossBinding? binding)
        => binding?.Policy == ObservedUnlinkedProtocol.Policy;

    internal static string Select(SampledLossBinding? binding, string strict, string sampled, string observed)
        => binding is null ? strict : IsObservedUnlinked(binding) ? observed : sampled;

    internal static void ValidateBinding(SampledLossBinding binding, string? repository = null)
    {
        if (IsObservedUnlinked(binding)) ObservedUnlinkedProtocol.ValidateBinding(binding, repository);
        else SampledLossProtocol.ValidateBinding(binding, repository);
    }

    internal static PrevalidationEvidenceValidation ValidateReadiness(MonitoredRunManifest campaign)
        => IsObservedUnlinked(campaign.SampledLoss) ? ObservedUnlinkedProtocol.ValidateReadiness(campaign)
            : SampledLossProtocol.ValidateReadiness(campaign);

    internal static void ValidateMeasurement(SampledLossBinding? binding, SampledLossMeasurement? measurement,
        bool required = true)
    {
        if (measurement is null)
        {
            PrevalidationProtocol.Require(!required || binding is null, "ObservationPolicyMeasurementMissing");
            return;
        }
        PrevalidationProtocol.Require(binding is not null
            && (measurement.ObservedUnlinked is not null) == IsObservedUnlinked(binding),
            "ObservationPolicyMeasurementMismatch");
        measurement.Validate();
    }

    internal static void ValidateSummary(SampledLossBinding? binding, MonitoredSweepSummary summary)
        => ValidateMeasurement(binding, summary.SampledLoss);

    internal static bool Admissible(MonitoredSweepSummary summary)
        => SampledLossProtocol.Admissible(summary)
            && (summary.SampledLoss?.ObservedUnlinked is null || SharedPackageBudgetFits(summary));

    internal static bool SharedPackageBudgetFits(MonitoredSweepSummary summary)
        => summary.PackageBytes is >= 0 and <= 268_435_456
            && summary.RecoveryBytes >= 0 && summary.RecoveryBytes <= 268_435_456 - summary.PackageBytes
            && summary.NativeTemporaryBytes >= 0
            && summary.NativeTemporaryBytes <= 268_435_456 - summary.PackageBytes - summary.RecoveryBytes;
}
