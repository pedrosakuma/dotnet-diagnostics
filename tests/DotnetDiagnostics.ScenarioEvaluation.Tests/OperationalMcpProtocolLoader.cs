using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpProtocolLoader
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentRunSchemaVersion = 1;
    public const int CurrentPilotSchemaVersion = 1;
    internal const string FrozenProtocolVersion = "1.4.0";
    internal const string FrozenProtocolFingerprint =
        "1b2ef845bd52287994d822decfca71a073114c72a2c7b5602a70d8e570779879";
    internal const string FrozenInventoryFingerprint =
        "c01e7f13107854fdc4f8f9aae5d60d6b0cd4f33a3728b5027c0d88947e57947e";
    internal const string FrozenProjectionFingerprint =
        "ea96a3a44e6326df5e1d58b8a88695ec4dc5307a77d871a0cb2a190674a8010e";

    public static string DefaultPath
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-ab-v1.protocol.json");

    public static OperationalMcpProtocol LoadDefault()
    {
        var protocol = OperationalMcpJson.Read<OperationalMcpProtocol>(DefaultPath);
        Validate(protocol);
        ValidateFrozenIdentity(protocol);
        return protocol;
    }

    public static string Fingerprint<T>(T value)
        => Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(value, OperationalMcpJson.Options)));

    public static string ProtocolFingerprint(OperationalMcpProtocol protocol)
        => Fingerprint(protocol);

    public static string InventoryFingerprint(OperationalMcpProtocol protocol)
        => Fingerprint(protocol.Inventory);

    public static string ProjectionFingerprint(OperationalMcpProtocol protocol)
        => Fingerprint(protocol.Projections);

    public static IReadOnlyList<OperationalMcpArm> ArmOrder(
        OperationalMcpProtocol protocol,
        string caseId)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{protocol.ArmOrderSeed}\n{caseId}"));
        return (bytes[0] & 1) == 0
            ? [OperationalMcpArm.Control, OperationalMcpArm.Indicator]
            : [OperationalMcpArm.Indicator, OperationalMcpArm.Control];
    }

    public static ScenarioManifest ToScenarioManifest(OperationalMcpCase testCase)
    {
        var kind = testCase.Family == OperationalMcpFamily.AllocationReproducibility
            ? "allocation"
            : "cpu";
        var view = testCase.Family == OperationalMcpFamily.AllocationReproducibility
            ? "allocation-reproducibility"
            : "hotspot-variability";
        var queryRequirement = testCase.Family == OperationalMcpFamily.AllocationReproducibility
            ? "compare every returned handle in one query_snapshot call"
            : "query every returned handle with query_snapshot";
        var reportedSymptom =
            $"{testCase.ReportedSymptom} Frozen procedure: request exactly "
            + $"{testCase.RequiredCaptures} fresh collect_sample(kind=\"{kind}\") captures, then "
            + $"{queryRequirement} using view=\"{view}\" before returning a diagnosis. "
            + "Do not substitute manual comparison of individual capture summaries for the required query.";
        return new(
            ScenarioManifestLoader.CurrentSchemaVersion,
            $"operational-mcp-{testCase.Id}",
            "1.0.0",
            [ScenarioPlatform.Windows, ScenarioPlatform.Linux],
            reportedSymptom,
            testCase.ExpectedEvidence,
            new ScenarioWorkload(
                "Launch one fresh Release BadCodeSample for this arm.",
                "Drive only the frozen loopback workload while the model requests bounded MCP evidence.",
                "Cancel load and terminate the evaluator-owned target after the arm.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["endpoint"] = testCase.EndpointPath,
                    ["workers"] = testCase.Workers.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["operationalCaseId"] = testCase.Id,
                    ["requiredCaptures"] = testCase.RequiredCaptures.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                testCase.WarmupMilliseconds,
                18),
            new ScenarioBudget(240, 50),
            [
                new EvidenceInvariant(
                    "operational-mcp-evidence",
                    EvidenceInvariantKind.CounterComparison,
                    Metric: "operational.mcp.evidence",
                    Comparison: NumericComparison.GreaterThanOrEqual,
                    Threshold: 0),
            ],
            ["Sampled evidence is not exact resource usage."],
            testCase.ForbiddenCertainty,
            [testCase.ExpectedEvidence],
            ["existing MCP response fields"],
            testCase.UsefulNextSteps,
            testCase.ForbiddenCertainty,
            "bounded-evidence-only");
    }

    private static void Validate(OperationalMcpProtocol protocol)
    {
        if (protocol.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Operational MCP protocol schema {protocol.SchemaVersion} is unsupported.");
        }

        Require(protocol.ProtocolId, nameof(protocol.ProtocolId));
        Require(protocol.ProtocolVersion, nameof(protocol.ProtocolVersion));
        Require(protocol.ArmOrderSeed, nameof(protocol.ArmOrderSeed));
        Require(protocol.FailurePolicy, nameof(protocol.FailurePolicy));
        Require(protocol.ExpectedCatalogFingerprint, nameof(protocol.ExpectedCatalogFingerprint));
        RequireFingerprint(
            protocol.ExpectedCatalogFingerprint,
            nameof(protocol.ExpectedCatalogFingerprint));
        _ = protocol.Budgets.ToAgentBudget();
        if (protocol.Budgets.MaximumSuiteWallTimeSeconds is < 1 or > 14_400)
        {
            throw new InvalidDataException(
                "The operational MCP suite wall-time budget must be in [1, 14400] seconds.");
        }

        if (protocol.Budgets.MaximumAssessmentWallTimeSeconds is < 1 or > 300)
        {
            throw new InvalidDataException(
                "The operational MCP assessment wall-time budget must be in [1, 300] seconds.");
        }

        if (protocol.Inventory.Count == 0
            || protocol.Inventory.Select(entry => entry.Family).Distinct().Count() != protocol.Inventory.Count)
        {
            throw new InvalidDataException("The operational MCP inventory must contain one entry per family.");
        }

        var eligible = protocol.Inventory
            .Where(entry => entry.Eligibility == OperationalMcpEligibility.Eligible)
            .Select(entry => entry.Family)
            .ToHashSet();
        if (!eligible.SetEquals(
                [OperationalMcpFamily.AllocationReproducibility, OperationalMcpFamily.CpuHotspotCaptureProfiles]))
        {
            throw new InvalidDataException(
                "The frozen v1 protocol must contain exactly the two truthfully reachable MCP families.");
        }

        foreach (var entry in protocol.Inventory)
        {
            if (entry.Eligibility == OperationalMcpEligibility.Eligible)
            {
                RequireFingerprint(
                    entry.ExpectedToolDefinitionFingerprint,
                    $"{entry.Family}.expectedToolDefinitionFingerprint");
            }
            else if (entry.ExpectedToolDefinitionFingerprint is not null)
            {
                throw new InvalidDataException(
                    $"Excluded family '{entry.Family}' must not freeze model-visible tool definitions.");
            }
        }

        if (protocol.Projections.Count != eligible.Count
            || protocol.Projections.Any(projection =>
                !eligible.Contains(projection.Family)
                || string.IsNullOrWhiteSpace(projection.Version)
                || string.IsNullOrWhiteSpace(projection.NeutralSummary)
                || projection.JsonPointers.Count == 0
                || projection.JsonPointers.Any(pointer => pointer != "/data")))
        {
            throw new InvalidDataException(
                "Each eligible family must have one deterministic /data projection.");
        }

        if (protocol.Cases.Count == 0
            || protocol.Cases.Select(testCase => testCase.Id).Distinct(StringComparer.Ordinal).Count()
                != protocol.Cases.Count)
        {
            throw new InvalidDataException("Operational MCP cases must have unique ids.");
        }

        foreach (var testCase in protocol.Cases)
        {
            Require(testCase.Id, "case.id");
            Require(testCase.ReportedSymptom, "case.reportedSymptom");
            Require(testCase.EndpointPath, "case.endpointPath");
            Require(testCase.ExpectedEvidence, "case.expectedEvidence");
            if (!eligible.Contains(testCase.Family)
                || testCase.Workers is < 1 or > 8
                || testCase.WarmupMilliseconds is < 0 or > 10_000
                || testCase.RequiredCaptures is < 3 or > 4
                || testCase.ForbiddenCertainty.Count == 0
                || testCase.UsefulNextSteps.Count == 0)
            {
                throw new InvalidDataException($"Operational MCP case '{testCase.Id}' is invalid.");
            }
        }

        foreach (var partition in Enum.GetValues<OperationalMcpPartition>())
        {
            foreach (var family in eligible)
            {
                if (!protocol.Cases.Any(testCase =>
                        testCase.Partition == partition && testCase.Family == family))
                {
                    throw new InvalidDataException(
                        $"Partition '{partition}' is missing family '{family}'.");
                }
            }

            foreach (var family in eligible)
            {
                var captures = protocol.Cases
                    .Where(testCase => testCase.Family == family)
                    .Select(testCase => testCase.RequiredCaptures)
                    .Distinct()
                    .ToArray();
                if (captures.Length != 1)
                {
                    throw new InvalidDataException(
                        $"Eligible family '{family}' must use one frozen capture count so its model-visible tool fingerprint is stable.");
                }

                var heldoutClasses = protocol.Cases
                    .Where(testCase =>
                        testCase.Partition == OperationalMcpPartition.Heldout
                        && testCase.Family == family)
                    .Select(testCase => testCase.CaseClass)
                    .ToHashSet();
                if (!heldoutClasses.SetEquals(
                        Enum.GetValues<OperationalMcpCaseClass>()))
                {
                    throw new InvalidDataException(
                        $"Held-out family '{family}' must predeclare positive, healthy, competing, and degraded cases.");
                }
            }
        }
    }

    private static void ValidateFrozenIdentity(OperationalMcpProtocol protocol)
    {
        if (!string.Equals(
                protocol.ProtocolVersion,
                FrozenProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                ProtocolFingerprint(protocol),
                FrozenProtocolFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                InventoryFingerprint(protocol),
                FrozenInventoryFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                ProjectionFingerprint(protocol),
                FrozenProjectionFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The frozen operational MCP protocol identity changed. "
                + "Create a new protocol revision instead of modifying protocol 1.4.0 in place.");
        }
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Operational MCP field '{name}' is required.");
        }
    }

    private static void RequireFingerprint(string? value, string name)
    {
        if (value is null
            || value.Length != 64
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"Operational MCP field '{name}' must be a lowercase SHA-256 fingerprint.");
        }
    }
}
