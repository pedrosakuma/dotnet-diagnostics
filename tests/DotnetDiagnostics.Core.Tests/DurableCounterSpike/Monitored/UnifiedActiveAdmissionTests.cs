using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Fact]
    public void UnifiedActiveFreshSchemasAndHashesAreRequiredWithoutUpgradingOldInspection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = UnifiedAdmissionManifest();
        PrevalidationProtocol.ValidateShape(manifest);
        DescriptorObservationPolicy.ValidateBinding(manifest.SampledLoss!);
        Action v6 = () => ObservedUnlinkedProtocol.ValidateBinding(manifest.SampledLoss!);
        v6.Should().Throw<DurableStorageExperimentException>();
        Action wrongHash = () => DescriptorObservationPolicy.ValidateBinding(manifest.SampledLoss! with
        { ProtocolSha256 = ObservedUnlinkedProtocol.ProtocolSha256 });
        wrongHash.Should().Throw<DurableStorageExperimentException>();
        Action wrongMap = () => DescriptorObservationPolicy.ValidateBinding(manifest.SampledLoss! with
        { ContextMapSha256 = ObservedUnlinkedProtocol.ContextMapSha256 });
        wrongMap.Should().Throw<DurableStorageExperimentException>();
        Action legacyInspection = () => PrevalidationProtocol.ValidateShape(manifest with
        { Schema = ObservedUnlinkedProtocol.PrevalidationManifestSchema }, allowLegacyInspection: true);
        legacyInspection.Should().Throw<DurableStorageExperimentException>();
        MonitoredFile.HashFile(Path.Combine(FindRepositoryRoot(), UnifiedActiveProtocol.Path))
            .Should().Be(UnifiedActiveProtocol.ProtocolSha256);
        MonitoredFile.HashFile(Path.Combine(FindRepositoryRoot(), ObservedUnlinkedProtocol.Path))
            .Should().Be(ObservedUnlinkedProtocol.ProtocolSha256);
        MonitoredFile.HashFile(Path.Combine(FindRepositoryRoot(), SampledLossProtocol.Path))
            .Should().Be(SampledLossProtocol.ProtocolSha256);
    }

    [Fact]
    public void UnifiedActiveReceiptsBindFreshPolicyInsteadOfOnlyNonNullBinding()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = UnifiedAdmissionManifest();
        var adoption = new PrevalidationAdoption(UnifiedActiveProtocol.AdoptionSchema,
            UnifiedActiveProtocol.ProtocolSha256, "component-only", DateTimeOffset.UtcNow, PrevalidationProtocol.Scope);
        var acceptance = new PrevalidationAcceptance(UnifiedActiveProtocol.AcceptanceSchema,
            PrevalidationProtocol.Scope, PrevalidationProtocol.AddendumSha256, "component-only",
            manifest.SourceCommits, PrevalidationProtocol.BinaryInventoryHash(manifest),
            manifest.ComponentEvidence.Sha256, manifest.Attribution.Sha256, manifest.Encoding.Sha256,
            PrevalidationLayout.DeriveSuiteIdentityBound(), true, true, true, true)
        { SampledProtocolSha256 = UnifiedActiveProtocol.ProtocolSha256 };
        var hash = HashText("component-only-no-authorization");
        var authorization = new PrevalidationAuthorization(UnifiedActiveProtocol.PrevalidationAuthorizationSchema,
            PrevalidationProtocol.Scope, manifest.SuiteId, hash, PrevalidationProtocol.AddendumSha256,
            manifest.ImplementationAcceptance.Sha256, manifest.HistoricalReport.Sha256, "component-only",
            DateTimeOffset.UtcNow, 8, 1, true);
        PrevalidationProtocol.ValidateAdoption(manifest, adoption);
        PrevalidationProtocol.ValidateAcceptance(manifest, acceptance);
        PrevalidationProtocol.ValidateAuthorization(manifest, hash, authorization);
        foreach (var prefix in new[] { "durable-prevalidation", "durable-sampled-prevalidation",
            "durable-observed-unlinked-prevalidation" })
        {
            Action oldAdoption = () => PrevalidationProtocol.ValidateAdoption(manifest,
                adoption with { Schema = prefix + "-adoption/1" });
            Action oldAcceptance = () => PrevalidationProtocol.ValidateAcceptance(manifest,
                acceptance with { Schema = prefix + "-implementation-acceptance/1" });
            Action oldAuthorization = () => PrevalidationProtocol.ValidateAuthorization(manifest, hash,
                authorization with { Schema = prefix + "-authorization/1" });
            oldAdoption.Should().Throw<DurableStorageExperimentException>();
            oldAcceptance.Should().Throw<DurableStorageExperimentException>();
            oldAuthorization.Should().Throw<DurableStorageExperimentException>();
        }
        Action oldHash = () => PrevalidationProtocol.ValidateAcceptance(manifest,
            acceptance with { SampledProtocolSha256 = ObservedUnlinkedProtocol.ProtocolSha256 });
        oldHash.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("schema")]
    [InlineData("binding")]
    [InlineData("build")]
    [InlineData("environment")]
    [InlineData("fixture")]
    [InlineData("plan")]
    [InlineData("seal")]
    [InlineData("unsealed")]
    public void UnifiedActiveReadinessRejectsUnsealedForeignOrMismatchedEvidence(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        var pv = UnifiedAdmissionManifest();
        var campaign = PrepareComponentExecution().Manifest with
        {
            Schema = UnifiedActiveProtocol.CampaignManifestSchema,
            SampledLoss = pv.SampledLoss,
            SourceCommits = pv.SourceCommits,
            RuntimeBinary = pv.RuntimeBinary,
            ToolBinary = pv.ToolBinary,
            SampleBinary = pv.SampleBinary,
            NativeBinaries = pv.NativeBinaries,
            Host = pv.Host,
            FixtureManifestSha256 = pv.FixtureManifest.Sha256,
            Clock = pv.Clock,
        };
        if (mutation == "missing")
        {
            Action missing = () => UnifiedActiveProtocol.ValidateReadiness(campaign);
            missing.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("ObservedUnlinkedReadinessMissing");
            return;
        }
        if (mutation == "build") campaign = campaign with { ToolBinary = campaign.ToolBinary with { Sha256 = HashText("foreign") } };
        if (mutation == "environment") campaign = campaign with
        {
            SampledLoss = campaign.SampledLoss! with
            { RuntimeEnvironmentSha256 = HashText("foreign-environment") }
        };
        if (mutation == "fixture") campaign = campaign with { FixtureManifestSha256 = HashText("foreign-fixture") };
        Directory.CreateDirectory(pv.PrivateRoot);
        var manifestPath = Path.Combine(_workspace, "unified-component-manifest.json");
        PrevalidationExecutor.WriteImmutable(manifestPath, pv);
        var sealPath = Path.Combine(pv.PrivateRoot, "seal.json");
        PrevalidationExecutor.WriteImmutable(sealPath, new PrevalidationSeal(
            "durable-prevalidation-seal/1", pv.SuiteId, HashFile(manifestPath), HashText("no-report"), []));
        var receipt = new SampledLossReadiness(
            mutation == "schema" ? ObservedUnlinkedProtocol.ReadinessSchema : UnifiedActiveProtocol.ReadinessSchema,
            Identity(manifestPath), mutation == "seal" ? Identity(sealPath) with { Sha256 = HashText("wrong") } : Identity(sealPath),
            mutation == "binding" ? HashText("wrong-binding") : UnifiedActiveProtocol.CampaignBindingHash(campaign),
            "component-only-not-readiness", DateTimeOffset.UtcNow);
        var receiptPath = Path.Combine(_workspace, "unified-component-readiness.json");
        PrevalidationExecutor.WriteImmutable(receiptPath, receipt);
        campaign = campaign with { SampledLoss = campaign.SampledLoss! with { Readiness = Identity(receiptPath) } };
        if (mutation == "plan") campaign = campaign with
        {
            Plan = campaign.Plan with
            { Executions = campaign.Plan.Executions.Take(34).ToArray() }
        };
        Action admit = () => UnifiedActiveProtocol.ValidateReadiness(campaign);
        var error = admit.Should().Throw<DurableStorageExperimentException>().Which;
        if (mutation is "schema" or "binding") error.Code.Should().Be("ObservedUnlinkedReadinessBindingMismatch");
        if (mutation is "build" or "fixture") error.Code.Should().Be("ObservedUnlinkedReadinessForeignBuildOrHost");
        if (mutation == "environment") error.Code.Should().Be("ObservedUnlinkedEnvironmentChanged");
        _output.WriteLine($"unified-readiness-rejection={mutation}:{error.Code}");
    }

    [Fact]
    public void UnifiedActiveCoverageRootLossIsAdmissibleButNeverComplete()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = UnifiedAdmissionManifest();
        var measurement = SampledLossMeasurement.Empty(unifiedActive: true) with
        { RootSampling = new(0, 0, 0, 1) };
        var coverage = manifest.Probes.Select(probe => new PrevalidationCoverage(
            PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id, "coverage-observed", null, false,
            probe.FixtureSlots, probe.FixtureSlots * 4, 571, 512, null, null, null, [])
        {
            SampledLoss = measurement,
            SampledAdmissible = true,
        }).ToArray();
        var hash = HashText("dto-only-not-evidence");
        var report = new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
            manifest.SuiteId, hash, manifest.HistoricalReport.Sha256, "coverage-observed-awaiting-independent-review",
            PrevalidationLayout.DeriveSuiteIdentityBound(), coverage, "DTO only: no real probes, seal or readiness");
        PrevalidationReportValidation.ValidateReport(manifest, hash, report);
        var restored = JsonSerializer.Deserialize<PrevalidationReport>(
            JsonSerializer.Serialize(report, PrevalidationProtocol.Json), PrevalidationProtocol.Json)!;
        restored.Probes[0].ObservationPopulation!.RootSampling!.UnknownBranchDescendants.Should().Be("unknown-not-zero");
        var forged = coverage.ToArray();
        forged[0] = forged[0] with { MonitoringComplete = true };
        Action complete = () => PrevalidationReportValidation.ValidateReport(manifest, hash, report with { Probes = forged });
        complete.Should().Throw<DurableStorageExperimentException>();
        Action downgrade = () => PrevalidationReportValidation.ValidateReport(ObservedAdmissionManifest(), hash, report);
        downgrade.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public async Task UnifiedActiveF3SourceRecoveryUsesOneSharedRecordBudgetAndExactOnceMerge()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Schema = UnifiedActiveProtocol.CampaignManifestSchema,
                SampledLoss = UnifiedBinding() with
                {
                    ManagedBinaries = [fixture.Manifest.ToolBinary],
                    RuntimeEnvironmentSha256 = PrevalidationProtocol.RuntimeEnvironmentHash()
                },
            }
        };
        var launcher = new ScriptedWorkerLauncher(ScriptedWorkerBehavior.SuccessfulRecovery, awaitIdentityEvent: true);
        var execution = MonitoredExecutionPlanner.Expand().Single(item => item.CaseId == "F3" && item.Candidate == "A");
        var outcome = await MonitoredCampaignRunner.RunExecutionForComponentAsync(fixture, execution, launcher,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        var summaries = Directory.EnumerateFiles(fixture.Manifest.OutputRoot, "*-monitor.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<MonitoredSweepSummary>(
                line, PrevalidationProtocol.Json)!).ToArray();
        foreach (var summary in summaries)
            _output.WriteLine(JsonSerializer.Serialize(summary, PrevalidationProtocol.Json));
        outcome.FailureCode.Should().BeNull();
        outcome.SampledAdmissible.Should().BeTrue();
        outcome.SampledLoss!.RootSampling.Should().NotBeNull();
        launcher.Descriptors.Should().HaveCount(2);
        summaries.Length.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(2_048);
        var total = summaries.Aggregate(SampledLossMeasurement.Empty(unifiedActive: true),
            (sum, item) => SampledLossMeasurement.Merge(sum, item.SampledLoss!));
        PrevalidationReportValidation.MeasurementsEqual(total, outcome.SampledLoss).Should().BeTrue();
    }

    private PrevalidationManifest UnifiedAdmissionManifest()
    {
        var manifest = PrevalidationContractFixture();
        return manifest with
        {
            Schema = UnifiedActiveProtocol.PrevalidationManifestSchema,
            ContextSummaryFieldMapSha256 = UnifiedActiveProtocol.ContextMapSha256,
            SampledLoss = UnifiedBinding() with
            {
                ManagedBinaries = manifest.ManagedBinaries,
                RuntimeEnvironmentSha256 = manifest.RuntimeEnvironmentSha256,
            },
        };
    }
}
