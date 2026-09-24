using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record SampledLossBinding(string Policy, string ProtocolSha256, string ContextMapSha256,
    string RuntimeEnvironmentSha256, IReadOnlyList<MonitoredBinaryIdentity> ManagedBinaries,
    MonitoredBinaryIdentity? Readiness);

internal sealed record SampledLossReadiness(string Schema, MonitoredBinaryIdentity PrevalidationManifest,
    MonitoredBinaryIdentity Seal, string CampaignBindingSha256, string Reviewer, DateTimeOffset ReviewedAt);

internal sealed record SampledLossPopulation(int EnumeratedCandidates, int SuccessfullyClassified,
    int LostObservations, double? LossRate, string LostBytesAndTypes)
{
    internal static SampledLossPopulation? From(SampledLossMeasurement? measurement)
        => measurement is null ? null : new(measurement.Candidates, measurement.Classified,
            measurement.Lost, measurement.LossRate, "unknown-not-zero");
}

// Fifteen fixed counters, not one entry per descriptor. Residual candidates are hard failures,
// never silently classified successes. Counts describe observations, not distinct resources.
internal sealed record SampledLossMeasurement(int[] Counts, int[]? FirstLoss)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservedUnlinkedMeasurement? ObservedUnlinked { get; init; }
    internal const int MaximumCandidatesPerSweep = 4 * 4_096;
    internal const int MaximumCandidatesPerEntry = MaximumCandidatesPerSweep * 2_048;
    [JsonIgnore] public int Candidates => checked(Counts[0] + Counts[5] + Counts[10]);
    [JsonIgnore] public int Classified => checked(Counts[1] + Counts[6] + Counts[11]);
    [JsonIgnore] public int Lost => checked(Counts[2] + Counts[3] + Counts[4]
        + Counts[7] + Counts[8] + Counts[9] + Counts[12] + Counts[13] + Counts[14]);
    [JsonIgnore] public double? LossRate => Candidates == 0 ? null : (double)Lost / Candidates;

    internal void Validate(int maximum = MaximumCandidatesPerEntry)
    {
        PrevalidationProtocol.Require(Counts is { Length: 15 }
            && Counts.All(value => value >= 0 && value <= maximum), "SampledLossCounterInvalid");
        PrevalidationProtocol.Require((long)Counts[0] + Counts[5] + Counts[10] <= maximum,
            "SampledLossCounterOverflow");
        for (var role = 0; role < 3; role++)
        {
            var offset = role * 5;
            PrevalidationProtocol.Require((long)Counts[offset + 1] + Counts[offset + 2] + Counts[offset + 3]
                + Counts[offset + 4] <= Counts[offset], "SampledLossDenominatorMismatch");
        }
        PrevalidationProtocol.Require(Lost == 0 ? FirstLoss is null
            : FirstLoss is { Length: 4 } context && context[0] is >= 0 and <= 2 && context[3] >= 0
                && (context[1] is 1 or 3 && context[2] == 2
                    || context[1] is 2 or 4 && context[2] is -2147024894 or -2147024893
                    || context[1] == 5 && context[2] is >= 1 and <= 7),
            "SampledLossContextInvalid");
        if (FirstLoss is { } first)
        {
            PrevalidationProtocol.Require(Counts[first[0] * 5 + (first[1] is 1 or 3 ? 2 : first[1] == 5 ? 4 : 3)] > 0,
                "SampledLossContextPopulationMismatch");
        }
        ObservedUnlinked?.Validate(maximum / 4, Classified);
    }

    internal static SampledLossMeasurement Empty(bool observedUnlinked = false)
        => new(new int[15], null) { ObservedUnlinked = observedUnlinked ? new(0, 0, 0) : null };

    internal static SampledLossMeasurement Merge(SampledLossMeasurement left, SampledLossMeasurement right,
        int maximum = MaximumCandidatesPerEntry)
    {
        left.Validate(maximum);
        right.Validate(maximum);
        PrevalidationProtocol.Require((left.ObservedUnlinked is null) == (right.ObservedUnlinked is null)
            || left.Candidates == 0 && left.ObservedUnlinked is null,
            "ObservedUnlinkedMergePolicyMismatch");
        var result = new SampledLossMeasurement(
            left.Counts.Zip(right.Counts, static (a, b) => checked(a + b)).ToArray(),
            left.FirstLoss ?? right.FirstLoss)
        {
            ObservedUnlinked = right.ObservedUnlinked is { } observed
                ? ObservedUnlinkedMeasurement.Merge(left.ObservedUnlinked ?? new(0, 0, 0), observed) : null,
        };
        result.Validate(maximum);
        return result;
    }
}

