using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.Bytes;

internal static class FileSystemPathAssertions
{
    public static void ShouldMatchFileSystemPath(this string actual, string expected)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string.Equals(actual, expected, comparison).Should().BeTrue(
            "paths should match using the platform's file-system casing rules; actual path was {0} and expected path was {1}",
            actual,
            expected);
    }
}
