using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalProtocolProvenance(
    string ProtocolSha256,
    string FrozenAtUtc,
    string SourceBaseCommit);

internal static class AllocationDirectionalProtocol
{
    public const string SourceBaseCommit =
        "402c3d2fefc8bb094b7341e5482559ce953f9395";
    public const string TextHashCanonicalization = "utf8-no-bom-crlf";

    public static IReadOnlyList<string> FrozenSourcePaths { get; } =
    [
        "global.json",
        "samples/BadCodeSample/AllocationShapeWorkload.cs",
        "samples/BadCodeSample/Program.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalContracts.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalHarness.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalJson.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalLive.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalPlan.cs",
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/AllocationDirectionalProtocol.cs",
    ];

    public static AllocationDirectionalProtocolProvenance ValidateFrozen()
    {
        var repositoryRoot = RepositoryRoot();
        var protocolPath = Path.Combine(
            repositoryRoot,
            "tests",
            "DotnetDiagnostics.ScenarioEvaluation.Tests",
            "Fixtures",
            "AllocationDirectional",
            "allocation-directional-baseline-current-v1.protocol.json");
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

    private static byte[] CanonicalTextBytes(string path)
    {
        var text = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
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
                "Could not locate the repository root for the frozen allocation directional protocol.");
    }
}
