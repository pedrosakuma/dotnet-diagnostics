using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationThroughputNormalizationProtocol
{
    public const string SourceBaseCommit =
        "56c2ef4a9eb296beb1f64d33b8f47ccf693250a3";
    public const string PrerequisiteV3Merge =
        "6777e0a05ca68a5a55e1d6191860ff36012db450";
    public const string ProtocolId =
        "allocation-throughput-normalization-falsifier";
    public const string ProtocolVersion = "1.0.0";
    public const string FileName =
        "allocation-throughput-normalization-falsifier-v1.protocol.json";

    private static readonly string[] RuntimeArtifactDirectories =
    [
        "src/DotnetDiagnostics.Mcp/bin/Release/net10.0",
        "samples/BadCodeSample/bin/Release/net10.0",
    ];

    public static IReadOnlyList<string> FrozenSourcePaths { get; } =
    [
        "global.json",
        "Directory.Build.props",
        "Directory.Packages.props",
        "samples/BadCodeSample/AllocationShapeWorkload.cs",
        "samples/BadCodeSample/BadCodeSample.csproj",
        "samples/BadCodeSample/Program.cs",
        "src/DotnetDiagnostics.Core/CpuSampling/AllocationSample.cs",
        "src/DotnetDiagnostics.Core/CpuSampling/EventPipeAllocationSampler.cs",
        "src/DotnetDiagnostics.Core/UseCases/AllocationSampleFailureClassifier.cs",
        "src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs",
        "src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs",
        "src/DotnetDiagnostics.Mcp/Tools/DiagnosticToolSampling.cs",
        "tests/DotnetDiagnostics.TestSupport/LiveSampleProcess.cs",
        "tests/DotnetDiagnostics.TestSupport/LiveSampleOptions.cs",
        "tests/DotnetDiagnostics.TestSupport/DiagnosticReadiness.cs",
        "tests/DotnetDiagnostics.TestSupport/SampleLocator.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalContracts.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalAttemptClassifier.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalCalibration.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalBoundedCalibration.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalJson.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLedger.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLive.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalPlan.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProjection.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProtocol.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationThroughputNormalizationHarness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationThroughputNormalizationLive.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationThroughputNormalizationPlan.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationThroughputNormalizationProtocol.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationThroughputNormalizationProtocolDocument.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationThroughputNormalizationTests.cs",
    ];

    public static IReadOnlyList<string> PreservedArtifactPaths { get; } =
    [
        .. AllocationDirectionalV4Protocol.PreservedArtifactPaths,
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v4.protocol.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v4.windows-x64.evidence.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v4.windows-x64.evidence.ledger.jsonl",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v4.windows-x64.evidence.report.json",
    ];

    public static IReadOnlyList<string> FrozenRuntimeArtifactPaths
        => EnumerateRuntimeArtifactPaths();

    public static AllocationDirectionalProtocolProvenance ValidateFrozen()
    {
        var protocolPath = Path.Combine(
            RepositoryRoot(),
            "tests",
            "DotnetDiagnostics.ScenarioEvaluation.Tests",
            "Fixtures",
            "AllocationDirectional",
            FileName);
        using var document = JsonDocument.Parse(File.ReadAllText(protocolPath));
        var root = document.RootElement;
        if (!string.Equals(
                root.GetProperty("status").GetString(),
                "frozen",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The throughput-normalization protocol must be frozen before empirical execution.");
        }

        var frozenAtUtc = root.GetProperty("frozenAtUtc").GetString();
        if (frozenAtUtc is null
            || !DateTimeOffset.TryParse(
                frozenAtUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new InvalidDataException(
                "The throughput-normalization protocol lacks a valid freeze timestamp.");
        }

        if (!string.Equals(
                root.GetProperty("sourceBaseCommit").GetString(),
                SourceBaseCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The throughput-normalization source base does not match merged prerequisite #1389.");
        }

        var inventory = root.GetProperty("inventory");
        var capture = root.GetProperty("capture");
        var selectors = root.GetProperty("selectors");
        if (inventory.GetProperty("cohortCount").GetInt32()
                != AllocationThroughputNormalizationCapturePlan.Cohorts.Count
            || inventory.GetProperty("totalPairs").GetInt32()
                != AllocationThroughputNormalizationCapturePlan.TotalPairs
            || inventory.GetProperty("liveMcpSides").GetInt32()
                != AllocationThroughputNormalizationCapturePlan.LiveMcpSides
            || capture.GetProperty("durationSeconds").GetInt32()
                != AllocationThroughputNormalizationCapturePlan
                    .CaptureDurationSeconds
            || capture.GetProperty("armDelayMilliseconds").GetInt32()
                != AllocationThroughputNormalizationCapturePlan
                    .ArmDelayMilliseconds
            || capture.GetProperty("workloadMilliseconds").GetInt32()
                != AllocationThroughputNormalizationCapturePlan
                    .WorkloadMilliseconds
            || capture.GetProperty("allocationEventOperatingLimit").GetInt64()
                != AllocationThroughputNormalizationCapturePlan
                    .AllocationEventOperatingLimit
            || !selectors.GetProperty("pairFirst").GetBoolean()
            || selectors.GetProperty("minimumValidPairs").GetInt32()
                != AllocationDirectionalBounds.RequiredPairs
            || selectors.GetProperty("minimumInlierPairs").GetInt32()
                != AllocationDirectionalCalibrationBounds.MinimumInlierPairs
            || selectors.GetProperty("practicalRelativeFloor").GetDecimal()
                != AllocationDirectionalCalibrationBounds
                    .PracticalRelativeEquivalenceFloor
            || selectors.GetProperty("minimumThroughputMultiplier").GetDecimal()
                != AllocationDirectionalCalibrationBounds
                    .MinimumThroughputMultiplier)
        {
            throw new InvalidDataException(
                "The frozen throughput-normalization inventory, geometry, or selector contract drifted.");
        }

        AllocationThroughputNormalizationCapturePlan.ValidateConstruction();
        ValidateHashes(
            root.GetProperty("frozenInputs").GetProperty("sourceFilesSha256"),
            FrozenSourcePaths,
            path => AllocationDirectionalProtocol.Sha256(Path.Combine(
                RepositoryRoot(),
                path.Replace('/', Path.DirectorySeparatorChar))),
            "source");
        ValidateHashes(
            root.GetProperty("frozenInputs").GetProperty("runtimeArtifactsSha256"),
            FrozenRuntimeArtifactPaths,
            path => AllocationDirectionalProtocol.RawSha256(Path.Combine(
                RepositoryRoot(),
                path.Replace('/', Path.DirectorySeparatorChar))),
            "runtime");
        ValidateHashes(
            root.GetProperty("frozenInputs").GetProperty(
                "preservedArtifactsSha256"),
            PreservedArtifactPaths,
            path => AllocationDirectionalProtocol.Sha256(Path.Combine(
                RepositoryRoot(),
                path.Replace('/', Path.DirectorySeparatorChar))),
            "preserved artifact");

        return new(
            AllocationDirectionalProtocol.Sha256(protocolPath),
            frozenAtUtc,
            SourceBaseCommit);
    }

    private static void ValidateHashes(
        JsonElement element,
        IReadOnlyList<string> paths,
        Func<string, string> actualHash,
        string kind)
    {
        var expected = element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString()
                ?? throw new InvalidDataException(
                    $"Frozen {kind} hash '{property.Name}' is empty."),
            StringComparer.Ordinal);
        if (expected.Count != paths.Count
            || paths.Any(path => !expected.ContainsKey(path)))
        {
            throw new InvalidDataException(
                $"The frozen throughput-normalization {kind} inventory does not match the executable inventory.");
        }

        foreach (var path in paths)
        {
            if (!string.Equals(
                    expected[path],
                    actualHash(path),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen throughput-normalization {kind} hash mismatch for '{path}'.");
            }
        }
    }

    private static string[] EnumerateRuntimeArtifactPaths()
    {
        var root = RepositoryRoot();
        return RuntimeArtifactDirectories
            .SelectMany(relativeDirectory =>
                Directory.EnumerateFiles(
                        Path.Combine(
                            root,
                            relativeDirectory.Replace(
                                '/',
                                Path.DirectorySeparatorChar)),
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Where(path =>
                        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(
                            ".deps.json",
                            StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(
                            ".runtimeconfig.json",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(path =>
                        $"{relativeDirectory}/{Path.GetFileName(path)}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(
                   directory.FullName,
                   "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root for the throughput-normalization protocol.");
    }
}
