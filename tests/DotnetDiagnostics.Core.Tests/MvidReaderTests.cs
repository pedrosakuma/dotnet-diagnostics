using System.IO;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class MvidReaderTests
{
    [Fact]
    public void TryRead_ReturnsModuleVersionId_ForManagedAssembly()
    {
        var reader = new MvidReader();
        var mvid = reader.TryRead(typeof(MvidReader).Assembly.Location);

        mvid.Should().NotBeNull();
    }

    [Fact]
    public void TryRead_EvictsOldestEntry_WhenCapacityIsExceeded()
    {
        var reader = new MvidReader(capacity: 2);
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "mvid-reader-cache-tests");
        Directory.CreateDirectory(fixtureDir);
        var sourceAssembly = typeof(MvidReader).Assembly.Location;
        var copies = Enumerable.Range(0, 3)
            .Select(i => Path.Combine(fixtureDir, $"copy-{i}.dll"))
            .ToArray();

        try
        {
            foreach (var copy in copies)
            {
                File.Copy(sourceAssembly, copy, overwrite: true);
                reader.TryRead(copy).Should().NotBeNull();
            }

            reader.CacheEntryCount.Should().Be(2);
            reader.TryRead(copies[0]).Should().NotBeNull("evicted entries must still be readable when reloaded from disk");
            reader.CacheEntryCount.Should().Be(2);
        }
        finally
        {
            foreach (var copy in copies)
            {
                try { if (File.Exists(copy)) File.Delete(copy); } catch { }
            }

            try { if (Directory.Exists(fixtureDir)) Directory.Delete(fixtureDir); } catch { }
        }
    }
}
