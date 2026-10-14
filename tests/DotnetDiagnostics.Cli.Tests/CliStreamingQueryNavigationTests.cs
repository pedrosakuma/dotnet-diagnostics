using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliStreamingQueryNavigationTests
{
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
}
