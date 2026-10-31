using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpProtocolLoader
{
    public const int CurrentSchemaVersion = 2;
    public const int CurrentRunSchemaVersion = 2;
    public const int CurrentPilotSchemaVersion = 2;
    internal const string FrozenProtocolVersion = "2.0.0";
    internal const string FrozenProtocolFingerprint =
        "df1db2c83d7d71967fa582153f49e88b0201017f3f4486602f2da49a2604c43d";
    internal const string FrozenInventoryFingerprint =
        "5502f0bfd8617e77faf3315fe5e8087976f8667d08c353b01093c4be95463f67";
    internal const string FrozenProjectionFingerprint =
        "9ab6488c85a32d5fa89426411068866c6966832e41cfa899d2c348e8ced47696";
    internal const string PreservedV1FileSha256 =
        "c95d63300432d0d09e2b65bcab5b9fae0c2cfe8ee9ee2fef456714975b939891";

    public static string DefaultPath
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-ab-v2.protocol.json");

    public static string PreservedV1Path
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

    public static OperationalMcpProtocol LoadPreservedV1()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV1Path)));
        if (!string.Equals(fileSha256, PreservedV1FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 1.4.0 file changed.");
        }

        var protocol = OperationalMcpJson.Read<OperationalMcpProtocol>(PreservedV1Path);
        if (protocol.SchemaVersion != 1
            || !string.Equals(protocol.ProtocolVersion, "1.4.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 1.4.0 artifact changed.");
        }

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

    public static string ProductSourceFingerprint()
    {
        var root = RepositoryRoot();
        var paths = new List<string>
        {
            Path.Combine(root, "Directory.Build.props"),
            Path.Combine(root, "Directory.Packages.props"),
            Path.Combine(root, "global.json"),
        };
        foreach (var directory in new[]
                 {
                     Path.Combine(root, "src", "DotnetDiagnostics.Core"),
                     Path.Combine(root, "src", "DotnetDiagnostics.Mcp"),
                 })
        {
            paths.AddRange(Directory.EnumerateFiles(
                directory,
                "*",
                SearchOption.AllDirectories).Where(path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.OrderBy(path =>
                     Path.GetRelativePath(root, path),
                     StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

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
        var reportedSymptom =
            $"{testCase.ReportedSymptom} The evaluator has already executed exactly "
            + $"{testCase.RequiredCaptures} fresh collect_sample(kind=\"{kind}\") captures and "
            + $"the frozen family-specific query_snapshot(view=\"{view}\") sequence. "
            + "Interpret the retained evidence first; use the same bounded tools only when a "
            + "follow-up can add evidence rather than repeat the predeclared sequence.";
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

        if (protocol.Budgets.MaximumPreflightWallTimeSeconds is < 1 or > 300
            || protocol.Budgets.MaximumMcpPayloadBytes is < 1 or > 1_048_576
            || protocol.Budgets.MaximumModelTransportOutputBytes is < 1 or > 1_048_576
            || protocol.Budgets.MaximumAssessmentInputBytes is < 1 or > 1_048_576
            || protocol.Budgets.MaximumAssessmentTransportOutputBytes is < 1 or > 1_048_576)
        {
            throw new InvalidDataException(
                "The operational MCP v2 preflight and byte budgets must be finite and bounded.");
        }

        var models = protocol.Models
            ?? throw new InvalidDataException(
                "The operational MCP v2 protocol must freeze diagnosing and assessor models.");
        Require(models.Provider, "models.provider");
        Require(models.DiagnosingModel, "models.diagnosingModel");
        Require(models.AssessorModel, "models.assessorModel");
        Require(models.ContextTier, "models.contextTier");
        RequireFingerprint(models.DiagnosingPromptSha256, "models.diagnosingPromptSha256");
        RequireFingerprint(models.AssessorPromptSha256, "models.assessorPromptSha256");
        RequireFingerprint(models.TransportProtocolSha256, "models.transportProtocolSha256");
        if (!string.Equals(
                models.DiagnosingPromptSha256,
                BlindedAgentHarness.SystemPromptSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                models.AssessorPromptSha256,
                OperationalMcpAssessor.SystemPromptSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                models.TransportProtocolSha256,
                BlindedAgentHarness.TransportProtocolSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The frozen v2 prompt or transport-protocol fingerprint changed.");
        }

        RequireFingerprint(
            protocol.ExpectedProductFingerprint,
            nameof(protocol.ExpectedProductFingerprint));
        var productFingerprint = ProductSourceFingerprint();
        if (!string.Equals(
                productFingerprint,
                protocol.ExpectedProductFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The frozen product source fingerprint changed: expected "
                + $"{protocol.ExpectedProductFingerprint}, observed {productFingerprint}.");
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
                || testCase.CaptureDurationSeconds is < 1 or > 6
                || testCase.CaptureTopN is < 1 or > 20
                || testCase.QueryTopN is < 1 or > 16
                || testCase.RequiredCaptures * testCase.CaptureDurationSeconds
                    > protocol.Budgets.MaximumCaptureSeconds
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

                if (protocol.Cases.Count(value =>
                        value.Partition == OperationalMcpPartition.Development) != eligible.Count
                    || protocol.Cases.Any(value =>
                        value.Id.Contains("1338", StringComparison.Ordinal)
                        || value.Id.Contains("v1", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "The v2 protocol must contain one fresh development case per eligible family and no v1 case identity.");
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new FileNotFoundException(
                "Could not locate the repository root for product fingerprinting.");
    }
}
