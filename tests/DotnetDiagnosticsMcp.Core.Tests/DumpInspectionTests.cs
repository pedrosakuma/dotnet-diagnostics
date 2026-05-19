using DotnetDiagnosticsMcp.Core.Dump;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Unit-level wiring tests for the dump-inspection contract (issue #12). The heavy lifting
/// (ClrMD heap walk against a real <c>.dmp</c>) is covered by <see cref="LiveCoreClrProcessTests"/>;
/// these tests pin the record shapes, defaults, and the <see cref="TypeIdentity"/> handoff payload
/// that lets the agent pivot to <c>dotnet-assembly-mcp</c>.
/// </summary>
public class DumpInspectionTests
{
    [Fact]
    public void DumpInspection_DefaultsRetentionPathsAndWarnings_ToNull()
    {
        // The basic inspection should compose without retention paths.
        var runtime = new DumpRuntimeInfo("coreclr", "10.0.0", "x64", IsServerGC: false, HeapCount: 1);
        var heap = new DumpHeapSummary(1024, 100, 200, 300, 400, 24, 2048);
        var top = new[] { new TypeStat("MyApp.Leak", "MyApp.dll", 10, 800, 78.12) };

        var inspection = new DumpInspection(
            FilePath: "/tmp/x.dmp",
            FileSizeBytes: 4096,
            Runtime: runtime,
            Heap: heap,
            TopTypesByBytes: top,
            TopTypesByInstances: top);

        inspection.RetentionPaths.Should().BeNull();
        inspection.Warnings.Should().BeNull();
        inspection.TopTypesByBytes.Should().HaveCount(1);
    }

    [Fact]
    public void TypeStat_CarriesTypeIdentity_ForHandoff()
    {
        // The (mvid, metadataToken) pair is what dotnet-assembly-mcp consumes to resolve
        // a TypeDefinition without ambiguous name parsing.
        var mvid = Guid.NewGuid();
        var identity = new TypeIdentity("MyApp.Leak")
        {
            ModuleName = "MyApp.dll",
            ModulePath = "/app/MyApp.dll",
            ModuleVersionId = mvid,
            MetadataToken = 0x02000042,
        };

        var stat = new TypeStat(
            TypeFullName: "MyApp.Leak",
            ModuleName: "MyApp.dll",
            InstanceCount: 10,
            TotalBytes: 800,
            TotalBytesPercent: 78.12,
            Identity: identity);

        stat.Identity.Should().NotBeNull();
        stat.Identity!.ModuleVersionId.Should().Be(mvid);
        stat.Identity.MetadataToken.Should().Be(0x02000042);
        stat.Identity.TypeFullName.Should().Be("MyApp.Leak");
    }

    [Fact]
    public void DumpInspectionOptions_HasSensibleDefaults()
    {
        // Defaults: 20 top types, no retention paths (expensive), 8-deep chains when on.
        var opts = new DumpInspectionOptions();
        opts.TopTypes.Should().Be(20);
        opts.IncludeRetentionPaths.Should().BeFalse();
        opts.RetentionPathLimit.Should().Be(8);
    }
}
