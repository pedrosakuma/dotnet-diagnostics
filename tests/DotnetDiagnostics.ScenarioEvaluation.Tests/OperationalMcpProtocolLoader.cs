using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpProtocolLoader
{
    public const int CurrentSchemaVersion = 4;
    public const int CurrentRunSchemaVersion = 4;
    public const int CurrentPilotSchemaVersion = 4;
    internal const string FrozenProtocolVersion = "4.0.0";
    internal const string FrozenProtocolFingerprint =
        "3f59d232f8a8353d4acab2fad66e3b04c9ce47743f42609b2bb2b03ca8fe78c3";
    internal const string FrozenInventoryFingerprint =
        "3cd676e89ff0caa57d8091335b955fd0975903691bd0d12911bf7e5af53f143e";
    internal const string FrozenProjectionFingerprint =
        "1e1c806d2ef04f826eddd1ad73df69a5cef129124a2025b2bb3ab707af7a01c4";
    internal const string PreservedV1FileSha256 =
        "c95d63300432d0d09e2b65bcab5b9fae0c2cfe8ee9ee2fef456714975b939891";
    internal const string PreservedV2FileSha256 =
        "02f06374fda5bcc9d2dcaadc0087af00e35d1b2f3b6679d49d9c559fc0cd1edd";
    internal const string PreservedV3FileSha256 =
        "00d4336e2dea90a2eff1b49cfa05919fac22cbf4a2ede60a25d604718a85e616";

    public static string DefaultPath
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-statistical-fields-ab-v4.protocol.json");

    public static string PreservedV1Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-ab-v1.protocol.json");

    public static string PreservedV2Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-ab-v2.protocol.json");

    public static string PreservedV3Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-wording-ab-v3.protocol.json");

    public static OperationalMcpProtocol LoadDefault()
    {
        var protocol = OperationalMcpJson.Read<OperationalMcpProtocol>(DefaultPath);
        Validate(protocol);
        ValidateFrozenIdentity(protocol);
        return protocol;
    }

    public static void ValidatePreservedV1()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV1Path)));
        if (!string.Equals(fileSha256, PreservedV1FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 1.4.0 file changed.");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(PreservedV1Path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || !string.Equals(
                root.GetProperty("protocolVersion").GetString(),
                "1.4.0",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 1.4.0 artifact changed.");
        }
    }

    public static void ValidatePreservedV2()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV2Path)));
        if (!string.Equals(fileSha256, PreservedV2FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 2.0.0 file changed.");
        }
    }

    public static void ValidatePreservedV3()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV3Path)));
        if (!string.Equals(fileSha256, PreservedV3FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 3.0.0 file changed.");
        }
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
            ? [OperationalMcpArm.NoStatisticalFields, OperationalMcpArm.CurrentIndicator]
            : [OperationalMcpArm.CurrentIndicator, OperationalMcpArm.NoStatisticalFields];
    }

    public static ScenarioManifest ToScenarioManifest(OperationalMcpCase testCase)
    {
        if (testCase.Family != OperationalMcpFamily.AllocationReproducibility)
        {
            throw new InvalidDataException(
                "Protocol 4.0.0 is allocation-only.");
        }

        const string kind = "allocation";
        const string view = "allocation-reproducibility";
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
                "The operational MCP preflight and byte budgets must be finite and bounded.");
        }

        var models = protocol.Models
            ?? throw new InvalidDataException(
                "The operational MCP protocol must freeze diagnosing and assessor models.");
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
                "The frozen prompt or transport-protocol fingerprint changed.");
        }

        if (protocol.DecisionRule.MaterialCostRelativeIncrease is <= 0 or > 1
            || protocol.DecisionRule.MaterialMcpPayloadByteIncrease < 1
            || protocol.DecisionRule.MaterialModelTransportByteIncrease < 1
            || protocol.DecisionRule.MaterialWallTimeSecondIncrease <= 0
            || protocol.DecisionRule.MaterialToolCallIncrease < 1)
        {
            throw new InvalidDataException(
                "The operational MCP decision rule must freeze positive material-cost thresholds.");
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
        if (!eligible.SetEquals([OperationalMcpFamily.AllocationReproducibility]))
        {
            throw new InvalidDataException(
                "The frozen v4 protocol must contain only allocation reproducibility.");
        }

        foreach (var entry in protocol.Inventory)
        {
            if (entry.Eligibility == OperationalMcpEligibility.Eligible)
            {
                RequireFingerprint(
                    entry.ExpectedControlToolDefinitionFingerprint,
                    $"{entry.Family}.expectedControlToolDefinitionFingerprint");
                RequireFingerprint(
                    entry.ExpectedCurrentToolDefinitionFingerprint,
                    $"{entry.Family}.expectedCurrentToolDefinitionFingerprint");
            }
            else if (entry.ExpectedControlToolDefinitionFingerprint is not null
                || entry.ExpectedCurrentToolDefinitionFingerprint is not null)
            {
                throw new InvalidDataException(
                    $"Excluded family '{entry.Family}' must not freeze model-visible tool definitions.");
            }
        }

        if (protocol.Projections.Count != eligible.Count
            || protocol.Projections.Any(projection =>
                !eligible.Contains(projection.Family)
                || string.IsNullOrWhiteSpace(projection.ControlVersion)
                || string.IsNullOrWhiteSpace(projection.CurrentVersion)
                || string.IsNullOrWhiteSpace(projection.ControlSource)
                || string.IsNullOrWhiteSpace(projection.CurrentSourceCommit)
                || projection.ResponseMutations.Count == 0
                || projection.ToolDefinitionMutations.Count == 0))
        {
            throw new InvalidDataException(
                "Each eligible family must have one deterministic control projection.");
        }

        foreach (var projection in protocol.Projections)
        {
            RequireFingerprint(
                projection.ExpectedControlFingerprint,
                $"{projection.Family}.expectedControlFingerprint");
            RequireFingerprint(
                projection.ExpectedCurrentFingerprint,
                $"{projection.Family}.expectedCurrentFingerprint");
            if (projection.ResponseMutations
                    .Concat(projection.ToolDefinitionMutations)
                    .Any(value =>
                        string.IsNullOrWhiteSpace(value.JsonPointer)
                        || value.Operation is not ("remove" or "replace")
                        || value.ExpectedKind is not ("string" or "object" or "array")
                        || (value.Operation == "replace"
                            && value.ControlValue is null)))
            {
                throw new InvalidDataException(
                    $"Control projection '{projection.Family}' contains an invalid mutation.");
            }

            var controlFingerprint =
                OperationalMcpResponseProjector.ProjectionFingerprint(
                    projection,
                    OperationalMcpArm.NoStatisticalFields);
            var currentFingerprint =
                OperationalMcpResponseProjector.ProjectionFingerprint(
                    projection,
                    OperationalMcpArm.CurrentIndicator);
            if (!string.Equals(
                    controlFingerprint,
                    projection.ExpectedControlFingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    currentFingerprint,
                    projection.ExpectedCurrentFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Control projection '{projection.Family}' fingerprint changed: "
                    + $"control={controlFingerprint}, current={currentFingerprint}.");
            }
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
                        value.Partition == OperationalMcpPartition.Development) != 2
                    || protocol.Cases.Any(value =>
                        value.Id.Contains("1338", StringComparison.Ordinal)
                        || value.Id.Contains("v1", StringComparison.Ordinal)
                        || value.Id.Contains("v2", StringComparison.Ordinal)
                        || value.Id.Contains("v3", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "The v4 protocol must contain fresh positive and healthy development cases and no prior protocol identity.");
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

        var developmentClasses = protocol.Cases
            .Where(value => value.Partition == OperationalMcpPartition.Development)
            .Select(value => value.CaseClass)
            .ToArray();
        if (developmentClasses.Length != 2
            || !developmentClasses.Contains(OperationalMcpCaseClass.Positive)
            || !developmentClasses.Contains(OperationalMcpCaseClass.HealthyControl))
        {
            throw new InvalidDataException(
                "The v4 development partition must contain one positive and one healthy allocation case.");
        }
    }

    private static void ValidateFrozenIdentity(OperationalMcpProtocol protocol)
    {
        var protocolFingerprint = ProtocolFingerprint(protocol);
        var inventoryFingerprint = InventoryFingerprint(protocol);
        var projectionFingerprint = ProjectionFingerprint(protocol);
        if (!string.Equals(
                protocol.ProtocolVersion,
                FrozenProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                protocolFingerprint,
                FrozenProtocolFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                inventoryFingerprint,
                FrozenInventoryFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                projectionFingerprint,
                FrozenProjectionFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The frozen operational MCP protocol identity changed. "
                + "Create a new protocol revision instead of modifying preserved protocols in place. "
                + $"Observed protocol={protocolFingerprint}, inventory={inventoryFingerprint}, "
                + $"projection={projectionFingerprint}.");
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