internal static class SampledLossProtocol
{
    internal const string Policy = "explicit-descriptor-sampling-loss/1";
    internal const string Path = "docs/design/durable-capture-sampled-loss-protocol.json";
    internal const string ProtocolSha256 = "739b485e257b0b436eb75a9bdefc32ce6f489156a6c847105fb7a7763671f26a";
    internal const string PrevalidationManifestSchema = "durable-sampled-prevalidation-manifest/1";
    internal const string CampaignManifestSchema = "durable-sampled-campaign-manifest/1";
    internal const string SummarySchema = "durable-sampled-sweep/1";
    internal const string FieldMap =
        "v=1;n=[s,st,en,o,m,p,r,h,w,e,u,rb,i,mi,di,mdi,rc,uc,er,dc,dr,tc,tr,hc,hr,mc,ma,ci,cr,rh];"
        + "g=gap;t=[kind,boundary,alarm,firstHardError];f=[active,complete,nonAtomic,sampledAdmissible];"
        + "l=[harness:candidates,classified,pinLoss,flagsLoss,coherenceLoss;diagnostic:same;target:same];"
        + "x=firstLoss[role,operation,error,fd];z=firstHardDescriptorFailure;"
        + "operation5=coherence-proof-mask(1:identity,2:flags,4:target;1..7;not-errno);"
        + "coherence-loss=two-valid-linked-regular-snapshots,exact-owner,classified-paths,no-runtime-proof-bypass;"
        + "null-context-values=-1;loss-bytes-and-types=unknown;admissible=no-hard-error-or-alarm-and-fully-accounted-candidates";
    internal static string ContextMapSha256 { get; } = MonitoredFile.HashBytes(Encoding.UTF8.GetBytes(FieldMap));

