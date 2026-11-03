using System.Security.Cryptography;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalV3Protocol
{
    public const string SourceBaseCommit =
        "9bf5701b879f7cdc85b93ec9efbf0e0cec76c548";
    public const string ProtocolId = "allocation-directional-baseline-current";
    public const string ProtocolVersion = "3.0.0";
    public const string FileName =
        "allocation-directional-baseline-current-v3.protocol.json";

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
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalHarness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV3Harness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalJson.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLedger.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLive.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalPlan.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProjection.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProtocol.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV3ProtocolDocument.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV3Protocol.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalV3Tests.cs",
    ];

    public static IReadOnlyList<string> PreservedArtifactPaths { get; } =
    [
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v1.protocol.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v1.windows-x64.evidence.execution.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v1.windows-x64.evidence.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v1.windows-x64.evidence.ledger.jsonl",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v1.windows-x64.evidence.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v2.protocol.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v2.windows-x64.evidence.execution.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v2.windows-x64.evidence.execution.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v2.windows-x64.evidence.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v2.windows-x64.evidence.ledger.jsonl",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-baseline-current-v2.windows-x64.evidence.report.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-throughput-calibration-v1.windows-x64.json",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional/allocation-directional-throughput-calibration-v1.windows-x64.report.json",
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
                "The allocation directional v3 protocol must be frozen before empirical execution.");
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
                "The allocation directional v3 protocol has no valid freeze timestamp.");
        }

        var sourceBaseCommit = root.GetProperty("sourceBaseCommit").GetString();
        if (!string.Equals(
                sourceBaseCommit,
                SourceBaseCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The allocation directional v3 source base does not match the reviewed prerequisites.");
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
                $"The allocation directional v3 frozen {kind} inventory does not match the executable inventory.");
        }

        foreach (var path in paths)
        {
            if (!string.Equals(
                    expected[path],
                    actualHash(path),
                    StringComparison.Ordinal))
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
                "Could not locate the repository root for the frozen allocation directional v3 protocol.");
    }
}
