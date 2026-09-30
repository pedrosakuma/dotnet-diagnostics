using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static partial class AdvisoryLlmAssessment
{
    public const string ContinuationAuthorizationVariable =
        "DOTNET_DIAGNOSTICS_ADVISORY_LLM_CONTINUE";
    private const int CurrentContinuationSchemaVersion = 1;
    private const int MaximumContinuationPlanBytes = 1024 * 1024;

    public static AdvisoryContinuationPlan FreezeContinuationPlan(
        AdvisoryLlmProtocol protocol,
        string draftPath,
        string outputPath)
    {
        var draft = ReadContinuationPlan(draftPath);
        if (draft.PlanFingerprint != string.Empty)
        {
            throw new InvalidDataException("A continuation draft must have an empty planFingerprint.");
        }

        var plan = draft with { PlanFingerprint = ComputeContinuationPlanFingerprint(draft) };
        ValidateContinuationPlan(protocol, plan);
        _ = ValidateContinuationSource(protocol, plan);
        WriteNew(outputPath, Serialize(plan), MaximumContinuationPlanBytes);
        return plan;
    }

    public static AdvisoryContinuationPlan LoadContinuationPlan(
        AdvisoryLlmProtocol protocol,
        string path)
    {
        var plan = ReadContinuationPlan(path);
        ValidateContinuationPlan(protocol, plan);
        _ = ValidateContinuationSource(protocol, plan);
        return plan;
    }

    public static string ComputeContinuationPlanFingerprint(AdvisoryContinuationPlan plan)
        => Sha256(Serialize(plan with { PlanFingerprint = string.Empty }));

    public static async Task<AdvisoryContinuationSummary> ContinueAsync(
        AdvisoryLlmProtocol protocol,
        AdvisoryContinuationPlan plan,
        string outputDirectory,
        IAdvisoryStructuredTransport transport,
        CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(ContinuationAuthorizationVariable) != "1")
        {
            throw new InvalidOperationException(
                $"Continuation requires {ContinuationAuthorizationVariable}=1.");
        }

        ValidateContinuationPlan(protocol, plan);
        var contexts = ValidateContinuationSource(protocol, plan);
        if (Directory.Exists(outputDirectory))
        {
            throw new IOException("Continuation output directory must not already exist.");
        }
        if (IsSameOrChildPath(Path.GetFullPath(outputDirectory), Path.GetFullPath(plan.SourceRunRoot)))
        {
            throw new InvalidDataException("Continuation output must be outside the immutable source run.");
        }

        Directory.CreateDirectory(outputDirectory);
        var recoveredSeals = contexts
            .Where(value =>
                value.Binding.Disposition == AdvisoryContinuationDisposition.RecoverRetainedPhaseA
                && value.RecoveryError is null)
            .ToDictionary(
                value => value.Slot.SlotId,
                value => WriteRecoveredSeal(protocol, value, outputDirectory),
                StringComparer.Ordinal);
        var started = DateTimeOffset.UtcNow;
        using var inferenceBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inferenceBudget.CancelAfter(TimeSpan.FromSeconds(
            Math.Min(protocol.Limits.MaximumInferenceSeconds, plan.MaximumNewPhaseBCalls * 120)));
        var results = new List<AdvisoryContinuationCaseResult>(contexts.Count);
        var actualCalls = 0;
        var abort = false;
        string? abortDetail = null;
        foreach (var context in contexts)
        {
            AdvisoryContinuationCaseResult result;
            if (context.Binding.Disposition == AdvisoryContinuationDisposition.ReuseCompleted)
            {
                result = ContinuationResult(
                    context,
                    AdvisoryPhaseOrigin.SourceCompletedReused,
                    AdvisoryPhaseOrigin.SourceCompletedReused,
                    context.SourceCase,
                    context.NormalizedPhaseA,
                    "Both completed source phases were reused without new model calls.");
            }
            else if (context.Binding.Disposition == AdvisoryContinuationDisposition.Unavailable)
            {
                result = ContinuationResult(
                    context,
                    AdvisoryPhaseOrigin.SourceFailurePreserved,
                    AdvisoryPhaseOrigin.NotRun,
                    context.SourceCase,
                    null,
                    "The source response was unavailable; no model call was made.");
            }
            else if (context.RecoveryError is not null)
            {
                result = ContinuationResult(
                    context,
                    AdvisoryPhaseOrigin.SourceFailurePreserved,
                    AdvisoryPhaseOrigin.NotRun,
                    context.SourceCase,
                    context.NormalizedPhaseA,
                    "The retained Phase A response remained invalid after exact framing normalization: "
                    + context.RecoveryError);
            }
            else if (abort)
            {
                var recoveredSeal = recoveredSeals[context.Slot.SlotId];
                var skipped = context.SourceCase with
                {
                    PhaseA = recoveredSeal.Call,
                    SealedPhaseAPath = recoveredSeal.Path,
                    SealedPhaseASha256 = recoveredSeal.Sha256,
                    PhaseAResponse = context.PhaseAResponse,
                    PhaseB = SkippedCall(
                        protocol.PhaseBModel,
                        "Continuation Phase B skipped after global prerequisite failure: " + abortDetail),
                };
                result = ContinuationResult(
                    context,
                    AdvisoryPhaseOrigin.SourceFailurePreserved,
                    AdvisoryPhaseOrigin.NotRun,
                    skipped,
                    context.NormalizedPhaseA,
                    "Phase B was skipped after a global prerequisite failure.");
            }
            else
            {
                if (actualCalls >= plan.MaximumNewPhaseBCalls)
                {
                    throw new InvalidDataException("Continuation exceeded its predeclared model-call bound.");
                }

                result = await ContinueRecoveredCaseAsync(
                    protocol,
                    context,
                    recoveredSeals[context.Slot.SlotId],
                    transport,
                    inferenceBudget.Token).ConfigureAwait(false);
                actualCalls++;
                if (result.Result.PhaseB.Status == AdvisoryCallStatus.TransportFailed
                    && IsGlobalPrerequisiteFailure(result.Result.PhaseB.Detail))
                {
                    abort = true;
                    abortDetail = result.Result.PhaseB.Detail;
                }
            }

            results.Add(result);
            var caseDirectory = Path.Combine(outputDirectory, context.Slot.SlotId);
            Directory.CreateDirectory(caseDirectory);
            WriteNew(
                Path.Combine(caseDirectory, "continuation-case-result.json"),
                Serialize(result),
                protocol.Limits.MaximumCaseArtifactBytes);
        }

        var summary = new AdvisoryContinuationSummary(
            CurrentContinuationSchemaVersion,
            plan.PlanId,
            plan.PlanFingerprint,
            protocol.ProtocolFingerprint,
            plan.SourceRunSummarySha256,
            started,
            DateTimeOffset.UtcNow,
            plan.MaximumNewPhaseBCalls,
            actualCalls,
            results.All(value => value.Result.PhaseBResponse is not null),
            abort,
            abortDetail,
            results);
        WriteNew(
            Path.Combine(outputDirectory, "continuation-summary.json"),
            Serialize(summary),
            protocol.Limits.MaximumCaseArtifactBytes * protocol.Limits.MaximumCases);
        return summary;
    }

    private static async Task<AdvisoryContinuationCaseResult> ContinueRecoveredCaseAsync(
        AdvisoryLlmProtocol protocol,
        ContinuationContext context,
        RecoveredSeal recoveredSeal,
        IAdvisoryStructuredTransport transport,
        CancellationToken cancellationToken)
    {
        var normalized = context.NormalizedPhaseA
            ?? throw new InvalidDataException("Recovered Phase A normalization is unavailable.");
        var phaseAResponse = context.PhaseAResponse
            ?? throw new InvalidDataException("Recovered Phase A response is unavailable.");

        var packet = CalibrationPackets.ReadPacket(context.Slot.PacketPath);
        var candidates = BuildCandidates(
            context.Slot,
            packet,
            phaseAResponse,
            context.Projection,
            out var mapping);
        var phaseBPrompt = BuildPhaseBPrompt(protocol, context.Projection, candidates);
        var phaseBInputSha = Sha256(Encoding.UTF8.GetBytes(
            context.Projection.ProjectionSha256
            + "\n"
            + recoveredSeal.Sha256
            + "\n"
            + SerializeText(candidates)));
        var phaseB = await InvokeAsync(
            protocol.PhaseBModel,
            phaseBPrompt,
            phaseBInputSha,
            protocol.Limits,
            transport,
            cancellationToken).ConfigureAwait(false);
        AdvisoryPhaseBResponse? phaseBResponse = null;
        if (phaseB.Status == AdvisoryCallStatus.Succeeded)
        {
            try
            {
                var normalizedB = NormalizeJsonResponse(
                    phaseB.RawResponse!,
                    protocol.Limits.MaximumResponseBytes);
                phaseB = phaseB with
                {
                    ResponseFramingTransform = normalizedB.Transform,
                    ResponseFramingVersion = normalizedB.Version,
                    NormalizedResponseSha256 = normalizedB.PayloadSha256,
                };
                phaseBResponse = ParsePhaseBNormalized(normalizedB.Payload, candidates, protocol.Limits);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                phaseB = phaseB with
                {
                    Status = AdvisoryCallStatus.InvalidResponse,
                    Detail = exception.Message,
                };
            }
        }

        var continued = context.SourceCase with
        {
            PhaseA = recoveredSeal.Call,
            SealedPhaseAPath = recoveredSeal.Path,
            SealedPhaseASha256 = recoveredSeal.Sha256,
            PhaseB = phaseB,
            CandidateMapping = mapping,
            PhaseAResponse = phaseAResponse,
            PhaseBResponse = phaseBResponse,
        };
        return ContinuationResult(
            context,
            AdvisoryPhaseOrigin.SourceFailurePreserved,
            AdvisoryPhaseOrigin.NewlyInvoked,
            continued,
            normalized,
            "Retained Phase A was framing-normalized and sealed; Phase B was newly invoked.");
    }

    private static RecoveredSeal WriteRecoveredSeal(
        AdvisoryLlmProtocol protocol,
        ContinuationContext context,
        string outputRoot)
    {
        var normalized = context.NormalizedPhaseA
            ?? throw new InvalidDataException("Recovered Phase A normalization is unavailable.");
        var phaseAResponse = context.PhaseAResponse
            ?? throw new InvalidDataException("Recovered Phase A response is unavailable.");
        var sourcePhaseA = context.SourceCase.PhaseA with
        {
            ResponseFramingTransform = normalized.Transform,
            ResponseFramingVersion = normalized.Version,
            NormalizedResponseSha256 = normalized.PayloadSha256,
        };
        var caseDirectory = Path.Combine(outputRoot, context.Slot.SlotId);
        Directory.CreateDirectory(caseDirectory);
        var seal = new AdvisoryPhaseASeal(
            CurrentSchemaVersion,
            protocol.ProtocolId,
            protocol.ProtocolFingerprint,
            context.Slot.SlotId,
            context.Slot.PacketFingerprint,
            context.Projection.ProjectionSha256,
            sourcePhaseA.PromptSha256,
            sourcePhaseA.ResponseSha256,
            sourcePhaseA,
            phaseAResponse);
        var sealPath = Path.Combine(caseDirectory, "phase-a.recovered.sealed.json");
        var sealBytes = Serialize(seal);
        WriteNew(sealPath, sealBytes, protocol.Limits.MaximumCaseArtifactBytes);
        return new RecoveredSeal(sealPath, Sha256(sealBytes), sourcePhaseA);
    }

    private static AdvisoryContinuationCaseResult ContinuationResult(
        ContinuationContext context,
        AdvisoryPhaseOrigin phaseAOrigin,
        AdvisoryPhaseOrigin phaseBOrigin,
        AdvisoryCaseResult result,
        AdvisoryNormalizedJson? normalized,
        string detail)
        => new(
            context.Slot.SlotId,
            context.Binding.Disposition,
            detail,
            context.Binding.SourceCaseResultSha256,
            context.SourceCase.PhaseA.Status,
            context.SourceCase.PhaseB.Status,
            context.SourceCase.PhaseA.ResponseSha256,
            normalized?.PayloadSha256,
            normalized?.Transform,
            phaseAOrigin,
            phaseBOrigin,
            result);

    private static AdvisoryContinuationPlan ReadContinuationPlan(string path)
    {
        var bytes = ReadBounded(path, MaximumContinuationPlanBytes);
        RejectDuplicateProperties(bytes);
        return JsonSerializer.Deserialize<AdvisoryContinuationPlan>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The continuation plan was empty.");
    }

    private static void ValidateContinuationPlan(
        AdvisoryLlmProtocol protocol,
        AdvisoryContinuationPlan plan)
    {
        if (plan.SchemaVersion != CurrentContinuationSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported continuation schema version {plan.SchemaVersion}.");
        }
        ValidateManifestText(plan.PlanId, 200, "continuation plan id");
        RequireSha(plan.PlanFingerprint, "continuation plan fingerprint");
        if (!FixedEquals(plan.PlanFingerprint, ComputeContinuationPlanFingerprint(plan))
            || !FixedEquals(plan.ProtocolFingerprint, protocol.ProtocolFingerprint))
        {
            throw new InvalidDataException("Continuation plan fingerprint or protocol binding is invalid.");
        }
        if (!Path.IsPathFullyQualified(plan.SourceRunRoot)
            || !Directory.Exists(plan.SourceRunRoot))
        {
            throw new InvalidDataException("Continuation source run root must be an existing absolute directory.");
        }
        RequireSha(plan.SourceRunSummarySha256, "source run summary hash");
        if (plan.Cases is null
            || plan.MaximumNewPhaseBCalls != 6
            || plan.Cases.Count != protocol.Slots.Count)
        {
            throw new InvalidDataException("Continuation must predeclare exactly six or fewer bounded B calls.");
        }
        EnsureUnique(plan.Cases.Select(value => value.SlotId), "continuation slot id");
        if (!plan.Cases.Select(value => value.SlotId).Order(StringComparer.Ordinal).SequenceEqual(
            protocol.Slots.Select(value => value.SlotId).Order(StringComparer.Ordinal),
            StringComparer.Ordinal)
            || plan.Cases.Count(value =>
                value.Disposition == AdvisoryContinuationDisposition.RecoverRetainedPhaseA) != 6
            || plan.Cases.Count(value =>
                value.Disposition == AdvisoryContinuationDisposition.ReuseCompleted) != 1
            || plan.Cases.Count(value =>
                value.Disposition == AdvisoryContinuationDisposition.Unavailable) != 1)
        {
            throw new InvalidDataException("Continuation dispositions must bind six recoveries, one reuse, and one unavailable case.");
        }
        if (plan.Cases.Single(value =>
                value.Disposition == AdvisoryContinuationDisposition.Unavailable).SlotId
            != protocol.Slots[4].SlotId
            || plan.Cases.Single(value =>
                value.Disposition == AdvisoryContinuationDisposition.ReuseCompleted).SlotId
            != protocol.Slots[7].SlotId)
        {
            throw new InvalidDataException(
                "Continuation must preserve the predeclared unavailable fifth slot and completed eighth slot.");
        }
        foreach (var binding in plan.Cases)
        {
            RequireSha(binding.SourceCaseResultSha256, "source case result hash");
            if (binding.Disposition == AdvisoryContinuationDisposition.ReuseCompleted)
            {
                RequireSha(binding.SourcePhaseASealSha256 ?? string.Empty, "source Phase A seal hash");
            }
            else if (binding.SourcePhaseASealSha256 is not null)
            {
                throw new InvalidDataException("Only the completed reused case may bind a source Phase A seal.");
            }
        }
    }

    private static List<ContinuationContext> ValidateContinuationSource(
        AdvisoryLlmProtocol protocol,
        AdvisoryContinuationPlan plan)
    {
        var summaryPath = ScopedSourcePath(plan.SourceRunRoot, "run-summary.json");
        var summaryBytes = ReadBounded(
            summaryPath,
            protocol.Limits.MaximumCaseArtifactBytes * protocol.Limits.MaximumCases);
        if (!FixedEquals(Sha256(summaryBytes), plan.SourceRunSummarySha256))
        {
            throw new InvalidDataException("Continuation source run summary hash changed.");
        }
        RejectDuplicateProperties(summaryBytes);
        var sourceSummary = JsonSerializer.Deserialize<AdvisoryRunSummary>(summaryBytes, JsonOptions)
            ?? throw new InvalidDataException("Continuation source run summary was empty.");
        if (sourceSummary.ProtocolId != protocol.ProtocolId
            || !FixedEquals(sourceSummary.ProtocolFingerprint, protocol.ProtocolFingerprint)
            || sourceSummary.Cases.Count != protocol.Slots.Count)
        {
            throw new InvalidDataException("Continuation source run does not match the frozen protocol.");
        }

        var contexts = new List<ContinuationContext>(protocol.Slots.Count);
        foreach (var slot in protocol.Slots)
        {
            var binding = plan.Cases.Single(value => value.SlotId == slot.SlotId);
            var casePath = ScopedSourcePath(plan.SourceRunRoot, slot.SlotId, "case-result.json");
            var caseBytes = ReadBounded(casePath, protocol.Limits.MaximumCaseArtifactBytes);
            if (!FixedEquals(Sha256(caseBytes), binding.SourceCaseResultSha256))
            {
                throw new InvalidDataException($"Continuation source case '{slot.SlotId}' hash changed.");
            }
            RejectDuplicateProperties(caseBytes);
            var sourceCase = JsonSerializer.Deserialize<AdvisoryCaseResult>(caseBytes, JsonOptions)
                ?? throw new InvalidDataException($"Continuation source case '{slot.SlotId}' was empty.");
            var summaryCase = sourceSummary.Cases.SingleOrDefault(value => value.SlotId == slot.SlotId)
                ?? throw new InvalidDataException($"Source summary omitted slot '{slot.SlotId}'.");
            if (!FixedEquals(Sha256(Serialize(sourceCase)), Sha256(Serialize(summaryCase))))
            {
                throw new InvalidDataException($"Source summary and case file disagree for '{slot.SlotId}'.");
            }

            var projection = Project(protocol, slot);
            ValidateSourceCaseCommon(protocol, slot, projection, sourceCase);
            AdvisoryNormalizedJson? normalizedA = null;
            AdvisoryPhaseAResponse? phaseAResponse = null;
            string? recoveryError = null;
            if (binding.Disposition == AdvisoryContinuationDisposition.RecoverRetainedPhaseA)
            {
                if (sourceCase.PhaseA.Status != AdvisoryCallStatus.InvalidResponse
                    || sourceCase.PhaseA.RawResponse is null
                    || sourceCase.PhaseB.Status != AdvisoryCallStatus.Skipped
                    || sourceCase.SealedPhaseAPath is not null
                    || sourceCase.SealedPhaseASha256 is not null)
                {
                    throw new InvalidDataException(
                        $"Recoverable source slot '{slot.SlotId}' does not have the required preserved failure.");
                }
                try
                {
                    normalizedA = NormalizeJsonResponse(
                        sourceCase.PhaseA.RawResponse,
                        protocol.Limits.MaximumResponseBytes);
                    if (normalizedA.Transform != "outer-json-fence-removed")
                    {
                        recoveryError = "The response is not one exact outer JSON fence.";
                    }
                    else
                    {
                        phaseAResponse = ParsePhaseANormalized(
                            normalizedA.Payload,
                            projection,
                            protocol.Limits);
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException)
                {
                    recoveryError = exception.Message;
                }
            }
            else if (binding.Disposition == AdvisoryContinuationDisposition.Unavailable)
            {
                if (sourceCase.PhaseA.RawResponse is not null
                    || sourceCase.PhaseB.Status != AdvisoryCallStatus.Skipped
                    || sourceCase.SealedPhaseAPath is not null
                    || sourceCase.SealedPhaseASha256 is not null)
                {
                    throw new InvalidDataException(
                        $"Unavailable source slot '{slot.SlotId}' must remain without a retained response.");
                }
            }
            else
            {
                (normalizedA, phaseAResponse) = ValidateCompletedSourceCase(
                    protocol,
                    plan,
                    slot,
                    binding,
                    projection,
                    sourceCase);
            }

            contexts.Add(new ContinuationContext(
                slot,
                binding,
                projection,
                sourceCase,
                normalizedA,
                phaseAResponse,
                recoveryError));
        }

        return contexts;
    }

    private static void ValidateSourceCaseCommon(
        AdvisoryLlmProtocol protocol,
        AdvisoryAssessmentSlot slot,
        AdvisoryProjection projection,
        AdvisoryCaseResult sourceCase)
    {
        if (sourceCase.SchemaVersion != CurrentSchemaVersion
            || sourceCase.ProtocolId != protocol.ProtocolId
            || !FixedEquals(sourceCase.ProtocolFingerprint, protocol.ProtocolFingerprint)
            || sourceCase.SlotId != slot.SlotId
            || !FixedEquals(sourceCase.PacketFingerprint, slot.PacketFingerprint)
            || !FixedEquals(sourceCase.PacketFileSha256, slot.PacketFileSha256)
            || !FixedEquals(sourceCase.ProjectionSha256, projection.ProjectionSha256)
            || sourceCase.PhaseA.Model != protocol.PhaseAModel.Model
            || sourceCase.PhaseA.ModelVersion != protocol.PhaseAModel.ModelVersion
            || sourceCase.PhaseB.Model != protocol.PhaseBModel.Model
            || sourceCase.PhaseB.ModelVersion != protocol.PhaseBModel.ModelVersion
            || !FixedEquals(sourceCase.PhaseA.InputSha256, projection.ProjectionSha256))
        {
            throw new InvalidDataException($"Continuation source case '{slot.SlotId}' binding is invalid.");
        }
        var phaseAPromptSha = Sha256(Encoding.UTF8.GetBytes(BuildPhaseAPrompt(protocol, projection)));
        if (!FixedEquals(sourceCase.PhaseA.PromptSha256, phaseAPromptSha))
        {
            throw new InvalidDataException($"Continuation source Phase A prompt changed for '{slot.SlotId}'.");
        }
        ValidateRawResponseHash(sourceCase.PhaseA, $"source Phase A '{slot.SlotId}'");
        ValidateRawResponseHash(sourceCase.PhaseB, $"source Phase B '{slot.SlotId}'");
    }

    private static (AdvisoryNormalizedJson, AdvisoryPhaseAResponse) ValidateCompletedSourceCase(
        AdvisoryLlmProtocol protocol,
        AdvisoryContinuationPlan plan,
        AdvisoryAssessmentSlot slot,
        AdvisoryContinuationCaseBinding binding,
        AdvisoryProjection projection,
        AdvisoryCaseResult sourceCase)
    {
        if (sourceCase.PhaseA.Status != AdvisoryCallStatus.Succeeded
            || sourceCase.PhaseB.Status != AdvisoryCallStatus.Succeeded
            || sourceCase.PhaseA.RawResponse is null
            || sourceCase.PhaseB.RawResponse is null
            || sourceCase.PhaseAResponse is null
            || sourceCase.PhaseBResponse is null)
        {
            throw new InvalidDataException($"Completed source slot '{slot.SlotId}' is incomplete.");
        }

        var sealPath = ScopedSourcePath(plan.SourceRunRoot, slot.SlotId, "phase-a.sealed.json");
        var sealBytes = ReadBounded(sealPath, protocol.Limits.MaximumCaseArtifactBytes);
        if (!FixedEquals(Sha256(sealBytes), binding.SourcePhaseASealSha256!)
            || !FixedEquals(Sha256(sealBytes), sourceCase.SealedPhaseASha256!))
        {
            throw new InvalidDataException($"Completed source seal changed for '{slot.SlotId}'.");
        }
        RejectDuplicateProperties(sealBytes);
        var seal = JsonSerializer.Deserialize<AdvisoryPhaseASeal>(sealBytes, JsonOptions)
            ?? throw new InvalidDataException($"Completed source seal '{slot.SlotId}' was empty.");
        var normalizedA = NormalizeJsonResponse(
            sourceCase.PhaseA.RawResponse,
            protocol.Limits.MaximumResponseBytes);
        var parsedA = ParsePhaseANormalized(normalizedA.Payload, projection, protocol.Limits);
        if (seal.SchemaVersion != CurrentSchemaVersion
            || seal.ProtocolId != protocol.ProtocolId
            || !FixedEquals(seal.ProtocolFingerprint, protocol.ProtocolFingerprint)
            || seal.SlotId != slot.SlotId
            || !FixedEquals(seal.PacketFingerprint, slot.PacketFingerprint)
            || !FixedEquals(seal.ProjectionSha256, projection.ProjectionSha256)
            || !FixedEquals(seal.PromptSha256, sourceCase.PhaseA.PromptSha256)
            || !FixedEquals(seal.ResponseSha256!, sourceCase.PhaseA.ResponseSha256!)
            || !FixedEquals(Sha256(Serialize(seal.Call)), Sha256(Serialize(sourceCase.PhaseA)))
            || !FixedEquals(Sha256(Serialize(seal.Response)), Sha256(Serialize(parsedA)))
            || !FixedEquals(Sha256(Serialize(sourceCase.PhaseAResponse)), Sha256(Serialize(parsedA))))
        {
            throw new InvalidDataException($"Completed source seal binding is invalid for '{slot.SlotId}'.");
        }

        var packet = CalibrationPackets.ReadPacket(slot.PacketPath);
        var candidates = BuildCandidates(slot, packet, parsedA, projection, out var mapping);
        if (!FixedEquals(Sha256(Serialize(mapping)), Sha256(Serialize(sourceCase.CandidateMapping))))
        {
            throw new InvalidDataException($"Completed source candidate mapping changed for '{slot.SlotId}'.");
        }
        var phaseBPromptSha = Sha256(Encoding.UTF8.GetBytes(BuildPhaseBPrompt(protocol, projection, candidates)));
        var expectedInputSha = Sha256(Encoding.UTF8.GetBytes(
            projection.ProjectionSha256 + "\n" + sourceCase.SealedPhaseASha256 + "\n" + SerializeText(candidates)));
        var normalizedB = NormalizeJsonResponse(
            sourceCase.PhaseB.RawResponse,
            protocol.Limits.MaximumResponseBytes);
        var parsedB = ParsePhaseBNormalized(normalizedB.Payload, candidates, protocol.Limits);
        if (!FixedEquals(sourceCase.PhaseB.PromptSha256, phaseBPromptSha)
            || !FixedEquals(sourceCase.PhaseB.InputSha256, expectedInputSha)
            || !FixedEquals(Sha256(Serialize(sourceCase.PhaseBResponse)), Sha256(Serialize(parsedB))))
        {
            throw new InvalidDataException($"Completed source Phase B binding changed for '{slot.SlotId}'.");
        }

        return (normalizedA, parsedA);
    }

    private static void ValidateRawResponseHash(AdvisoryCallRecord call, string description)
    {
        if (call.RawResponse is null)
        {
            if (call.ResponseSha256 is not null)
            {
                throw new InvalidDataException($"{description} has a hash without retained bytes.");
            }
            return;
        }
        if (call.ResponseSha256 is null
            || !FixedEquals(
                Sha256(Encoding.UTF8.GetBytes(call.RawResponse)),
                call.ResponseSha256))
        {
            throw new InvalidDataException($"{description} retained response hash changed.");
        }
    }

    private static string ScopedSourcePath(string root, params string[] segments)
    {
        var path = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!IsSameOrChildPath(path, Path.GetFullPath(root)))
        {
            throw new InvalidDataException("Continuation source path escaped its declared root.");
        }
        return path;
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "."
            || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && relative != ".."
                && !Path.IsPathFullyQualified(relative));
    }

    private sealed record ContinuationContext(
        AdvisoryAssessmentSlot Slot,
        AdvisoryContinuationCaseBinding Binding,
        AdvisoryProjection Projection,
        AdvisoryCaseResult SourceCase,
        AdvisoryNormalizedJson? NormalizedPhaseA,
        AdvisoryPhaseAResponse? PhaseAResponse,
        string? RecoveryError);

    private sealed record RecoveredSeal(
        string Path,
        string Sha256,
        AdvisoryCallRecord Call);
}
