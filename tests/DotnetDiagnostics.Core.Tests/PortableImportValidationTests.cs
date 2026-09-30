using System.Text.Json;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed class PortableImportValidationTests
{
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void MetadataEntryBudgetIsCheckedBeforeDeserialization(int count, bool allowed)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { entries = Enumerable.Repeat(new { entryId = new string('a', 32) }, count) });
        if (allowed) PortableMetadataPreflight.Check(bytes, 16, 16);
        else
        {
            var error = Assert.Throws<CaptureStoreException>(() => PortableMetadataPreflight.Check(bytes, 16, 16));
            Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
            Assert.Contains("MetadataArrayItems", error.Message);
        }
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void DecodedLabelByteBudgetIsExact(int bytes, bool allowed)
    {
        var value = new string('\u00e9', 128) + (bytes == 257 ? "a" : "");
        var json = JsonSerializer.SerializeToUtf8Bytes(new { label = value });
        if (allowed) PortableMetadataPreflight.Check(json, 16, 16);
        else
        {
            var error = Assert.Throws<CaptureStoreException>(() => PortableMetadataPreflight.Check(json, 16, 16));
            Assert.Contains("MetadataTextBytes", error.Message);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void CrossSnapshotGraphRejectsCyclesAcrossTheWholeCapture(int count, bool cycle)
    {
        var graph = new ulong[count];
        for (var i = 0; i < count - 1; i++) graph[i] = 1UL << (i + 1);
        if (cycle)
        {
            graph[^1] = 1;
            var error = Assert.Throws<CaptureStoreException>(() => PortableFrameValidation.ValidateCompositionGraph(graph));
            Assert.Equal(CaptureErrorCode.CorruptPackage, error.Code);
            Assert.Contains("Data.CompositionCycle", error.Message);
        }
        else PortableFrameValidation.ValidateCompositionGraph(graph);
    }

    [Fact]
    public void GraphCannotOverflowItsSixtyFourArtifactBitset()
    {
        var error = Assert.Throws<CaptureStoreException>(() => PortableFrameValidation.ValidateCompositionGraph(new ulong[65]));
        Assert.Contains("MaxArtifacts", error.Message);
    }
}
