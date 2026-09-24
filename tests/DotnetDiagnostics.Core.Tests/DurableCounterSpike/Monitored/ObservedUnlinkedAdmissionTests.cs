using System.Text;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Fact]
    public void ObservedUnlinkedAdmissionKeepsFrozenSampledBindingClosed()
    {
        var binding = ObservedAdmissionBinding();
        DescriptorObservationPolicy.ValidateBinding(binding);
        Action oldPolicy = () => SampledLossProtocol.ValidateBinding(binding);
        oldPolicy.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossPolicyMismatch");
        Action relabeled = () => DescriptorObservationPolicy.ValidateBinding(binding with
            { Policy = SampledLossProtocol.Policy });
        relabeled.Should().Throw<DurableStorageExperimentException>();
        Action oldMap = () => DescriptorObservationPolicy.ValidateBinding(binding with
            { ContextMapSha256 = SampledLossProtocol.ContextMapSha256 });
        oldMap.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("ObservedUnlinkedPolicyMismatch");
        Action oldHash = () => DescriptorObservationPolicy.ValidateBinding(binding with
            { ProtocolSha256 = SampledLossProtocol.ProtocolSha256 });
        oldHash.Should().Throw<DurableStorageExperimentException>();
        Action unknown = () => DescriptorObservationPolicy.ValidateBinding(binding with { Policy = "unrecognized/1" });
        unknown.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void ObservedUnlinkedMapAddsOnlyVersionAndExplicitAccountingGroups()
    {
        var expected = "v=2;" + SampledLossProtocol.FieldMap["v=1;".Length..]
            + ";a=[classifiedUnlinkedIdentities,nativeTemporaryIdentities,nativeTemporaryBytes]"
            + ";unlinked=two-coherent-zero-link-regular-snapshots,exact-live-owner,max-observed-length"
            + ";native-temporary=unknown-native-path,writable-diagnostic,conservative-context-attribution"
            + ";native-temporary-bytes=workspace-partition-and-shared-package-recovery-budget";
        ObservedUnlinkedProtocol.FieldMap.Should().Be(expected);
        ObservedUnlinkedProtocol.ContextMapSha256.Should().Be(MonitoredFile.HashBytes(Encoding.UTF8.GetBytes(expected)))
            .And.NotBe(SampledLossProtocol.ContextMapSha256);
    }

    [Fact]
    public void ObservedUnlinkedMeasurementMustBePresentEvenWhenZeroAndNeverUnderV5()
    {
        var binding = ObservedAdmissionBinding();
        var zero = SampledLossMeasurement.Empty() with { ObservedUnlinked = new(0, 0, 0) };
        DescriptorObservationPolicy.ValidateMeasurement(binding, zero);
        Action missing = () => DescriptorObservationPolicy.ValidateMeasurement(binding, SampledLossMeasurement.Empty());
        missing.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("ObservationPolicyMeasurementMismatch");
        Action nullMeasurement = () => DescriptorObservationPolicy.ValidateMeasurement(binding, null);
        nullMeasurement.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("ObservationPolicyMeasurementMissing");
        DescriptorObservationPolicy.ValidateMeasurement(binding, null, required: false);
        var v5 = binding with { Policy = SampledLossProtocol.Policy,
            ProtocolSha256 = SampledLossProtocol.ProtocolSha256, ContextMapSha256 = SampledLossProtocol.ContextMapSha256 };
        Action old = () => DescriptorObservationPolicy.ValidateMeasurement(v5, zero);
        old.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("ObservationPolicyMeasurementMismatch");
        DescriptorObservationPolicy.ValidateMeasurement(v5, SampledLossMeasurement.Empty());
        PrevalidationReportValidation.MeasurementsEqual(zero, SampledLossMeasurement.Empty()).Should().BeFalse();
    }

    [Theory]
    [InlineData(100, 168_435_355, 100_000_001, true)]
    [InlineData(100, 168_435_355, 100_000_002, false)]
    [InlineData(268_435_456, 0, 1, false)]
    [InlineData(0, 268_435_456, 1, false)]
    [InlineData(0, 0, long.MaxValue, false)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, false)]
    public void ObservedUnlinkedNativeTemporaryBytesShareOneOverflowSafePackageBudget(
        long package, long recovery, long nativeTemporary, bool fits)
    {
        var summary = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            PackageBytes = package, RecoveryBytes = recovery,
            SampledLoss = SampledLossMeasurement.Empty() with
                { ObservedUnlinked = new(1, 1, nativeTemporary) },
        };
        DescriptorObservationPolicy.SharedPackageBudgetFits(summary).Should().Be(fits);
    }

    [Fact]
    public void ObservedUnlinkedPrevalidationRequiresNewSchemaMapAndMatchingInventory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var strict = PrevalidationContractFixture();
        var binding = ObservedAdmissionBinding() with
        {
            ManagedBinaries = strict.ManagedBinaries, RuntimeEnvironmentSha256 = strict.RuntimeEnvironmentSha256,
        };
        var manifest = strict with
        {
            Schema = ObservedUnlinkedProtocol.PrevalidationManifestSchema, SampledLoss = binding,
            ContextSummaryFieldMapSha256 = ObservedUnlinkedProtocol.ContextMapSha256,
        };
        PrevalidationProtocol.ValidateShape(manifest);
        Action oldSchema = () => PrevalidationProtocol.ValidateShape(manifest with
            { Schema = SampledLossProtocol.PrevalidationManifestSchema }, allowLegacyInspection: true);
        oldSchema.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("PrevalidationSchemaMismatch");
        Action oldMap = () => PrevalidationProtocol.ValidateShape(manifest with
            { ContextSummaryFieldMapSha256 = SampledLossProtocol.ContextMapSha256 }, allowLegacyInspection: true);
        oldMap.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("PrevalidationContextEncodingMismatch");
        Action oldBinding = () => PrevalidationProtocol.ValidateShape(manifest with
            { SampledLoss = SampledBinding(strict) });
        oldBinding.Should().Throw<DurableStorageExperimentException>();
    }

    private static SampledLossBinding ObservedAdmissionBinding()
        => new(ObservedUnlinkedProtocol.Policy, ObservedUnlinkedProtocol.ProtocolSha256,
            ObservedUnlinkedProtocol.ContextMapSha256, MonitoredFile.HashBytes("component-environment"u8.ToArray()),
            [new("/resolved-by-future-admission/tool.dll", new string('b', 64))], null);

    [Fact]
    public void ObservedUnlinkedReceiptsCannotReuseStrictOrSampledSchemas()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = ObservedAdmissionManifest();
        var adoption = new PrevalidationAdoption(ObservedUnlinkedProtocol.AdoptionSchema,
            ObservedUnlinkedProtocol.ProtocolSha256, "component-only", DateTimeOffset.UtcNow, PrevalidationProtocol.Scope);
        PrevalidationProtocol.ValidateAdoption(manifest, adoption);
        var acceptance = new PrevalidationAcceptance(ObservedUnlinkedProtocol.AcceptanceSchema,
            PrevalidationProtocol.Scope, PrevalidationProtocol.AddendumSha256, "component-only",
            manifest.SourceCommits, PrevalidationProtocol.BinaryInventoryHash(manifest),
            manifest.ComponentEvidence.Sha256, manifest.Attribution.Sha256, manifest.Encoding.Sha256,
            PrevalidationLayout.DeriveSuiteIdentityBound(), true, true, true, true)
        {
            SampledProtocolSha256 = ObservedUnlinkedProtocol.ProtocolSha256,
        };
        PrevalidationProtocol.ValidateAcceptance(manifest, acceptance);
        var hash = HashText("component-only-not-authorization");
        var authorization = new PrevalidationAuthorization(ObservedUnlinkedProtocol.PrevalidationAuthorizationSchema,
            PrevalidationProtocol.Scope, manifest.SuiteId, hash, PrevalidationProtocol.AddendumSha256,
            manifest.ImplementationAcceptance.Sha256, manifest.HistoricalReport.Sha256, "component-only",
            DateTimeOffset.UtcNow, 8, 1, true);
        PrevalidationProtocol.ValidateAuthorization(manifest, hash, authorization);
        foreach (var prefix in new[] { "durable-prevalidation", "durable-sampled-prevalidation" })
        {
            Action oldAdoption = () => PrevalidationProtocol.ValidateAdoption(manifest,
                adoption with { Schema = prefix + "-adoption/1" });
            oldAdoption.Should().Throw<DurableStorageExperimentException>();
            Action oldAcceptance = () => PrevalidationProtocol.ValidateAcceptance(manifest,
                acceptance with { Schema = prefix + "-implementation-acceptance/1" });
            oldAcceptance.Should().Throw<DurableStorageExperimentException>();
            Action oldAuthorization = () => PrevalidationProtocol.ValidateAuthorization(manifest, hash,
                authorization with { Schema = prefix + "-authorization/1" });
            oldAuthorization.Should().Throw<DurableStorageExperimentException>();
        }
        Action oldProtocol = () => PrevalidationProtocol.ValidateAcceptance(manifest,
            acceptance with { SampledProtocolSha256 = SampledLossProtocol.ProtocolSha256 });
        oldProtocol.Should().Throw<DurableStorageExperimentException>();
        Action otherManifest = () => PrevalidationProtocol.ValidateAuthorization(manifest, HashText("different"), authorization);
        otherManifest.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("schema")]
    [InlineData("binding")]
    [InlineData("build")]
    [InlineData("environment")]
    [InlineData("fixture")]
    [InlineData("plan")]
    [InlineData("unsealed")]
    public void ObservedUnlinkedReadinessNeverUpgradesPartialForeignOrUnsealedArtifacts(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        var pv = ObservedAdmissionManifest();
        var campaign = PrepareComponentExecution().Manifest with
        {
            Schema = ObservedUnlinkedProtocol.CampaignManifestSchema, SampledLoss = pv.SampledLoss,
            SourceCommits = pv.SourceCommits, RuntimeBinary = pv.RuntimeBinary, ToolBinary = pv.ToolBinary,
            SampleBinary = pv.SampleBinary, NativeBinaries = pv.NativeBinaries, Host = pv.Host,
            FixtureManifestSha256 = pv.FixtureManifest.Sha256, Clock = pv.Clock,
        };
        if (mutation == "missing")
        {
            Action missing = () => ObservedUnlinkedProtocol.ValidateReadiness(campaign);
            missing.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("ObservedUnlinkedReadinessMissing");
            return;
        }
        if (mutation == "build") campaign = campaign with { ToolBinary = campaign.ToolBinary with { Sha256 = HashText("foreign-build") } };
        if (mutation == "environment") campaign = campaign with { SampledLoss = campaign.SampledLoss! with
            { RuntimeEnvironmentSha256 = HashText("foreign-environment") } };
        if (mutation == "fixture") campaign = campaign with { FixtureManifestSha256 = HashText("foreign-fixture") };
        Directory.CreateDirectory(pv.PrivateRoot);
        var manifestPath = Path.Combine(_workspace, "observed-component-manifest.json");
        PrevalidationExecutor.WriteImmutable(manifestPath, pv);
        var sealPath = Path.Combine(pv.PrivateRoot, "seal.json");
        PrevalidationExecutor.WriteImmutable(sealPath, new PrevalidationSeal(
            "durable-prevalidation-seal/1", pv.SuiteId, HashFile(manifestPath), HashText("no-report"), []));
        var receipt = new SampledLossReadiness(
            mutation == "schema" ? "durable-sampled-readiness/1" : ObservedUnlinkedProtocol.ReadinessSchema,
            Identity(manifestPath), Identity(sealPath),
            mutation == "binding" ? HashText("foreign-binding") : ObservedUnlinkedProtocol.CampaignBindingHash(campaign),
            "component-only-not-independent-readiness", DateTimeOffset.UtcNow);
        var receiptPath = Path.Combine(_workspace, "observed-component-readiness.json");
        PrevalidationExecutor.WriteImmutable(receiptPath, receipt);
        campaign = campaign with { SampledLoss = campaign.SampledLoss! with { Readiness = Identity(receiptPath) } };
        if (mutation == "plan") campaign = campaign with { Plan = campaign.Plan with
            { Executions = campaign.Plan.Executions.Take(34).ToArray() } };
        Action admit = () => ObservedUnlinkedProtocol.ValidateReadiness(campaign);
        var failure = admit.Should().Throw<DurableStorageExperimentException>().Which;
        if (mutation is "schema" or "binding") failure.Code.Should().Be("ObservedUnlinkedReadinessBindingMismatch");
        if (mutation is "build" or "fixture") failure.Code.Should().Be("ObservedUnlinkedReadinessForeignBuildOrHost");
        if (mutation == "environment") failure.Code.Should().Be("ObservedUnlinkedEnvironmentChanged");
        _output.WriteLine($"controlled-readiness-rejection={mutation}:{failure.Code}");
    }

    [Fact]
    public void ObservedUnlinkedCoverageCarriesZeroByteIdentityAndRejectsV5Downgrade()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = ObservedAdmissionManifest();
        var measurement = new SampledLossMeasurement([1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], null)
        {
            ObservedUnlinked = new(1, 1, 0),
        };
        var coverage = manifest.Probes.Select(probe => new PrevalidationCoverage(
            PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id, "coverage-observed", null, true,
            probe.FixtureSlots, probe.FixtureSlots * 4, 571, 512, null, null, null, [])
        {
            SampledLoss = measurement, SampledAdmissible = true,
        }).ToArray();
        var hash = HashText("component-only-observed-report");
        var report = new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
            manifest.SuiteId, hash, manifest.HistoricalReport.Sha256, "coverage-observed-awaiting-independent-review",
            PrevalidationLayout.DeriveSuiteIdentityBound(), coverage, "DTO only; no seal or real probe evidence");
        PrevalidationReportValidation.ValidateReport(manifest, hash, report);
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<PrevalidationReport>(
            System.Text.Json.JsonSerializer.Serialize(report, PrevalidationProtocol.Json), PrevalidationProtocol.Json)!;
        roundtrip.Probes[0].SampledLoss!.ObservedUnlinked.Should().Be(new ObservedUnlinkedMeasurement(1, 1, 0));
        var v5 = manifest with { Schema = SampledLossProtocol.PrevalidationManifestSchema,
            ContextSummaryFieldMapSha256 = SampledLossProtocol.ContextMapSha256, SampledLoss = SampledBinding(manifest) };
        Action downgrade = () => PrevalidationReportValidation.ValidateReport(v5, hash, report);
        downgrade.Should().Throw<DurableStorageExperimentException>();
    }

    private PrevalidationManifest ObservedAdmissionManifest()
    {
        var manifest = PrevalidationContractFixture();
        return manifest with
        {
            Schema = ObservedUnlinkedProtocol.PrevalidationManifestSchema,
            ContextSummaryFieldMapSha256 = ObservedUnlinkedProtocol.ContextMapSha256,
            SampledLoss = ObservedAdmissionBinding() with
            {
                ManagedBinaries = manifest.ManagedBinaries, RuntimeEnvironmentSha256 = manifest.RuntimeEnvironmentSha256,
            },
        };
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("dirty")]
    [InlineData("runner")]
    [InlineData("monitor")]
    [InlineData("tool")]
    [InlineData("support")]
    [InlineData("unstamped")]
    public void ObservedUnlinkedSourceBuildRequiresActualCommittedRevision(string mutation)
    {
        var head = "0123456789abcdef0123456789abcdef01234567";
        var other = "fedcba9876543210fedcba9876543210fedcba98";
        var commits = new MonitoredSourceCommits(head, head, head, head, head);
        if (mutation == "runner") commits = commits with { RunnerCommit = other };
        if (mutation == "monitor") commits = commits with { MonitorCommit = other };
        var tool = "1.0.0+" + (mutation == "tool" ? other : head);
        var support = mutation == "unstamped" ? "1.0.0" : "1.0.0+" + (mutation == "support" ? other : head);
        Action validate = () => ObservedUnlinkedProtocol.ValidateBuildIdentity(commits, head,
            mutation == "dirty" ? " M observed.cs\n" : "", tool, support);
        if (mutation == "clean") validate.Should().NotThrow();
        else validate.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void ObservedUnlinkedContractHashMatchesExactProspectiveFile()
        => MonitoredFile.HashFile(Path.Combine(FindRepositoryRoot(), ObservedUnlinkedProtocol.Path))
            .Should().Be(ObservedUnlinkedProtocol.ProtocolSha256);
}