    internal static MonitoredSweepSummary WorstCaseSummary()
        => MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            CurrentContextIdentities = int.MaxValue, CurrentContextRootedIdentities = int.MaxValue,
            RetainedHistoryBytes = long.MaxValue, FirstErrorCode = new string('e', 64),
            FirstDescriptorFailure = [2, 4, int.MinValue, int.MaxValue],
            SampledLoss = new([8_384, 2_096, 2_096, 2_096, 2_096,
                4_000, 1_000, 1_000, 1_000, 1_000,
                4_000, 1_000, 1_000, 1_000, 1_000], [2, 4, -2147024894, int.MaxValue]),
        };

    internal static void ValidateBinding(SampledLossBinding binding, string? repository = null)
    {
        PrevalidationProtocol.Require(binding.Policy == Policy && binding.ProtocolSha256 == ProtocolSha256
            && binding.ContextMapSha256 == ContextMapSha256
            && MonitoredFile.IsResolvedSha256(binding.RuntimeEnvironmentSha256)
            && binding.ManagedBinaries.Count is > 0 and <= 256, "SampledLossPolicyMismatch");
        if (repository is not null)
        {
            PrevalidationProtocol.Require(MonitoredFile.HashFile(
                MonitoredPathRules.ResolveRepositoryFile(repository, Path)) == ProtocolSha256,
                "SampledLossProtocolChanged");
            PrevalidationProtocol.Require(binding.RuntimeEnvironmentSha256 == PrevalidationProtocol.RuntimeEnvironmentHash(),
                "SampledLossEnvironmentChanged");
            foreach (var binary in binding.ManagedBinaries)
                MonitoredRunManifestValidator.ValidateBinary(binary, "SampledManagedBinary");
        }
    }

    internal static int Classify(Exception exception, MonitoredProcessIdentity? owner = null)
    {
        if (exception is not DurableStorageExperimentException { DescriptorFailure: { } failure } storage
            || storage.KnownUnlinkedDescriptor || failure.Descriptor < 0)
            return 0;
        if (storage.Code == "DescriptorObservationUnavailable"
            && failure.Operation is 1 or 3 && failure.Error == 2)
            return 2;
        if (failure.Operation is 2 or 4
            && (storage.Code == "DescriptorFlagsFileNotFoundException" && failure.Error == -2147024894
                || storage.Code == "DescriptorFlagsDirectoryNotFoundException" && failure.Error == -2147024893))
            return 3;
        if (storage.Code == "DescriptorIdentityChangedDuringObservation"
            && failure.Operation == 5 && storage.CoherenceProof is { Valid: true } proof
            && failure.Error == proof.Changes && owner is not null && owner == proof.Owner
            && owner.Role is >= MonitoredProcessRole.Harness and <= MonitoredProcessRole.Target
            && LinuxProcessIdentity.Matches(owner))
            return 4;
        return 0;
    }

    internal static bool Admissible(MonitoredSweepSummary summary)
    {
        if (summary.SampledLoss is null) return summary.Complete;
        ValidateSummary(summary);
        return IsAdmissible(summary);
    }

    private static bool IsAdmissible(MonitoredSweepSummary summary)
        => summary.ErrorCount == 0 && summary.UnclassifiedCount == 0 && summary.Alarm is null
            && summary.SampledLoss!.Candidates == summary.SampledLoss.Classified + summary.SampledLoss.Lost;

    internal static void ValidateSummary(MonitoredSweepSummary summary)
    {
        var loss = summary.SampledLoss!;
        loss.Validate(SampledLossMeasurement.MaximumCandidatesPerSweep);
        if (loss.ObservedUnlinked is { } observed)
            PrevalidationProtocol.Require(observed.Identities <= summary.IdentityCount
                && observed.NativeTemporaryBytes <= summary.WorkspaceBytes
                && observed.NativeTemporaryBytes <= summary.OpenUnlinkedBytes
                && summary.NonAtomic, "ObservedUnlinkedSummaryMismatch");
        PrevalidationProtocol.Require(summary.ObservationKind is "boundary" or "periodic"
            && MonitoredSweepSummaryEncoding.IsBoundedToken(summary.Boundary, 64)
            && (summary.Alarm is null || MonitoredSweepSummaryEncoding.IsBoundedToken(summary.Alarm, 64))
            && (summary.FirstErrorCode is null || MonitoredSweepSummaryEncoding.IsBoundedToken(summary.FirstErrorCode, 64))
            && summary.ErrorCount >= 0 && summary.UnclassifiedCount >= 0
            && double.IsFinite(summary.GapMilliseconds) && summary.GapMilliseconds >= 0,
            "SampledLossSummaryContextInvalid");
        PrevalidationProtocol.Require(summary.Sequence > 0
            && new long[] { summary.StartedTimestamp, summary.CompletedTimestamp, summary.ObservedSweepBytes,
                summary.MaximumObservedSweepBytes, summary.PackageBytes, summary.RecoveryBytes, summary.HistoryBytes,
                summary.WorkspaceBytes, summary.EvidenceBytes, summary.OpenUnlinkedBytes, summary.RuntimeOnlyExcludedBytes,
                summary.IdentityCount, summary.MaximumIdentityCount, summary.DescriptorOnlyIdentityCount,
                summary.MaximumDescriptorOnlyIdentityCount, summary.RuntimeOnlyExcludedCount,
                summary.DiagnosticCpuTicks, summary.DiagnosticPeakRssBytes, summary.TargetCpuTicks,
                summary.TargetPeakRssBytes, summary.HarnessCpuTicks, summary.HarnessPeakRssBytes,
                summary.MonitorCpuTicks, summary.MonitorAllocatedBytes, summary.CurrentContextIdentities ?? 0,
                summary.CurrentContextRootedIdentities ?? 0, summary.RetainedHistoryBytes ?? 0 }.All(static value => value >= 0),
            "SampledLossNegativeMeasurement");
        MonitoredSweepSummaryEncoding.ValidateDescriptorFailure(summary);
        PrevalidationProtocol.Require(summary.Complete ==
            (summary.ErrorCount == 0 && summary.UnclassifiedCount == 0 && loss.Lost == 0)
            && (summary.ErrorCount == 0 && summary.UnclassifiedCount == 0
                ? summary.FirstErrorCode is null && loss.Candidates == loss.Classified + loss.Lost
                : summary.FirstErrorCode is not null), "SampledLossSummaryMismatch");
    }

    internal static string CampaignBindingHash(MonitoredRunManifest manifest)
        => MonitoredFile.HashBytes(JsonSerializer.SerializeToUtf8Bytes(
            manifest with { SampledLoss = manifest.SampledLoss! with { Readiness = null } }, PrevalidationProtocol.Json));

    internal static PrevalidationEvidenceValidation ValidateReadiness(MonitoredRunManifest campaign)
    {
        var binding = campaign.SampledLoss ?? throw PrevalidationProtocol.Error(
            "SampledLossReadinessMissing", "A sampled campaign requires an explicit policy binding.");
        ValidateBinding(binding);
        var artifact = binding.Readiness ?? throw PrevalidationProtocol.Error(
            "SampledLossReadinessMissing", "A sealed all-eight prevalidation and independent review are required.");
        PrevalidationProtocol.VerifyArtifact(artifact);
        var receipt = PrevalidationProtocol.Read<SampledLossReadiness>(artifact.Path);
        PrevalidationProtocol.Require(receipt.Schema == "durable-sampled-readiness/1"
            && receipt.CampaignBindingSha256 == CampaignBindingHash(campaign)
            && !string.IsNullOrWhiteSpace(receipt.Reviewer) && receipt.ReviewedAt != default,
            "SampledLossReadinessBindingMismatch");
        PrevalidationProtocol.VerifyArtifact(receipt.PrevalidationManifest);
        PrevalidationProtocol.VerifyArtifact(receipt.Seal);
        var manifest = PrevalidationProtocol.Read<PrevalidationManifest>(receipt.PrevalidationManifest.Path);
        PrevalidationProtocol.Require(manifest.SampledLoss is not null
            && manifest.SourceCommits == campaign.SourceCommits
            && manifest.RuntimeBinary == campaign.RuntimeBinary && manifest.ToolBinary == campaign.ToolBinary
            && manifest.SampleBinary == campaign.SampleBinary
            && manifest.NativeBinaries.SequenceEqual(campaign.NativeBinaries)
            && manifest.ManagedBinaries.SequenceEqual(binding.ManagedBinaries)
            && manifest.RuntimeEnvironmentSha256 == binding.RuntimeEnvironmentSha256
            && manifest.FixtureManifest.Sha256 == campaign.FixtureManifestSha256 && manifest.Clock == campaign.Clock
            && MonitoredRunManifestValidator.StableHostFactsMatch(manifest.Host, campaign.Host)
            && MonitoredPathRules.PathsEqual(receipt.Seal.Path, System.IO.Path.Combine(manifest.PrivateRoot, "seal.json")),
            "SampledLossReadinessForeignBuildOrHost");
        var validation = PrevalidationReportValidation.Validate(receipt.PrevalidationManifest.Path);
        PrevalidationProtocol.Require(validation.Status == "coverage-observed-awaiting-independent-review"
            && validation.Outcomes == 8 && validation.SealedArtifacts > 0,
            "SampledLossReadinessCoverageMissing");
        PrevalidationProtocol.ValidateManagedInventory(manifest);
        PrevalidationProtocol.Require(Environment.ProcessPath == manifest.RuntimeBinary.Path
            && DotnetDiagnostics.TestSupport.SampleLocator.LocateSampleDll() == manifest.SampleBinary.Path,
            "SampledLossReadinessExecutingBinaryMismatch");
        return validation;
    }

}

