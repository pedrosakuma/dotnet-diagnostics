namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record PrevalidationEvidenceValidation(string Schema, string SuiteId, string Status,
    int Outcomes, int SealedArtifacts, bool CampaignAdmissionGranted)
{
    public string? FailureCode { get; init; }
    public string? FailureStage { get; init; }
    public PrevalidationSecondaryFailures? SecondaryFailures { get; init; }
    public PrevalidationCoverage? FirstFailedProbe { get; init; }
}

internal static class PrevalidationReportValidation
{
    internal static PrevalidationEvidenceValidation Validate(string manifestPath)
    {
        PrevalidationProtocol.EnsureImmutable(manifestPath);
        var manifest = PrevalidationProtocol.Read<PrevalidationManifest>(manifestPath);
        PrevalidationProtocol.ValidateShape(manifest, allowLegacyInspection: true);
        var manifestHash = MonitoredFile.HashFile(manifestPath);
        var sealPath = Path.Combine(manifest.PrivateRoot, "seal.json");
        var partialPath = Path.Combine(manifest.PrivateRoot, "partial.json");
        if (File.Exists(partialPath))
        {
            var partial = PrevalidationProtocol.Read<PrevalidationReport>(partialPath);
            ValidateReport(manifest, manifestHash, partial);
            PrevalidationProtocol.Require(partial.Status.StartsWith("partial-unsealed:", StringComparison.Ordinal),
                "PrevalidationPartialStatusMismatch");
            return new("durable-prevalidation-evidence-validation/2", manifest.SuiteId,
                "partial-unsealed-not-readiness-evidence", partial.Probes.Count, 0, false)
            {
                FailureCode = partial.FailureCode,
                FailureStage = partial.FailureStage,
                SecondaryFailures = partial.SecondaryFailures,
                FirstFailedProbe = partial.Probes.FirstOrDefault(static item => item.Outcome == "incomplete"),
            };
        }
        PrevalidationProtocol.EnsureImmutable(sealPath);
        var seal = PrevalidationProtocol.Read<PrevalidationSeal>(sealPath, 16_777_216);
        PrevalidationProtocol.Require(seal.Schema == "durable-prevalidation-seal/1"
            && seal.SuiteId == manifest.SuiteId && seal.ManifestSha256 == manifestHash
            && seal.Artifacts.Count is > 0 and < 4_096
            && seal.Artifacts.Count + 1 <= PrevalidationLayout.DeriveSuiteIdentityBound(),
            "PrevalidationSealMismatch");
        var actualFiles = MonitoredPathRules.EnumerateFilesRejectingLinks(manifest.PrivateRoot, 4_096, 4_096)
            .Select(path => Path.GetRelativePath(manifest.PrivateRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        PrevalidationProtocol.Require(actualFiles.Count == seal.Artifacts.Count + 1
            && actualFiles.Remove("seal.json"), "PrevalidationSealInventoryMismatch");
        long bytes = new FileInfo(sealPath).Length;
        foreach (var artifact in seal.Artifacts)
        {
            PrevalidationProtocol.Require(actualFiles.Remove(artifact.RelativePath)
                && artifact.Length >= 0, "PrevalidationSealArtifactMismatch");
            var path = MonitoredPathRules.ResolveContainedExistingFile(manifest.PrivateRoot, artifact.RelativePath);
            PrevalidationProtocol.EnsureImmutable(path);
            PrevalidationProtocol.Require(new FileInfo(path).Length == artifact.Length
                && MonitoredFile.HashFile(path) == artifact.Sha256, "PrevalidationSealedEvidenceChanged");
            bytes = checked(bytes + artifact.Length);
        }
        PrevalidationProtocol.Require(bytes <= manifest.Bounds.SuiteBytes, "PrevalidationSealedSuiteByteLimit");
        var reportPath = Path.Combine(manifest.PrivateRoot, "report.json");
        PrevalidationProtocol.Require(MonitoredFile.HashFile(reportPath) == seal.ReportSha256,
            "PrevalidationReportHashMismatch");
        var report = PrevalidationProtocol.Read<PrevalidationReport>(reportPath);
        ValidateReport(manifest, manifestHash, report);
        ValidateControlCopies(manifest, manifestHash);
        ValidateRawCoverage(manifest, report);
        PrevalidationProtocol.Require(report.Status == (report.Probes.All(PrevalidationExecutor.HasCoverage)
            ? "coverage-observed-awaiting-independent-review" : "stopped-incomplete"),
            "PrevalidationReportStatusMismatch");
        return new("durable-prevalidation-evidence-validation/2", manifest.SuiteId, report.Status,
            report.Probes.Count, seal.Artifacts.Count, false)
        {
            FailureCode = report.FailureCode,
            FailureStage = report.FailureStage,
            SecondaryFailures = report.SecondaryFailures,
            FirstFailedProbe = report.Probes.FirstOrDefault(static item => item.Outcome == "incomplete"),
        };
    }

    private static void ValidateControlCopies(PrevalidationManifest manifest, string manifestHash)
    {
        var start = PrevalidationProtocol.Read<PrevalidationStartReceipt>(
            Path.Combine(manifest.PrivateRoot, "suite-start.json"));
        PrevalidationProtocol.Require(start.Schema == "durable-prevalidation-start/1"
            && start.SuiteId == manifest.SuiteId && start.ManifestSha256 == manifestHash,
            "PrevalidationSealedStartMismatch");
        var expected = new (string Name, string Hash)[]
        {
            ("resolved-manifest.json", manifestHash),
            ("authorization.json", start.AuthorizationSha256),
            ("adoption.json", manifest.Adoption.Sha256),
            ("implementation-acceptance.json", manifest.ImplementationAcceptance.Sha256),
            ("attribution.json", manifest.Attribution.Sha256),
            ("encoding.json", manifest.Encoding.Sha256),
            ("component-evidence.json", manifest.ComponentEvidence.Sha256),
            ("historical-readiness.md", manifest.HistoricalReport.Sha256),
            ("fixture-manifest.json", manifest.FixtureManifest.Sha256),
        };
        foreach (var (name, hash) in expected)
        {
            PrevalidationProtocol.Require(MonitoredFile.HashFile(
                Path.Combine(manifest.PrivateRoot, "controls", name)) == hash, "PrevalidationControlCopyMismatch");
        }
        var authorization = PrevalidationProtocol.Read<PrevalidationAuthorization>(
            Path.Combine(manifest.PrivateRoot, "controls", "authorization.json"));
        PrevalidationProtocol.ValidateAuthorization(manifest, manifestHash, authorization);
    }

    internal static void ValidateReport(PrevalidationManifest manifest, string manifestHash, PrevalidationReport report)
    {
        var legacy = manifest.ContextSummaryFieldMapSha256 == PrevalidationProtocol.LegacyContextSummaryFieldMapSha256;
        PrevalidationProtocol.Require(report.Schema == (legacy
                ? "durable-prevalidation-report/1" : PrevalidationProtocol.ReportSchema)
            && report.Scope == PrevalidationProtocol.Scope && report.SuiteId == manifest.SuiteId
            && report.ManifestSha256 == manifestHash && !report.CampaignAdmissionGranted
            && report.HistoricalReportSha256 == manifest.HistoricalReport.Sha256
            && report.DerivedSuiteIdentityBound == PrevalidationLayout.DeriveSuiteIdentityBound()
            && report.Probes.Count == 8, "PrevalidationReportMismatch");
        PrevalidationFailureCodes.Validate(report.FailureCode, report.SecondaryFailures);
        if (legacy)
        {
            PrevalidationProtocol.Require(report.FailureCode is null && report.FailureStage is null
                && report.SecondaryFailures is null,
                "PrevalidationLegacyFailureEvidenceMismatch");
        }
        else
        {
            PrevalidationFailureCodes.ValidateStage(report.FailureCode, report.FailureStage);
            var first = report.Probes.FirstOrDefault(static item => item.Outcome == "incomplete");
            PrevalidationProtocol.Require(first is null
                    ? report.Status.StartsWith("partial-unsealed:", StringComparison.Ordinal)
                        || report.FailureCode is null && report.SecondaryFailures is null
                    : report.FailureCode == first.FailureCode && report.FailureStage == first.FailureStage,
                "PrevalidationPrimaryFailureMismatch");
            PrevalidationProtocol.Require(!report.Status.StartsWith("partial-unsealed:", StringComparison.Ordinal)
                || report.FailureCode is not null, "PrevalidationPartialFailureMissing");
        }
        var stopped = false;
        for (var index = 0; index < 8; index++)
        {
            var probe = manifest.Probes[index];
            var outcome = report.Probes[index];
            PrevalidationProtocol.Require(outcome.Schema == (legacy
                    ? "durable-prevalidation-coverage/1" : PrevalidationProtocol.CoverageSchema)
                && outcome.Ordinal == probe.Ordinal && outcome.ProbeId == probe.Id
                && outcome.DeclaredFixtureSlots == probe.FixtureSlots && !outcome.CampaignAdmissionGranted
                && outcome.Outcome is "coverage-observed" or "incomplete" or "not-run"
                && (!stopped || outcome.Outcome == "not-run"), "PrevalidationOutcomeOrderMismatch");
            PrevalidationFailureCodes.Validate(outcome.FailureCode, outcome.SecondaryFailures);
            if (legacy)
            {
                PrevalidationProtocol.Require(outcome.SecondaryFailures is null && outcome.FailureStage is null,
                    "PrevalidationLegacyFailureEvidenceMismatch");
            }
            else
            {
                PrevalidationFailureCodes.ValidateStage(outcome.FailureCode, outcome.FailureStage);
                PrevalidationProtocol.Require(outcome.Outcome == "coverage-observed"
                    ? PrevalidationExecutor.HasCoverage(outcome)
                    : !outcome.MonitoringComplete && outcome.FailureCode is not null
                        && (outcome.Outcome != "not-run" || outcome.SecondaryFailures is null),
                    "PrevalidationOutcomeFailureMismatch");
            }
            if (PrevalidationExecutor.HasCoverage(outcome))
            {
                PrevalidationProtocol.Require(outcome.ObservedFixtureFiles == probe.FixtureSlots * 4
                    && outcome.MaximumContextIdentities is > 0 and <= 571
                    && outcome.MaximumSuiteBytes is >= 0 and <= 3_221_225_472,
                    "PrevalidationOutcomeCoverageMismatch");
            }
            else
            {
                stopped = true;
            }
        }
    }

    private static void ValidateRawCoverage(PrevalidationManifest manifest, PrevalidationReport report)
    {
        foreach (var outcome in report.Probes.Where(PrevalidationExecutor.HasCoverage))
        {
            var probe = manifest.Probes[outcome.Ordinal - 1];
            var cleanup = PrevalidationProtocol.Read<PrevalidationCleanupResult>(
                Path.Combine(PrevalidationLayout.ContextRoot(manifest, probe), "cleanup.json"));
            PrevalidationProtocol.Require(cleanup.Schema == (report.Schema == "durable-prevalidation-report/1"
                    ? "durable-prevalidation-cleanup/1" : "durable-prevalidation-cleanup/2")
                && cleanup.Quiescent && cleanup.Errors.Count == 0 && cleanup.AdditionalErrorCount == 0
                && cleanup.Unconfirmed.Count == 0,
                "PrevalidationCleanupEvidenceIncomplete");
            var history = PrevalidationLayout.HistoryRoot(manifest, probe);
            for (var slot = 0; slot < probe.FixtureSlots; slot++)
            {
                for (var member = 0; member < 4; member++)
                {
                    var path = MonitoredPathRules.ResolveContainedExistingFile(history,
                        $"inventory-{slot:D2}/fixture-{member}.bin");
                    PrevalidationProtocol.Require(new FileInfo(path).Length == 512,
                        "PrevalidationFixtureEvidenceMissing");
                }
            }
            if (probe.Ordinal != 8)
            {
                var rawPath = Path.Combine(PrevalidationLayout.Settings(manifest, probe).OutputRoot,
                    $"{probe.Ordinal:D2}-outcome.json");
                var raw = PrevalidationProtocol.Read<MonitoredCaseOutcome>(rawPath);
                var projected = PrevalidationExecutor.ProjectCoverage(probe, raw, manifest.PrivateRoot,
                    requireEntryCompletion: true);
                PrevalidationProtocol.Require(PrevalidationExecutor.HasCoverage(projected)
                    && projected.SourceOffered == outcome.SourceOffered
                    && projected.SourceCommitted == outcome.SourceCommitted
                    && projected.SourceCoverage == outcome.SourceCoverage
                    && projected.RequiredBoundaries.SequenceEqual(outcome.RequiredBoundaries),
                    "PrevalidationRawCoverageMismatch");
                var descriptor = PrevalidationProtocol.Read<MonitoredWorkerDescriptor>(
                    Path.Combine(PrevalidationLayout.ExecutionRoot(manifest, probe), "worker-descriptor.json"));
                PrevalidationProtocol.Require(descriptor.Schema == PrevalidationProtocol.WorkerSchema
                    && descriptor.ManifestSha256 == report.ManifestSha256 && descriptor.Execution == probe.Execution,
                    "PrevalidationRawDescriptorMismatch");
            }
            else
            {
                var root = PrevalidationLayout.ContextRoot(manifest, probe);
                var proof = PrevalidationProtocol.Read<PrevalidationDescriptorProof>(
                    Path.Combine(root, "geometry-descriptor-proof.json"), 65_536);
                PrevalidationProtocol.Require(proof.Schema == "durable-prevalidation-descriptor-fixtures/1"
                    && proof.Fixtures.Count == 32 && proof.Fixtures.All(static item => item.Length == 512)
                    && proof.Fixtures.Select(static item => item.Identity).Distinct(StringComparer.Ordinal).Count() == 32,
                    "PrevalidationGeometryProofMissing");
                var raw = MonitoredFile.ReadBounded(Path.Combine(root, "coordinator-monitor.jsonl"), 2_048 * 1_024);
                var summaries = System.Text.Encoding.UTF8.GetString(raw)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => System.Text.Json.JsonSerializer.Deserialize<MonitoredSweepSummary>(
                        line, PrevalidationProtocol.Json))
                    .ToArray();
                foreach (var summary in summaries)
                {
                    PrevalidationProtocol.Require(summary is not null, "PrevalidationSummaryMissing");
                    PrevalidationExecutor.ValidateSummaryFailure(summary!);
                }
                PrevalidationProtocol.Require(summaries.Length is > 0 and <= 2_048
                    && summaries.All(item => item is { Complete: true, Alarm: null,
                        CurrentContextIdentities: > 0 and <= 571, CurrentContextRootedIdentities: <= 539,
                        IdentityCount: <= 4_096, ObservedSweepBytes: <= 3_221_225_472 })
                    && summaries.Any(item => item?.Boundary == "fixtures-complete" && item.ObservationKind == "boundary")
                    && summaries.Any(item => item?.Boundary == "harness-exit-quiescent" && item.ObservationKind == "boundary")
                    && summaries.Any(item => item?.Boundary == "owned-cleanup-quiescent" && item.ObservationKind == "boundary"),
                    "PrevalidationGeometryEntryCoverageMissing");
                var measured = summaries.Single(item => item?.Boundary == "geometry-539-rooted-32-descriptor-only"
                        && item.ObservationKind == "boundary");
                PrevalidationProtocol.Require(measured is { Complete: true, Alarm: null,
                    CurrentContextRootedIdentities: 539, CurrentContextIdentities: 571,
                    DescriptorOnlyIdentityCount: 32 }, "PrevalidationGeometryMeasurementMissing");
            }
        }
    }
}
