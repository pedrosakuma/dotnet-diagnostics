using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class CalibrationProtocols
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumProtocolBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static CalibrationProtocol Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > MaximumProtocolBytes)
        {
            throw new InvalidDataException(
                $"Calibration protocol must be between 1 and {MaximumProtocolBytes} bytes.");
        }

        RejectDuplicateProperties(bytes);
        var protocol = JsonSerializer.Deserialize<CalibrationProtocol>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The calibration protocol was empty.");
        Validate(protocol);
        return protocol;
    }

    public static void Validate(CalibrationProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported calibration protocol schema version {protocol.SchemaVersion}.");
        }

        Require(protocol.ProtocolId, nameof(protocol.ProtocolId));
        Require(protocol.RubricId, nameof(protocol.RubricId));
        RequireSha256(protocol.ProtocolFingerprint, nameof(protocol.ProtocolFingerprint));
        RequireSha256(protocol.RubricFingerprint, nameof(protocol.RubricFingerprint));
        RequireSha256(protocol.Holdout.PrivateDefinitionSha256, nameof(protocol.Holdout.PrivateDefinitionSha256));
        var computedFingerprint = ComputeFingerprint(protocol);
        if (!FixedEquals(protocol.ProtocolFingerprint, computedFingerprint))
        {
            throw new InvalidDataException(
                $"The calibration protocol fingerprint is stale or invalid; computed {computedFingerprint}.");
        }

        ValidateBaseline(protocol);
        ValidateSlots(protocol);
    }

    public static void ValidateSourceReport(
        CalibrationProtocol protocol,
        CalibrationCaseDescriptor descriptor,
        AgentHarnessReport report)
    {
        Validate(protocol);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(report);
        var slot = ValidateBinding(protocol, descriptor);
        var expectedEvidenceKind = EvidenceKind(slot);
        if (report.EvidenceKind != expectedEvidenceKind)
        {
            throw new InvalidDataException(
                $"Report evidence kind must be '{expectedEvidenceKind}' for frozen slot '{slot.Id}'.");
        }

        if (slot.Kind == CalibrationProtocolSlotKind.AuthoredEditedReplay)
        {
            return;
        }

        if (slot.Budget != report.Budget)
        {
            throw new InvalidDataException($"Run budget does not match frozen slot '{slot.Id}'.");
        }

        var expected = protocol.Model;
        var provenance = report.Provenance;
        if (provenance.Provider != expected.Provider
            || provenance.Model != expected.Model
            || provenance.ModelVersion != expected.ModelVersion
            || provenance.ProductCommit != protocol.Product.Commit
            || provenance.ProductVersion != protocol.Product.Version
            || provenance.Transport != expected.Transport
            || provenance.TransportVersion != expected.TransportVersion)
        {
            throw new InvalidDataException(
                $"Run provenance does not match the frozen model/product baseline for slot '{slot.Id}'.");
        }

        if (slot.DefinitionVisibility == CalibrationDefinitionVisibility.Public
            && (provenance.WorkloadId != slot.WorkloadFamily
                || slot.PublicWorkloadParameters is null
                || !SameParameters(provenance.WorkloadConfiguration, slot.PublicWorkloadParameters)))
        {
            throw new InvalidDataException(
                $"Run workload does not match the frozen public definition for slot '{slot.Id}'.");
        }
    }

    public static void ValidatePacket(CalibrationProtocol protocol, CalibrationPacket packet)
    {
        Validate(protocol);
        ArgumentNullException.ThrowIfNull(packet);
        var slot = ValidateBinding(protocol, packet.Descriptor);
        if (packet.Generation.Kind != slot.ProvenanceKind)
        {
            throw new InvalidDataException(
                $"Packet provenance does not match frozen slot '{slot.Id}'.");
        }
        if (packet.Generation.EvidenceKind != EvidenceKind(slot))
        {
            throw new InvalidDataException(
                $"Packet evidence kind does not match frozen slot '{slot.Id}'.");
        }

        if (slot.Kind == CalibrationProtocolSlotKind.Live
            && (packet.Generation.Provider != protocol.Model.Provider
                || packet.Generation.Model != protocol.Model.Model
                || packet.Generation.ModelVersion != protocol.Model.ModelVersion
                || packet.Generation.ProductCommit != protocol.Product.Commit
                || packet.Generation.Transport != protocol.Model.Transport
                || packet.Generation.TransportVersion != protocol.Model.TransportVersion))
        {
            throw new InvalidDataException(
                $"Packet generation does not match the frozen baseline for slot '{slot.Id}'.");
        }
    }

    public static void ValidateCompletePacketSet(
        CalibrationProtocol protocol,
        IReadOnlyList<CalibrationPacket> packets)
    {
        Validate(protocol);
        ArgumentNullException.ThrowIfNull(packets);
        foreach (var packet in packets)
        {
            ValidatePacket(protocol, packet);
        }

        EnsureUnique(packets.Select(packet => packet.Descriptor.CaseId), "packet slot id");
        EnsureUnique(packets.Select(packet => packet.SourceRunId), "source run id");
        EnsureUnique(packets.Select(packet => packet.Descriptor.CaptureId), "capture id");
        var captureHashes = packets.Select(packet => packet.Descriptor.CaptureHash!).ToArray();
        EnsureUnique(captureHashes, "capture hash");

        var expectedSlots = protocol.Slots.Select(slot => slot.Id).Order(StringComparer.Ordinal);
        var actualSlots = packets.Select(packet => packet.Descriptor.CaseId).Order(StringComparer.Ordinal);
        if (!actualSlots.SequenceEqual(expectedSlots, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The packet set does not contain exactly one packet for every frozen protocol slot.");
        }
    }

    public static ScenarioManifest ResolveWorkload(
        CalibrationProtocol protocol,
        string slotId,
        string? privateDefinitionPath = null)
    {
        Validate(protocol);
        var slot = protocol.Slots.SingleOrDefault(value => value.Id == slotId)
            ?? throw new InvalidDataException($"Case '{slotId}' is not a frozen protocol slot.");
        if (slot.Kind != CalibrationProtocolSlotKind.Live)
        {
            throw new InvalidDataException($"Protocol slot '{slotId}' is not a live run.");
        }

        if (slot.DefinitionVisibility == CalibrationDefinitionVisibility.Public)
        {
            var manifest = ScenarioManifestLoader.LoadAll().SingleOrDefault(value =>
                value.Id == slot.WorkloadFamily)
                ?? throw new InvalidDataException(
                    $"Public workload '{slot.WorkloadFamily}' is not registered.");
            if (slot.PublicWorkloadParameters is null
                || !SameParameters(manifest.Workload.Parameters, slot.PublicWorkloadParameters))
            {
                throw new InvalidDataException(
                    $"Public workload parameters do not match frozen slot '{slot.Id}'.");
            }

            return manifest;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(privateDefinitionPath);
        var length = new FileInfo(privateDefinitionPath).Length;
        if (length is < 1 or > MaximumProtocolBytes)
        {
            throw new InvalidDataException(
                $"Private heldout definition must be between 1 and {MaximumProtocolBytes} bytes.");
        }

        var bytes = File.ReadAllBytes(privateDefinitionPath);
        if (!FixedEquals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                protocol.Holdout.PrivateDefinitionSha256))
        {
            throw new InvalidDataException(
                "The private heldout definition does not match its frozen SHA-256 commitment.");
        }

        RejectDuplicateProperties(bytes);
        var definition = JsonSerializer.Deserialize<CalibrationPrivateDefinition>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The private heldout definition was empty.");
        ValidatePrivateDefinition(protocol, definition);
        var privateSlot = definition.Slots.Single(value => value.Id == slot.Id);
        var baseManifest = ScenarioManifestLoader.LoadAll().SingleOrDefault(value =>
            value.Id == privateSlot.WorkloadFamily)
            ?? throw new InvalidDataException(
                $"Private workload family '{privateSlot.WorkloadFamily}' is not registered.");
        var resolved = baseManifest with
        {
            Version = privateSlot.WorkloadVersion,
            GroundTruth = privateSlot.WorkloadTruth,
            Workload = baseManifest.Workload with { Parameters = privateSlot.Parameters },
        };
        ScenarioManifestValidator.Validate(resolved);
        return resolved;
    }

    public static string ComputeFingerprint(CalibrationProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            protocol with { ProtocolFingerprint = string.Empty },
            JsonOptions);
        return ComputeCanonicalJsonFingerprint(bytes);
    }

    internal static string ComputeCanonicalJsonFingerprint(ReadOnlySpan<byte> json)
    {
        // Indented System.Text.Json output uses the platform newline. Raw carriage
        // returns cannot occur inside JSON strings, where they are escaped.
        var carriageReturnCount = json.Count((byte)'\r');
        if (carriageReturnCount == 0)
        {
            return Convert.ToHexStringLower(SHA256.HashData(json));
        }

        var normalized = new byte[json.Length - carriageReturnCount];
        var destination = 0;
        foreach (var value in json)
        {
            if (value != '\r')
            {
                normalized[destination++] = value;
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(normalized));
    }

    public static string ProtocolPath(params string[] segments)
        => segments.Aggregate(AppContext.BaseDirectory, Path.Combine);

    private static CalibrationProtocolSlot ValidateBinding(
        CalibrationProtocol protocol,
        CalibrationCaseDescriptor descriptor)
    {
        if (descriptor.ProtocolId != protocol.ProtocolId
            || descriptor.ProtocolFingerprint != protocol.ProtocolFingerprint
            || descriptor.RubricFingerprint != protocol.RubricFingerprint)
        {
            throw new InvalidDataException(
                "The case descriptor does not match the frozen protocol and rubric fingerprints.");
        }

        var slot = protocol.Slots.SingleOrDefault(value => value.Id == descriptor.CaseId)
            ?? throw new InvalidDataException(
                $"Case '{descriptor.CaseId}' is not a frozen protocol slot.");
        if (descriptor.Partition != slot.Partition
            || descriptor.ProvenanceKind != slot.ProvenanceKind)
        {
            throw new InvalidDataException(
                $"Case descriptor partition or provenance does not match frozen slot '{slot.Id}'.");
        }
        if (protocol.Holdout.RequireDistinctRunIdsAndCaptureHashes
            && string.IsNullOrWhiteSpace(descriptor.CaptureHash))
        {
            throw new InvalidDataException(
                $"Frozen slot '{slot.Id}' requires a committed capture hash.");
        }

        return slot;
    }

    private static void ValidateBaseline(CalibrationProtocol protocol)
    {
        Require(protocol.Model.Provider, nameof(protocol.Model.Provider));
        Require(protocol.Model.Model, nameof(protocol.Model.Model));
        Require(protocol.Model.ModelVersion, nameof(protocol.Model.ModelVersion));
        Require(protocol.Model.ModelVersionEvidence, nameof(protocol.Model.ModelVersionEvidence));
        Require(protocol.Model.Transport, nameof(protocol.Model.Transport));
        Require(protocol.Model.TransportVersion, nameof(protocol.Model.TransportVersion));
        Require(protocol.Product.Product, nameof(protocol.Product.Product));
        Require(protocol.Product.Version, nameof(protocol.Product.Version));
        RequireGitCommit(protocol.Product.Commit);
        if (protocol.FrozenAtUtc == DateTimeOffset.UnixEpoch)
        {
            throw new InvalidDataException("The protocol freeze timestamp is required.");
        }
    }

    private static void ValidateSlots(CalibrationProtocol protocol)
    {
        if (protocol.Limits.ExpectedLiveRuns < 1
            || protocol.Limits.ExpectedReplayRuns < 1
            || protocol.Limits.MaximumProviderTurns < 1)
        {
            throw new InvalidDataException("Protocol run and provider-turn limits must be positive.");
        }

        EnsureUnique(protocol.Slots.Select(slot => slot.Id), "slot id");
        var live = protocol.Slots.Where(slot => slot.Kind == CalibrationProtocolSlotKind.Live).ToArray();
        var replays = protocol.Slots.Where(
            slot => slot.Kind == CalibrationProtocolSlotKind.AuthoredEditedReplay).ToArray();
        if (live.Length != protocol.Limits.ExpectedLiveRuns
            || replays.Length != protocol.Limits.ExpectedReplayRuns)
        {
            throw new InvalidDataException("Protocol slots do not match the declared live/replay denominators.");
        }

        foreach (var slot in protocol.Slots)
        {
            ValidateSlot(slot);
        }

        if (live.Sum(slot => slot.Budget!.MaximumModelTurns) > protocol.Limits.MaximumProviderTurns)
        {
            throw new InvalidDataException("Live slots exceed the aggregate provider-turn budget.");
        }

        var heldout = protocol.Slots.Where(slot => slot.Partition == CalibrationPartition.Heldout).ToArray();
        if (heldout.Any(slot =>
                slot.Kind != CalibrationProtocolSlotKind.Live
                || slot.ProvenanceKind != CalibrationProvenanceKind.LiveModel
                || slot.DefinitionVisibility != CalibrationDefinitionVisibility.PrivateCommitted
                || slot.WorkloadFamily is not null
                || slot.PublicWorkloadParameters is not null
                || slot.EvidenceQualityMarkers.Count != 0))
        {
            throw new InvalidDataException(
                "Heldout slots must be opaque, privately committed live-model cases.");
        }

        var requiredMarkers = Enum.GetValues<CalibrationEvidenceQualityMarker>();
        var observedMarkers = protocol.Slots
            .Where(slot => slot.DefinitionVisibility == CalibrationDefinitionVisibility.Public)
            .SelectMany(slot => slot.EvidenceQualityMarkers)
            .ToHashSet();
        if (requiredMarkers.Any(marker => !observedMarkers.Contains(marker)))
        {
            throw new InvalidDataException("The protocol does not cover every required evidence-quality marker.");
        }

        EnsureUnique(protocol.Review.IndependentSlotIds, "independent-review slot id");
        var flagged = protocol.Slots
            .Where(slot => slot.RequiresIndependentReview)
            .Select(slot => slot.Id)
            .Order(StringComparer.Ordinal);
        var declared = protocol.Review.IndependentSlotIds.Order(StringComparer.Ordinal);
        if (protocol.Review.RequiredDistinctReviewersForIndependentSlots < 2
            || !protocol.Review.AdjudicateEveryDisagreement
            || !flagged.SequenceEqual(declared, StringComparer.Ordinal)
            || !protocol.Review.IndependentSlotIds.Any(id =>
                protocol.Slots.Single(slot => slot.Id == id).Partition == CalibrationPartition.Heldout)
            || !protocol.Review.IndependentSlotIds.Any(id =>
                protocol.Slots.Single(slot => slot.Id == id).Kind == CalibrationProtocolSlotKind.AuthoredEditedReplay))
        {
            throw new InvalidDataException(
                "Independent-review slots must be predeclared across heldout and replay cases with adjudication.");
        }

        if (!protocol.Holdout.KeepLabelsUnavailableDuringDevelopment
            || !protocol.Holdout.RequireFreshCaptureIds
            || !protocol.Holdout.RequireDistinctRunIdsAndCaptureHashes)
        {
            throw new InvalidDataException("The frozen holdout isolation controls must all be enabled.");
        }
    }

    private static void ValidateSlot(CalibrationProtocolSlot slot)
    {
        Require(slot.Id, nameof(slot.Id));
        Require(slot.WorkloadDefinition, nameof(slot.WorkloadDefinition));
        if (slot.Repetition < 1
            || !Enum.IsDefined(slot.Partition)
            || !Enum.IsDefined(slot.Kind)
            || !Enum.IsDefined(slot.ProvenanceKind)
            || !Enum.IsDefined(slot.DefinitionVisibility)
            || slot.EvidenceQualityMarkers.Any(marker => !Enum.IsDefined(marker))
            || slot.EvidenceQualityMarkers.Distinct().Count() != slot.EvidenceQualityMarkers.Count)
        {
            throw new InvalidDataException($"Protocol slot '{slot.Id}' is incomplete or invalid.");
        }

        if (slot.Kind == CalibrationProtocolSlotKind.Live)
        {
            if (slot.ProvenanceKind != CalibrationProvenanceKind.LiveModel || slot.Budget is null)
            {
                throw new InvalidDataException(
                    $"Live slot '{slot.Id}' requires live-model provenance and a budget.");
            }

            ValidateBudget(slot.Id, slot.Budget);
        }
        else if (slot.Partition != CalibrationPartition.Development
                 || slot.ProvenanceKind != CalibrationProvenanceKind.AuthoredEditedReplay
                 || slot.DefinitionVisibility != CalibrationDefinitionVisibility.Public
                 || slot.Budget is not null)
        {
            throw new InvalidDataException(
                $"Authored replay slot '{slot.Id}' must be a public development case without a model budget.");
        }

        if (slot.DefinitionVisibility == CalibrationDefinitionVisibility.Public
            && (string.IsNullOrWhiteSpace(slot.WorkloadFamily)
                || slot.PublicWorkloadParameters is not { Count: > 0 }
                || slot.EvidenceQualityMarkers.Count == 0))
        {
            throw new InvalidDataException(
                $"Public slot '{slot.Id}' requires a workload family and parameters.");
        }
    }

    private static void ValidatePrivateDefinition(
        CalibrationProtocol protocol,
        CalibrationPrivateDefinition definition)
    {
        if (definition.SchemaVersion != 1
            || definition.ProtocolId != protocol.ProtocolId
            || string.IsNullOrWhiteSpace(definition.Handling))
        {
            throw new InvalidDataException("The private heldout definition header is invalid.");
        }

        EnsureUnique(definition.Slots.Select(slot => slot.Id), "private heldout slot id");
        EnsureUnique(definition.Slots.Select(slot => slot.CaptureSeed), "private capture seed");
        var expected = protocol.Slots
            .Where(slot => slot.Partition == CalibrationPartition.Heldout)
            .Select(slot => slot.Id)
            .Order(StringComparer.Ordinal);
        var actual = definition.Slots.Select(slot => slot.Id).Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The private heldout definition does not contain exactly the frozen heldout slots.");
        }

        foreach (var slot in definition.Slots)
        {
            Require(slot.WorkloadFamily, nameof(slot.WorkloadFamily));
            Require(slot.WorkloadVersion, nameof(slot.WorkloadVersion));
            Require(slot.CaptureSeed, nameof(slot.CaptureSeed));
            Require(slot.WorkloadTruth, nameof(slot.WorkloadTruth));
            if (slot.Parameters.Count == 0
                || slot.Parameters.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                || slot.EvidenceQualityMarkers.Count == 0
                || slot.EvidenceQualityMarkers.Any(marker => !Enum.IsDefined(marker))
                || slot.EvidenceQualityMarkers.Distinct().Count() != slot.EvidenceQualityMarkers.Count)
            {
                throw new InvalidDataException(
                    $"Private heldout slot '{slot.Id}' is incomplete or invalid.");
            }

            var duplicatesDevelopment = protocol.Slots
                .Where(candidate =>
                    candidate.Partition == CalibrationPartition.Development
                    && candidate.Kind == CalibrationProtocolSlotKind.Live)
                .Any(candidate =>
                    candidate.WorkloadFamily == slot.WorkloadFamily
                    && candidate.PublicWorkloadParameters is not null
                    && SameParameters(candidate.PublicWorkloadParameters, slot.Parameters));
            if (duplicatesDevelopment)
            {
                throw new InvalidDataException(
                    $"Private heldout slot '{slot.Id}' duplicates a development workload and parameter set.");
            }
        }
    }

    private static void ValidateBudget(string slotId, AgentHarnessBudget budget)
    {
        if (budget.MaximumWallTimeSeconds is < 1 or > 45
            || budget.MaximumToolCalls is < 1 or > 4
            || budget.MaximumModelTurns is < 1 or > 4
            || budget.MaximumCaptureSeconds is < 1 or > 12
            || budget.MaximumInputTokens is < 1 or > 12_000
            || budget.MaximumOutputTokens is < 1 or > 2_000
            || budget.MaximumResponseBytes is < 1 or > 131_072
            || budget.MaximumArtifactBytes is < 1 or > 524_288
            || budget.MaximumEstimatedCostUsd is null or < 0)
        {
            throw new InvalidDataException($"Live slot '{slotId}' exceeds the frozen budget envelope.");
        }
    }

    private static bool SameParameters(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
           && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string EvidenceKind(CalibrationProtocolSlot slot)
        => slot.Kind == CalibrationProtocolSlotKind.Live
            ? "real-model"
            : "authored-edited-replay";

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var array = values.ToArray();
        if (array.Any(string.IsNullOrWhiteSpace)
            || array.Distinct(StringComparer.Ordinal).Count() != array.Length)
        {
            throw new InvalidDataException($"Every {description} must be non-empty and unique.");
        }
    }

    private static void Require(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{description} is required.");
        }
    }

    private static void RequireSha256(string value, string description)
    {
        Require(value, description);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidDataException($"{description} must be a lowercase SHA-256 value.");
        }
    }

    private static void RequireGitCommit(string value)
    {
        Require(value, "Product commit");
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidDataException("Product commit must be a full lowercase Git commit SHA.");
        }
    }

    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        Check(document.RootElement);

        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            $"Duplicate JSON property '{property.Name}' is not allowed.");
                    }

                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Check(item);
                }
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
