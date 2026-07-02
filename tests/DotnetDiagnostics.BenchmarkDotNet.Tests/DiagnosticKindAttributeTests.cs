using System.Reflection;
using DotnetDiagnostics.BenchmarkDotNet;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public class DiagnosticKindAttributeTests
{
    [Fact]
    public void ParsesSingleKind_WithDefaultDuration()
    {
        var attr = new DiagnosticKindAttribute("gc");

        attr.Kinds.Should().Be("gc");
        attr.DurationSeconds.Should().Be(5);
        attr.KindList.Should().ContainSingle().Which.Should().Be("gc");
    }

    [Fact]
    public void ParsesMultipleKinds_TrimmingAndDroppingEmpties()
    {
        var attr = new DiagnosticKindAttribute(" gc , contention ,, threadpool ", durationSeconds: 8);

        attr.DurationSeconds.Should().Be(8);
        attr.KindList.Should().Equal("gc", "contention", "threadpool");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Throws_OnNullOrWhitespaceKinds(string? kinds)
    {
        var act = () => new DiagnosticKindAttribute(kinds!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EnumOverload_SingleKind_MapsToToken_WithDefaultDuration()
    {
        var attr = new DiagnosticKindAttribute(BenchmarkDiagnosticKind.Gc);

        attr.Kinds.Should().Be("gc");
        attr.KindList.Should().ContainSingle().Which.Should().Be("gc");
        attr.DurationSeconds.Should().Be(DiagnosticKindAttribute.DefaultDurationSeconds);
    }

    [Fact]
    public void EnumOverload_MultipleKinds_MapToTokensInOrder()
    {
        var attr = new DiagnosticKindAttribute(
            BenchmarkDiagnosticKind.Gc,
            BenchmarkDiagnosticKind.Contention,
            BenchmarkDiagnosticKind.ThreadPool);

        attr.KindList.Should().Equal("gc", "contention", "threadpool");
        attr.Kinds.Should().Be("gc,contention,threadpool");
    }

    [Fact]
    public void EnumOverload_DurationSeconds_OverridableViaNamedArg()
    {
        var attr = new DiagnosticKindAttribute(BenchmarkDiagnosticKind.Gc) { DurationSeconds = 8 };

        attr.DurationSeconds.Should().Be(8);
    }

    [Fact]
    public void EnumOverload_Empty_Throws()
    {
        var act = () => new DiagnosticKindAttribute(Array.Empty<BenchmarkDiagnosticKind>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EnumOverload_ProducesSameTokensAsStringOverload()
    {
        var viaEnum = new DiagnosticKindAttribute(BenchmarkDiagnosticKind.Gc, BenchmarkDiagnosticKind.Db);
        var viaString = new DiagnosticKindAttribute("gc,db");

        viaEnum.KindList.Should().Equal(viaString.KindList);
    }

    [Fact]
    public void EveryEnumMember_MapsToASupportedKind()
    {
        foreach (var kind in Enum.GetValues<BenchmarkDiagnosticKind>())
        {
            var token = BenchmarkDiagnosticKinds.Token(kind);
            InProcessDiagnosticCollector.IsSupported(token)
                .Should().BeTrue($"enum member {kind} maps to token '{token}' which must be a supported kind");
        }
    }

    [Fact]
    public void EnumTokens_CoverEverySupportedKind()
    {
        var enumTokens = Enum.GetValues<BenchmarkDiagnosticKind>()
            .Select(BenchmarkDiagnosticKinds.Token)
            .ToHashSet(StringComparer.Ordinal);

        enumTokens.Should().BeEquivalentTo(InProcessDiagnosticCollector.SupportedKinds);
    }

    // Compile-time proof that the headline attribute syntax — a params enum array combined with the
    // named DurationSeconds argument — is legal and behaves as documented in the README.
    [DiagnosticKind(BenchmarkDiagnosticKind.Gc, BenchmarkDiagnosticKind.Contention, DurationSeconds = 7)]
    private static void EnumAttributeUsageProbe()
    {
    }

    [Fact]
    public void EnumAttributeUsage_AppliesAndReadsBackViaReflection()
    {
        var attr = typeof(DiagnosticKindAttributeTests)
            .GetMethod(nameof(EnumAttributeUsageProbe), BindingFlags.NonPublic | BindingFlags.Static)!
            .GetCustomAttribute<DiagnosticKindAttribute>()!;

        attr.KindList.Should().Equal("gc", "contention");
        attr.DurationSeconds.Should().Be(7);
    }
}
