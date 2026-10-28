using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Safety;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliStreamingQueryNavigationTests
{
    [Theory]
    [InlineData("object")]
    [InlineData("gcroot")]
    [InlineData("type-instances")]
    public void ObjectReadSafety_DumpOriginIsOfflineWarn_LiveOriginStaysHighAcknowledge(string view)
    {
        var dump = CliInvocationSafety.Resolve(
            CliStreamingQueryNavigation.CreateObjectReadSafetyOptions(HeapSnapshotOrigin.Dump, "h", view, null));
        dump.RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        dump.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.Warn);
        dump.TargetImpact.Should().NotContain(TargetImpact.PtraceAttach).And.NotContain(TargetImpact.ProcessSuspension);

        var store = new MemoryDiagnosticHandleStore();
        var artifact = new HeapSnapshotArtifact(HeapSnapshotOrigin.Live, Environment.ProcessId, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, new("CoreCLR", "10.0", "x64", false, 1), new(0, 0, 0, 0, 0, 0, 0), [], []);
        var handle = store.Register(Environment.ProcessId, "heap-snapshot", artifact, TimeSpan.FromMinutes(1));
        var live = CliInvocationSafety.Resolve(
            CliStreamingQueryNavigation.CreateObjectReadSafetyOptions(HeapSnapshotOrigin.Live, handle.Id, view, null), store);
        live.RiskLevel.Should().Be(InvocationRiskLevel.High);
        live.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.Acknowledge);
        live.TargetImpact.Should().Contain(TargetImpact.PtraceAttach).And.Contain(TargetImpact.ProcessSuspension);
    }

    [Theory]
    [InlineData("0x1", 0x1UL)]
    [InlineData("0X1F", 0x1FUL)]
    [InlineData("0x7f1234567890", 0x7f1234567890UL)]
    [InlineData("0xfedcba9876543210", 0xfedcba9876543210UL)]
    [InlineData("0xffffffffffffffff", ulong.MaxValue)]
    public void TryParseHexAddress_AcceptsExact64BitHex(string text, ulong expected)
    {
        CliStreamingQueryNavigation.TryParseHexAddress(text, out var address).Should().BeTrue();
        address.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0x0")]
    [InlineData("0x0000")]
    [InlineData("4096")]
    [InlineData("-0x1")]
    [InlineData(" 0x1")]
    [InlineData("0x1 ")]
    [InlineData("0x+1")]
    [InlineData("0x1_0")]
    [InlineData("0xg")]
    [InlineData("0x10000000000000000")]
    [InlineData("0x1ffffffffffffffff")]
    public void TryParseHexAddress_RejectsEverythingElse(string? text)
    {
        CliStreamingQueryNavigation.TryParseHexAddress(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("{}", "Absent")]
    [InlineData("{\"address\":null}", "Absent")]
    [InlineData("{\"address\":\"0xff\"}", "Valid")]
    [InlineData("{\"address\":255}", "Invalid")]
    [InlineData("{\"address\":9007199254740993}", "Invalid")]
    [InlineData("{\"address\":1.5}", "Invalid")]
    [InlineData("{\"address\":true}", "Invalid")]
    [InlineData("{\"address\":[\"0xff\"]}", "Invalid")]
    [InlineData("{\"address\":\"255\"}", "Invalid")]
    public void TryReadAddress_NeverCoercesNumbers(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        CliStreamingQueryNavigation.TryReadAddress(document.RootElement, out _).ToString().Should().Be(expected);
    }

    [Fact]
    public void Projections_KeepAddressesAboveJavaScriptSafeIntegerAsHexStrings()
    {
        const ulong big = 0xfedcba9876543210UL;
        var inspection = new HeapObjectInspection(big, "T", 24, "SOH", "Gen0")
        {
            Fields = [new HeapObjectField("next", "T", "-") { ObjectAddress = ulong.MaxValue }],
            ArraySample = [new HeapArrayElement(0, "T", "-") { ObjectAddress = big + 1 }],
        };
        var root = new HeapGcRootInspection(big, "T", [new RetentionFrame("T", ulong.MaxValue) { RootKind = "Strong" }], false);

        var json = JsonSerializer.Serialize(new
        {
            objectDetails = CliStreamingQueryNavigation.ProjectObject(inspection),
            gcRoot = CliStreamingQueryNavigation.ProjectGcRoot(root),
        });
        using var document = JsonDocument.Parse(json);

        var details = document.RootElement.GetProperty("objectDetails");
        details.GetProperty("address").GetString().Should().Be("0xfedcba9876543210");
        details.GetProperty("fields")[0].GetProperty("objectAddress").GetString().Should().Be("0xffffffffffffffff");
        details.GetProperty("arraySample")[0].GetProperty("objectAddress").GetString().Should().Be("0xfedcba9876543211");
        document.RootElement.GetProperty("gcRoot").GetProperty("chain")[0].GetProperty("objectAddress").GetString()
            .Should().Be("0xffffffffffffffff");
    }

    [Fact]
    public void ObjectReadViews_ExcludeObjectSize()
    {
        CliStreamingQueryNavigation.ObjectReadViews.Should().BeEquivalentTo("object", "gcroot");
    }

    [Fact]
    public void TypeInstanceCursor_IsStableAndBoundToHandleSnapshotAndType()
    {
        var snapshot = new HeapSnapshotArtifact(
            HeapSnapshotOrigin.Dump,
            42,
            DateTimeOffset.Parse("2026-10-26T12:00:00Z"),
            TimeSpan.Zero,
            new("CoreCLR", "10.0", "x64", false, 1),
            new(0, 0, 0, 0, 0, 0, 0),
            [],
            []);
        var type = new TypeIdentity("Sample.Cache")
        {
            ModuleName = "Sample.dll",
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };

        var cursor = CliStreamingQueryNavigation.CreateTypeInstanceCursor("h1", type, snapshot, 0xfedcba9876543210);

        CliStreamingQueryNavigation.TryParseTypeInstanceCursor(cursor, "h1", type, snapshot, out var address)
            .Should().BeTrue();
        address.Should().Be(0xfedcba9876543210);
        CliStreamingQueryNavigation.TryParseTypeInstanceCursor(cursor, "h2", type, snapshot, out _)
            .Should().BeFalse();
        CliStreamingQueryNavigation.TryParseTypeInstanceCursor(
            cursor, "h1", type with { TypeFullName = "Sample.Other" }, snapshot, out _).Should().BeFalse();
    }

    [Fact]
    public void TypeInstanceProjection_KeepsExactAddressAsHexString()
    {
        var json = JsonSerializer.Serialize(CliStreamingQueryNavigation.ProjectTypeInstance(
            new HeapTypeInstance(0xfedcba9876543210, 48, "Generation2", "Generation2")));

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("address").GetString().Should().Be("0xfedcba9876543210");
    }
}
