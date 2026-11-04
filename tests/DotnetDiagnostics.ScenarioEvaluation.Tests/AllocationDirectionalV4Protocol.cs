using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalV4Protocol
{
    public const string SourceBaseCommit =
        "9a46d91e160126253e962432ca37f77ccae3f646";
    public const string ProtocolId = "allocation-directional-baseline-current";
    public const string ProtocolVersion = "4.0.0";
    public const string FileName =
        "allocation-directional-baseline-current-v4.protocol.json";

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
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalHarness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV3Harness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV4Harness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalJson.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLedger.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLive.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalPlan.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProjection.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProtocol.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV4ProtocolDocument.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV4Protocol.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV4Tests.cs",
    ];

    public static IReadOnlyList<string> PreservedArtifactPaths { get; } =
    [
        .. AllocationDirectionalV3Protocol.PreservedArtifactPaths,
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v3.protocol.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v3.windows-x64.evidence.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v3.windows-x64.evidence.ledger.jsonl",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v3.windows-x64.evidence.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v1.windows-x64.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v1.windows-x64.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v1.windows-x64.started.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v2.windows-x64.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v2.windows-x64.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v2.windows-x64.started.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v3.windows-x64.started.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v4.windows-x64.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v4.windows-x64.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v4.windows-x64.started.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v5.windows-x64.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v5.windows-x64.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v5.windows-x64.started.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v6.windows-x64.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v6.windows-x64.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-bounded-calibration-v6.windows-x64.started.json",
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
                "The allocation directional v4 protocol must be frozen before empirical execution.");
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
                "The allocation directional v4 protocol has no valid freeze timestamp.");
        }

        if (!string.Equals(
                root.GetProperty("sourceBaseCommit").GetString(),
                SourceBaseCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The allocation directional v4 source base does not match the reviewed bounded calibration prerequisite.");
        }

        var capture = root.GetProperty("capture");
        if (capture.GetProperty("durationSeconds").GetInt32()
                != AllocationDirectionalV4CapturePlan.CaptureDurationSeconds
            || capture.GetProperty("armDelayMilliseconds").GetInt32()
                != AllocationDirectionalV4CapturePlan.ArmDelayMilliseconds
            || capture.GetProperty("workloadMilliseconds").GetInt32()
                != AllocationDirectionalV4CapturePlan.WorkloadMilliseconds
            || capture.GetProperty("burstPauseMilliseconds").GetInt32() != 1
            || capture.GetProperty("burstPauseEveryOperations").GetInt32() != 8
            || capture.GetProperty("allocationEventOperatingLimit").GetInt64()
                != AllocationDirectionalV4CapturePlan.AllocationEventOperatingLimit)
        {
            throw new InvalidDataException(
                "The allocation directional v4 frozen geometry or operating limit does not match bounded calibration v6.");
        }

        var selectors = root.GetProperty("selectors");
        if (!selectors.GetProperty("pairFirst").GetBoolean()
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
                "The allocation directional v4 frozen selector contract does not match the reviewed pair-first practical-equivalence gate.");
        }

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
            root.GetProperty("frozenInputs").GetProperty("preservedArtifactsSha256"),
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
                $"The allocation directional v4 frozen {kind} inventory does not match the executable inventory.");
        }

        foreach (var path in paths)
        {
            if (!string.Equals(expected[path], actualHash(path), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen {kind} hash mismatch for '{path}'.");
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
                        || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
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
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root for the frozen allocation directional v4 protocol.");
    }
}
