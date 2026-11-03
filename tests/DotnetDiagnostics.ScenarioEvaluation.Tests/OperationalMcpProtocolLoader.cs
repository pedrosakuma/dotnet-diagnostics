using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpProtocolLoader
{
    private static readonly HashSet<string> TextSourceExtensions =
    [
        ".cs",
        ".csproj",
        ".c",
        ".editorconfig",
        ".h",
        ".json",
        ".md",
        ".proj",
        ".props",
        ".py",
        ".resx",
        ".targets",
        ".txt",
        ".xml",
        ".yml",
        ".yaml",
    ];

    public const int CurrentSchemaVersion = 8;
    public const int CurrentRunSchemaVersion = 8;
    public const int CurrentPilotSchemaVersion = 8;
    internal static readonly string DefaultOutputRoot =
        Path.Combine("artifacts", "operational-mcp-pilot-v8");
    internal const string FrozenProtocolVersion = "8.0.0";
    internal const string FrozenProtocolFingerprint =
        "be4235d3f7063a8752e5c6dfc9be4d9d0494bcee1ad8a1e303b1252620003888";
    internal const string FrozenInventoryFingerprint =
        "dbb9dcaf01f047e69302aa31e6a6638ab711e0fc9e09ed2bf1b713221af36259";
    internal const string FrozenProjectionFingerprint =
        "cfe5de2db20b2c592a55d3f7a04a7e5df4cb03f09e55567722a4b0c8936339d2";
    internal const string PreservedV1FileSha256 =
        "c95d63300432d0d09e2b65bcab5b9fae0c2cfe8ee9ee2fef456714975b939891";
    internal const string PreservedV2FileSha256 =
        "02f06374fda5bcc9d2dcaadc0087af00e35d1b2f3b6679d49d9c559fc0cd1edd";
    internal const string PreservedV3FileSha256 =
        "00d4336e2dea90a2eff1b49cfa05919fac22cbf4a2ede60a25d604718a85e616";
    internal const string PreservedV4FileSha256 =
        "bb95d62708d42de0c84af290ca8c27f5220412e71622bd1d1e0d8374e8c41db6";
    internal const string PreservedV5FileSha256 =
        "4ed218ae50b857bdb5ebe664762b8a8faebc3cee52175dbaa2f26b4c71886e94";
    internal const string PreservedV6FileSha256 =
        "74a2878e5736e4b6c69f9cd2506c2c4ec055109f73943b7a3c77f2b04d4ba080";
    internal const string PreservedV7FileSha256 =
        "5325b3f59416ada38ee6569f75e93ed0650cf940e2f0a50e15b69f5fef78accd";

    public static string DefaultPath
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-field-isolation-v8.protocol.json");

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

    public static string PreservedV4Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-statistical-fields-ab-v4.protocol.json");

    public static string PreservedV5Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-measurement-quality-v5.protocol.json");

    public static string PreservedV6Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-field-isolation-v6.protocol.json");

    public static string PreservedV7Path
        => Path.Combine(
            AppContext.BaseDirectory,
            "OperationalMcp",
            "operational-mcp-allocation-field-isolation-v7.protocol.json");

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

    public static void ValidatePreservedV4()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV4Path)));
        if (!string.Equals(fileSha256, PreservedV4FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 4.0.0 file changed.");
        }
    }

    public static void ValidatePreservedV5()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV5Path)));
        if (!string.Equals(fileSha256, PreservedV5FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 5.0.0 file changed.");
        }
    }

    public static void ValidatePreservedV6()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV6Path)));
        if (!string.Equals(fileSha256, PreservedV6FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 6.0.0 file changed.");
        }
    }

    public static void ValidatePreservedV7()
    {
        var fileSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(PreservedV7Path)));
        if (!string.Equals(fileSha256, PreservedV7FileSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The preserved operational MCP protocol 7.0.0 file changed.");
        }
    }

    public static OperationalMcpProtocol LoadPreservedV7()
    {
        ValidatePreservedV7();
        return OperationalMcpJson.Read<OperationalMcpProtocol>(PreservedV7Path);
    }

    public static IReadOnlyList<string> PreservedProtocolPaths =>
    [
        PreservedV1Path,
        PreservedV2Path,
        PreservedV3Path,
        PreservedV4Path,
        PreservedV5Path,
        PreservedV6Path,
        PreservedV7Path,
    ];

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
            hash.AppendData(SourceBytesForFingerprint(path));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] SourceBytesForFingerprint(string path)
        => NormalizeSourceBytesForFingerprint(
            Path.GetExtension(path),
            File.ReadAllBytes(path));

    internal static byte[] NormalizeSourceBytesForFingerprint(
        string extension,
        byte[] bytes)
    {
        if (!TextSourceExtensions.Contains(extension))
        {
            return bytes;
        }

        using var normalized = new MemoryStream(bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '\r'
                && index + 1 < bytes.Length
                && bytes[index + 1] == '\n')
            {
                continue;
            }

            normalized.WriteByte(bytes[index]);
        }

        return normalized.ToArray();
    }

    public static IReadOnlyList<OperationalMcpArm> ArmOrder(
        OperationalMcpProtocol protocol,
        string caseId)
    {
        var arms = Enum.GetValues<OperationalMcpArm>();
        var seed = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{protocol.ArmOrderSeed}\n{caseId}"));
        for (var index = arms.Length - 1; index > 0; index--)
        {
            var offset = (arms.Length - 1 - index) * sizeof(uint);
            var random = BitConverter.ToUInt32(seed, offset);
            var selected = (int)(random % (uint)(index + 1));
            (arms[index], arms[selected]) = (arms[selected], arms[index]);
        }

        return arms;
    }

    public static ScenarioManifest ToScenarioManifest(OperationalMcpCase testCase)
    {
        if (testCase.Family != OperationalMcpFamily.AllocationReproducibility)
        {
            throw new InvalidDataException(
                "Protocol 8.0.0 is allocation-only.");
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
        if (protocol.FrozenAtUtc == default)
        {
            throw new InvalidDataException(
                "The operational MCP protocol must have a reviewed freeze timestamp.");
        }
        Require(protocol.ExpectedCatalogFingerprint, nameof(protocol.ExpectedCatalogFingerprint));
        RequireFingerprint(
            protocol.ExpectedCatalogFingerprint,
            nameof(protocol.ExpectedCatalogFingerprint));
        if (protocol.AssessorAdditiveProperties.Count == 0
            || protocol.AssessorAdditiveProperties.Any(value =>
                string.IsNullOrWhiteSpace(value.Key)
                || value.Value != "stringArray")
            || protocol.AssessorAdditiveProperties.Keys.Any(value =>
                value is "claims"
                    or "uncertainty"
                    or "uncertaintyRationale"
                    or "unsupportedCertaintyFindings"
                    or "usefulNextSteps"
                    or "unhelpfulNextSteps"
                    or "summary"))
        {
            throw new InvalidDataException(
                "Protocol 8 must explicitly declare typed, non-canonical additive assessor properties.");
        }
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
                "The frozen v8 protocol must contain only allocation reproducibility.");
        }

        foreach (var entry in protocol.Inventory)
        {
            if (entry.Eligibility == OperationalMcpEligibility.Eligible)
            {
                if (entry.ExpectedToolDefinitionFingerprints is null
                    || !entry.ExpectedToolDefinitionFingerprints.Keys.ToHashSet()
                        .SetEquals(Enum.GetValues<OperationalMcpArm>()))
                {
                    throw new InvalidDataException(
                        $"Eligible family '{entry.Family}' must freeze one tool-definition fingerprint per arm.");
                }

                foreach (var (arm, fingerprint) in entry.ExpectedToolDefinitionFingerprints)
                {
                    RequireFingerprint(
                        fingerprint,
                        $"{entry.Family}.expectedToolDefinitionFingerprints.{arm}");
                }
            }
            else if (entry.ExpectedToolDefinitionFingerprints is not null)
            {
                throw new InvalidDataException(
                    $"Excluded family '{entry.Family}' must not freeze model-visible tool definitions.");
            }
        }

        if (protocol.Projections.Count != eligible.Count
            || protocol.Projections.Any(projection =>
                !eligible.Contains(projection.Family)
                || !projection.Versions.Keys.ToHashSet()
                    .SetEquals(Enum.GetValues<OperationalMcpArm>())
                || !projection.Sources.Keys.ToHashSet()
                    .SetEquals(Enum.GetValues<OperationalMcpArm>()
                        .Where(arm => arm != OperationalMcpArm.CompleteCurrent))
                || projection.Versions.Values.Any(string.IsNullOrWhiteSpace)
                || projection.Sources.Values.Any(string.IsNullOrWhiteSpace)
                || string.IsNullOrWhiteSpace(projection.CurrentSourceCommit)
                || projection.GuardPackage is null
                || projection.CandidateResponsePointers.Count == 0
                || projection.ResponseMutations.Count == 0
                || projection.GuardPackage.ToolDefinitionMutations.Count == 0))
        {
            throw new InvalidDataException(
                "Each eligible family must have one deterministic six-arm projection.");
        }

        foreach (var projection in protocol.Projections)
        {
            RequireFingerprint(
                projection.GuardPackage.ExpectedFingerprint,
                $"{projection.Family}.guardPackage.expectedFingerprint");
            RequireFingerprint(
                projection.ExpectedMutationFingerprint,
                $"{projection.Family}.expectedMutationFingerprint");
            var guardFingerprint =
                OperationalMcpResponseProjector.GuardPackageFingerprint(
                    projection.GuardPackage);
            if (!string.Equals(
                    guardFingerprint,
                    projection.GuardPackage.ExpectedFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' guard package fingerprint changed: "
                    + $"expected={projection.GuardPackage.ExpectedFingerprint}, "
                    + $"observed={guardFingerprint}.");
            }

            var mutationFingerprint =
                OperationalMcpResponseProjector.MutationInventoryFingerprint(projection);
            if (!string.Equals(
                    mutationFingerprint,
                    projection.ExpectedMutationFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' mutation inventory fingerprint changed: "
                    + $"expected={projection.ExpectedMutationFingerprint}, "
                    + $"observed={mutationFingerprint}.");
            }

            if (projection.CandidateResponsePointers.Any(pointer =>
                    string.IsNullOrWhiteSpace(pointer)
                    || pointer[0] != '/')
                || projection.CandidateResponsePointers.Distinct(StringComparer.Ordinal).Count()
                    != projection.CandidateResponsePointers.Count)
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' candidate response pointers are invalid.");
            }

            var mutationPointers = projection.ResponseMutations
                .Select(value => value.JsonPointer)
                .Where(pointer => !string.Equals(
                    pointer,
                    "/data/method",
                    StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            if (!mutationPointers.SetEquals(projection.CandidateResponsePointers))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' candidate response pointers "
                    + "must exactly match the field-only mutation pointers.");
            }

            if (projection.CandidateResponsePointers.Any(pointer =>
                    IsSameOrAncestor(pointer, "/summary")
                    || IsSameOrAncestor(pointer, "/data/captures")
                    || IsSameOrAncestor(pointer, "/data/assumptions")
                    || IsSameOrAncestor(pointer, "/data/limitations")))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' candidate response pointers "
                    + "must not cover shared guard or raw-capture evidence.");
            }

            if (!projection.ExpectedFingerprints.Keys.ToHashSet()
                    .SetEquals(Enum.GetValues<OperationalMcpArm>()))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' must freeze one fingerprint per arm.");
            }

            foreach (var (arm, fingerprint) in projection.ExpectedFingerprints)
            {
                RequireFingerprint(
                    fingerprint,
                    $"{projection.Family}.expectedFingerprints.{arm}");
            }

            if (projection.ResponseMutations.Any(value =>
                        string.IsNullOrWhiteSpace(value.JsonPointer)
                        || value.Operation is not ("remove" or "replace" or "select")
                        || value.ExpectedKind is not ("string" or "number" or "object" or "array")
                        || value.Arms.Count == 0
                        || value.Arms.Distinct().Count() != value.Arms.Count
                        || (value.Operation is "replace" or "select"
                            && (value.Values is null
                                || !value.Values.Keys.ToHashSet().SetEquals(value.Arms)))))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' contains an invalid mutation.");
            }

            if (projection.GuardPackage.ToolDefinitionMutations.Any(value =>
                    string.IsNullOrWhiteSpace(value.JsonPointer)
                    || value.ExpectedKind is not ("string" or "number" or "object" or "array")))
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' contains an invalid guard mutation.");
            }

            var fingerprintMismatches = new List<string>();
            foreach (var arm in Enum.GetValues<OperationalMcpArm>())
            {
                var fingerprint =
                    OperationalMcpResponseProjector.ProjectionFingerprint(
                        projection,
                        arm);
                if (!string.Equals(
                        fingerprint,
                        projection.ExpectedFingerprints[arm],
                        StringComparison.Ordinal))
                {
                    fingerprintMismatches.Add(
                        $"{arm}: expected={projection.ExpectedFingerprints[arm]}, observed={fingerprint}");
                }
            }

            if (fingerprintMismatches.Count > 0)
            {
                throw new InvalidDataException(
                    $"Projection '{projection.Family}' fingerprints changed: "
                    + string.Join("; ", fingerprintMismatches));
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
                        || value.Id.Contains("v3", StringComparison.Ordinal)
                        || value.Id.Contains("v4", StringComparison.Ordinal)
                        || value.Id.Contains("v5", StringComparison.Ordinal)
                        || value.Id.Contains("v6", StringComparison.Ordinal)
                        || value.Id.Contains("v7", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "The v8 protocol must contain fresh positive and healthy development cases and no prior protocol identity.");
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
                "The v8 development partition must contain one positive and one healthy allocation case.");
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

    private static bool IsSameOrAncestor(string candidate, string protectedPointer)
        => string.Equals(candidate, protectedPointer, StringComparison.Ordinal)
            || protectedPointer.StartsWith(
                candidate.EndsWith('/')
                    ? candidate
                    : candidate + "/",
                StringComparison.Ordinal);

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
