using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalProtocolProvenance(
    string ProtocolSha256,
    string FrozenAtUtc,
    string SourceBaseCommit);

internal static class AllocationDirectionalProtocol
{
    private static readonly string[] RuntimeArtifactDirectories =
    [
        "src/DotnetDiagnostics.Mcp/bin/Release/net10.0",
        "samples/BadCodeSample/bin/Release/net10.0",
    ];

    public const string SourceBaseCommit =
        "4209ebc66b7c1c9c5cbe6fc80d901216585ffdb9";
    public const string TextHashCanonicalization = "utf8-no-bom-crlf";

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
        "src/DotnetDiagnostics.Core/UseCases/SamplerUseCases.cs",
        "src/DotnetDiagnostics.Mcp/Tools/CollectSampleTool.cs",
        "src/DotnetDiagnostics.Mcp/Tools/DiagnosticToolSampling.cs",
        "tests/DotnetDiagnostics.TestSupport/LiveSampleProcess.cs",
        "tests/DotnetDiagnostics.TestSupport/LiveSampleOptions.cs",
        "tests/DotnetDiagnostics.TestSupport/DiagnosticReadiness.cs",
        "tests/DotnetDiagnostics.TestSupport/SampleLocator.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalContracts.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalAttemptClassifier.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalHarness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalJson.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLedger.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLive.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalPlan.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProjection.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProtocol.cs",
    ];

    public static IReadOnlyList<string> FrozenRuntimeArtifactPaths
        => EnumerateRuntimeArtifactPaths();

    public static AllocationDirectionalProtocolProvenance ValidateFrozen()
    {
        var repositoryRoot = RepositoryRoot();
        var protocolPath = Path.Combine(
            repositoryRoot,
            "tests",
            "DotnetDiagnostics.ScenarioEvaluation.Tests",
            "Fixtures",
            "AllocationDirectional",
            "allocation-directional-baseline-current-v2.protocol.json");
        using var document = JsonDocument.Parse(File.ReadAllText(protocolPath));
        var root = document.RootElement;
        if (!string.Equals(
                root.GetProperty("status").GetString(),
                "frozen",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The allocation directional protocol must be frozen before empirical execution.");
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
                "The allocation directional protocol has no valid freeze timestamp.");
        }

        var sourceBaseCommit = root.GetProperty("sourceBaseCommit").GetString();
        if (!string.Equals(
                sourceBaseCommit,
                SourceBaseCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The allocation directional protocol source base commit does not match the reviewed prerequisite.");
        }

        var expectedHashes = root.GetProperty("frozenInputs")
            .GetProperty("sourceFilesSha256")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString()
                    ?? throw new InvalidDataException(
                        $"Frozen source hash '{property.Name}' is empty."),
                StringComparer.Ordinal);
        if (expectedHashes.Count != FrozenSourcePaths.Count
            || FrozenSourcePaths.Any(path => !expectedHashes.ContainsKey(path)))
        {
            throw new InvalidDataException(
                "The allocation directional frozen source inventory does not match the executable inventory.");
        }

        foreach (var relativePath in FrozenSourcePaths)
        {
            var actual = Sha256(Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.Equals(
                    actual,
                    expectedHashes[relativePath],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen source hash mismatch for '{relativePath}'.");
            }
        }

        var expectedRuntimeHashes = root.GetProperty("frozenInputs")
            .GetProperty("runtimeArtifactsSha256")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString()
                    ?? throw new InvalidDataException(
                        $"Frozen runtime hash '{property.Name}' is empty."),
                StringComparer.Ordinal);
        if (expectedRuntimeHashes.Count != FrozenRuntimeArtifactPaths.Count
            || FrozenRuntimeArtifactPaths.Any(path =>
                !expectedRuntimeHashes.ContainsKey(path)))
        {
            throw new InvalidDataException(
                "The allocation directional frozen runtime inventory does not match the executable inventory.");
        }

        foreach (var runtimePath in FrozenRuntimeArtifactPaths)
        {
            var runtimeActual = RawSha256(Path.Combine(
                repositoryRoot,
                runtimePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.Equals(
                    runtimeActual,
                    expectedRuntimeHashes[runtimePath],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen runtime hash mismatch for '{runtimePath}'.");
            }
        }

        return new(
            Sha256(protocolPath),
            frozenAtUtc,
            SourceBaseCommit);
    }

    internal static string Sha256(string path)
    {
        var bytes = CanonicalTextBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static int CanonicalByteCount(string path)
        => CanonicalTextBytes(path).Length;

    internal static string RawSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    internal static int RawByteCount(string path)
        => checked((int)new FileInfo(path).Length);

    internal static (int ByteCount, string Sha256) GitBlobFingerprint(
        string relativePath)
    {
        var startInfo = new ProcessStartInfo(
            "git",
            $"cat-file blob HEAD:{relativePath.Replace('\\', '/')}")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git cat-file.");
        using var bytes = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not read frozen git blob '{relativePath}': {error}");
        }

        return (
            checked((int)bytes.Length),
            Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
    }

    private static byte[] CanonicalTextBytes(string path)
    {
        var text = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root for the frozen allocation directional protocol.");
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
}