// Only the prospective schema uses positional groups. The old serializer remains byte-for-byte
// unchanged, including its 854-byte worst-case fixture.
internal sealed class SampledSweepConverter : JsonConverter<MonitoredSweepSummary>
{
    private static JsonSerializerOptions Legacy(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        for (var index = copy.Converters.Count - 1; index >= 0; index--)
            if (copy.Converters[index] is SampledSweepConverter) copy.Converters.RemoveAt(index);
        return copy;
    }

    private sealed record Wire(int V, long[] N, double G, string?[] T, bool[] F, int[] L, int[]? X, int[]? Z)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long[]? A { get; init; }
    }

    public override MonitoredSweepSummary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        PrevalidationProtocol.Require(document.RootElement.ValueKind == JsonValueKind.Object,
            "SampledLossWireShape");
        if (!document.RootElement.TryGetProperty("v", out _))
        {
            var legacy = document.RootElement.Deserialize<MonitoredSweepSummary>(Legacy(options))!;
            PrevalidationProtocol.Require(legacy.SampledLoss is null, "SampledLossUnversionedWire");
            MonitoredSweepSummaryEncoding.ValidateDescriptorFailure(legacy);
            return legacy;
        }
        PrevalidationProtocol.RejectDuplicateMembers(document.RootElement);
        Wire? wire;
        try
        {
            wire = document.RootElement.Deserialize<Wire>(Legacy(options));
        }
        catch (JsonException)
        {
            throw PrevalidationProtocol.Error("SampledLossWireShape", "Sampled sweep groups have invalid types.");
        }
        if (wire is not { V: 1 or 2, N.Length: 30, T.Length: 4, F.Length: 4, L: not null }
            || wire.T[0] is null || wire.T[1] is null)
        {
            throw PrevalidationProtocol.Error("SampledLossWireShape", "Sampled sweep groups are missing or malformed.");
        }
        PrevalidationProtocol.Require(wire.V == 1
            ? !document.RootElement.TryGetProperty("a", out _)
            : wire.A is { Length: 3 } a && a[0] is >= 0 and <= 4_096
                && a[1] is >= 0 and <= 4_096 && a[2] >= 0, "ObservedUnlinkedWireShape");
        var n = wire.N;
        int I(int index)
        {
            PrevalidationProtocol.Require(n[index] is >= int.MinValue and <= int.MaxValue,
                "SampledLossWireScalarRange");
            return (int)n[index];
        }
        var result = new MonitoredSweepSummary(I(0), wire.T[0]!, n[1], n[2], wire.G, wire.T[1]!, wire.F[0],
            n[3], n[4], n[5], n[6], n[7], n[8], n[9], n[10], n[11],
            I(12), I(13), I(14), I(15), I(16), I(17), I(18), wire.F[1], wire.T[2], wire.F[2],
            n[19], n[20], n[21], n[22], n[23], n[24], n[25], n[26])
        {
            CurrentContextIdentities = n[27] == -1 ? null : I(27),
            CurrentContextRootedIdentities = n[28] == -1 ? null : I(28),
            RetainedHistoryBytes = n[29] == -1 ? null : n[29],
            FirstErrorCode = wire.T[3], FirstDescriptorFailure = wire.Z,
            SampledLoss = new(wire.L, wire.X)
            {
                ObservedUnlinked = wire.A is { } values ? new((int)values[0], (int)values[1], values[2]) : null,
            },
        };
        SampledLossProtocol.ValidateSummary(result);
        PrevalidationProtocol.Require(wire.F[3] == DescriptorObservationPolicy.Admissible(result),
            "SampledLossWireAdmissionMismatch");
        return result;
    }

    public override void Write(Utf8JsonWriter writer, MonitoredSweepSummary s, JsonSerializerOptions options)
    {
        if (s.SampledLoss is not { } loss)
        {
            JsonSerializer.Serialize(writer, s, Legacy(options));
            return;
        }
        SampledLossProtocol.ValidateSummary(s);
        var wire = new Wire(loss.ObservedUnlinked is null ? 1 : 2,
            [s.Sequence, s.StartedTimestamp, s.CompletedTimestamp, s.ObservedSweepBytes, s.MaximumObservedSweepBytes,
                s.PackageBytes, s.RecoveryBytes, s.HistoryBytes, s.WorkspaceBytes, s.EvidenceBytes, s.OpenUnlinkedBytes,
                s.RuntimeOnlyExcludedBytes, s.IdentityCount, s.MaximumIdentityCount, s.DescriptorOnlyIdentityCount,
                s.MaximumDescriptorOnlyIdentityCount, s.RuntimeOnlyExcludedCount, s.UnclassifiedCount, s.ErrorCount,
                s.DiagnosticCpuTicks, s.DiagnosticPeakRssBytes, s.TargetCpuTicks, s.TargetPeakRssBytes,
                s.HarnessCpuTicks, s.HarnessPeakRssBytes, s.MonitorCpuTicks, s.MonitorAllocatedBytes,
                s.CurrentContextIdentities ?? -1, s.CurrentContextRootedIdentities ?? -1, s.RetainedHistoryBytes ?? -1],
            s.GapMilliseconds, [s.ObservationKind, s.Boundary, s.Alarm, s.FirstErrorCode],
            [s.ActiveStorageStage, s.Complete, s.NonAtomic, DescriptorObservationPolicy.Admissible(s)],
            loss.Counts, loss.FirstLoss, s.FirstDescriptorFailure)
        {
            A = loss.ObservedUnlinked is { } observed
                ? [observed.Identities, observed.NativeTemporaryIdentities, observed.NativeTemporaryBytes] : null,
        };
        JsonSerializer.Serialize(writer, wire, Legacy(options));
    }
}
