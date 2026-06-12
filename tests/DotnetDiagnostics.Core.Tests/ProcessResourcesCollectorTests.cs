using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ProcessResourcesCollectorTests
{
    [Theory]
    [InlineData("VmRSS:\t    1234 kB", 1234L * 1024)]
    [InlineData("VmRSS:\t10000000 kB", 10000000L * 1024)] // 8-digit value: no padding space before the number
    [InlineData("VmRSS:\t 999999999 kB", 999999999L * 1024)]
    public void ReadLinuxRssBytes_ParsesValueRegardlessOfPadding(string vmRssLine, long expectedBytes)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $"Name:\tsample\nVmPeak:\t   2048 kB\n{vmRssLine}\nThreads:\t4\n");
            var notes = new List<string>();

            var bytes = ProcessResourcesCollector.ReadLinuxRssBytes(path, notes);

            bytes.Should().Be(expectedBytes);
            notes.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinuxRssBytes_MissingField_ReturnsNullWithNote()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "Name:\tsample\nThreads:\t4\n");
            var notes = new List<string>();

            var bytes = ProcessResourcesCollector.ReadLinuxRssBytes(path, notes);

            bytes.Should().BeNull();
            notes.Should().ContainSingle().Which.Should().Contain("VmRSS");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
