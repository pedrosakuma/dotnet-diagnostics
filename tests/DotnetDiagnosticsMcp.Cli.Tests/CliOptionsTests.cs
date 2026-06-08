using DotnetDiagnosticsMcp.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_BareCommand_SetsCommand()
    {
        var options = CliOptions.Parse(new[] { "processes" }, out var error);

        error.Should().BeNull();
        options.Should().NotBeNull();
        options!.Command.Should().Be("processes");
        options.Pid.Should().BeNull();
        options.Json.Should().BeFalse();
        options.Help.Should().BeFalse();
    }

    [Fact]
    public void Parse_PidAndJson_AreCaptured()
    {
        var options = CliOptions.Parse(new[] { "capabilities", "--pid", "1234", "--json" }, out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("capabilities");
        options.Pid.Should().Be(1234);
        options.Json.Should().BeTrue();
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--pid")]
    public void Parse_PidShortAndLongForms_AreEquivalent(string flag)
    {
        var options = CliOptions.Parse(new[] { "capabilities", flag, "42" }, out var error);

        error.Should().BeNull();
        options!.Pid.Should().Be(42);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_HelpFlag_SetsHelp(string flag)
    {
        var options = CliOptions.Parse(new[] { flag }, out var error);

        error.Should().BeNull();
        options!.Help.Should().BeTrue();
    }

    [Fact]
    public void Parse_PidMissingValue_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "capabilities", "--pid" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_PidNonInteger_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "capabilities", "--pid", "abc" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("expects an integer");
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "processes", "--bogus" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("Unknown option");
    }

    [Fact]
    public void Parse_SecondPositional_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "processes", "capabilities" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("Only one command is accepted");
    }

    [Fact]
    public void Parse_NoArgs_ReturnsEmptyOptions()
    {
        var options = CliOptions.Parse(Array.Empty<string>(), out var error);

        error.Should().BeNull();
        options!.Command.Should().BeNull();
        options.Help.Should().BeFalse();
    }

    [Fact]
    public void Parse_ComparePathsAndSave_AreCaptured()
    {
        var options = CliOptions.Parse(new[] { "compare", "a.json", "b.json", "c.json", "--save", "out.json", "--json" }, out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("compare");
        options.ComparePaths.Should().Equal("a.json", "b.json", "c.json");
        options.SavePath.Should().Be("out.json");
        options.Json.Should().BeTrue();
    }

    [Fact]
    public void Parse_CollectSave_IsCaptured()
    {
        var options = CliOptions.Parse(new[] { "collect", "--kind", "counters", "--save", "snapshot.json" }, out var error);

        error.Should().BeNull();
        options!.SavePath.Should().Be("snapshot.json");
    }

    [Fact]
    public void Parse_CollectFlags_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[]
            {
                "collect", "--kind", "counters", "--duration", "7", "--interval", "2",
                "--max-events", "50", "--depth", "detail", "--min-level", "Warning",
                "--provider", "System.Runtime", "--provider", "Microsoft.AspNetCore.Hosting",
                "--meter", "MyMeter", "--source", "Src.A", "--category", "Cat.*",
                "--unsafe-provider",
            },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("collect");
        options.Kind.Should().Be("counters");
        options.DurationSeconds.Should().Be(7);
        options.IntervalSeconds.Should().Be(2);
        options.MaxEvents.Should().Be(50);
        options.Depth.Should().Be("detail");
        options.MinLevel.Should().Be("Warning");
        options.Providers.Should().Equal("System.Runtime", "Microsoft.AspNetCore.Hosting");
        options.Meters.Should().Equal("MyMeter");
        options.Sources.Should().Equal("Src.A");
        options.Categories.Should().Equal("Cat.*");
        options.UnsafeProvider.Should().BeTrue();
    }

    [Theory]
    [InlineData("-d")]
    [InlineData("--duration")]
    public void Parse_DurationShortAndLongForms_AreEquivalent(string flag)
    {
        var options = CliOptions.Parse(new[] { "collect", "--kind", "gc", flag, "12" }, out var error);

        error.Should().BeNull();
        options!.DurationSeconds.Should().Be(12);
    }

    [Fact]
    public void Parse_CollectDefaults_AreEmptyNotNull()
    {
        var options = CliOptions.Parse(new[] { "collect", "--kind", "gc" }, out var error);

        error.Should().BeNull();
        options!.DurationSeconds.Should().BeNull();
        options.IntervalSeconds.Should().BeNull();
        options.MaxEvents.Should().BeNull();
        options.UnsafeProvider.Should().BeFalse();
        options.Providers.Should().BeEmpty();
        options.Meters.Should().BeEmpty();
        options.Sources.Should().BeEmpty();
        options.Categories.Should().BeEmpty();
    }

    [Fact]
    public void Parse_KindMissingValue_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "collect", "--kind" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_ProviderMissingValue_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "collect", "--kind", "counters", "--provider" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_InspectHeapFlags_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[]
            {
                "inspect-heap", "--source", "dump", "--dump-file", "./app.dmp", "--top-types", "30",
                "--include-retention-paths", "--retention-path-limit", "12", "--include-static-fields",
                "--include-delegate-targets", "--include-duplicate-strings", "--symbol-path", @"C:\syms",
            },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("inspect-heap");
        options.Sources.Should().Equal("dump");
        options.DumpFile.Should().Be("./app.dmp");
        options.TopTypes.Should().Be(30);
        options.IncludeRetentionPaths.Should().BeTrue();
        options.RetentionPathLimit.Should().Be(12);
        options.IncludeStaticFields.Should().BeTrue();
        options.IncludeDelegateTargets.Should().BeTrue();
        options.IncludeDuplicateStrings.Should().BeTrue();
        options.SymbolPath.Should().Be(@"C:\syms");
    }

    [Fact]
    public void Parse_DumpFlags_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[] { "dump", "--pid", "1234", "--dump-type", "WithHeap", "--out", "./dumps", "--confirm" },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("dump");
        options.Pid.Should().Be(1234);
        options.DumpType.Should().Be("WithHeap");
        options.OutDir.Should().Be("./dumps");
        options.Confirm.Should().BeTrue();
    }

    [Fact]
    public void Parse_HeapAndDumpDefaults_AreFalseOrNull()
    {
        var options = CliOptions.Parse(new[] { "inspect-heap" }, out var error);

        error.Should().BeNull();
        options!.DumpFile.Should().BeNull();
        options.TopTypes.Should().BeNull();
        options.RetentionPathLimit.Should().BeNull();
        options.IncludeRetentionPaths.Should().BeFalse();
        options.IncludeStaticFields.Should().BeFalse();
        options.IncludeDelegateTargets.Should().BeFalse();
        options.IncludeDuplicateStrings.Should().BeFalse();
        options.SymbolPath.Should().BeNull();
        options.DumpType.Should().BeNull();
        options.OutDir.Should().BeNull();
        options.Confirm.Should().BeFalse();
    }

    [Fact]
    public void Parse_DumpTypeMissingValue_ReturnsError()
    {
        var options = CliOptions.Parse(new[] { "dump", "--dump-type" }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_GetBytesModuleFlags_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "module", "--pid", "1234", "--mvid", "11112222-3333-4444-5555-666677778888", "--asset", "pdb", "--out", "./app.dll" },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("get-bytes");
        options.Kind.Should().Be("module");
        options.Pid.Should().Be(1234);
        options.Mvid.Should().Be("11112222-3333-4444-5555-666677778888");
        options.Asset.Should().Be("pdb");
        options.OutDir.Should().Be("./app.dll");
    }

    [Fact]
    public void Parse_GetBytesDumpFlags_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[] { "get-bytes", "--kind", "dump", "--dump-file", "./app.dmp", "--out", "./copy.dmp" },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("get-bytes");
        options.Kind.Should().Be("dump");
        options.DumpFile.Should().Be("./app.dmp");
        options.OutDir.Should().Be("./copy.dmp");
    }

    [Fact]
    public void Parse_QueryFlags_AreCaptured()
    {
        var options = CliOptions.Parse(
            new[] { "query", "--handle", "h-123", "--view", "top-types" },
            out var error);

        error.Should().BeNull();
        options!.Command.Should().Be("query");
        options.Handle.Should().Be("h-123");
        options.View.Should().Be("top-types");
    }

    [Theory]
    [InlineData("--mvid")]
    [InlineData("--asset")]
    [InlineData("--handle")]
    [InlineData("--view")]
    public void Parse_NewFlagsMissingValue_ReturnError(string flag)
    {
        var options = CliOptions.Parse(new[] { "get-bytes", flag }, out var error);

        options.Should().BeNull();
        error.Should().Contain("requires a value");
    }

    [Fact]
    public void Parse_GetBytesDefaults_AreNull()
    {
        var options = CliOptions.Parse(new[] { "get-bytes" }, out var error);

        error.Should().BeNull();
        options!.Mvid.Should().BeNull();
        options.Asset.Should().BeNull();
        options.Handle.Should().BeNull();
        options.View.Should().BeNull();
    }
}
