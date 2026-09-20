using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class LiveCompatibilityEvidenceTests
{
    private static readonly Guid FixtureMvid = Guid.Parse("a403283c-58f8-4321-b230-197ae60ad1e0");
    private const int FixtureToken = 100663299;
    private const string RetainedSignature =
        "CompatibilityFixture.ClosedGenericHold[[System.Int32, System.Private.CoreLib]](Int32, System.Threading.ManualResetEventSlim, System.Threading.ManualResetEventSlim)";
    private static readonly string[] Arguments = ["dotnet", "/sample/MultiVersionSample.dll", "--live-compatibility"];
    private static readonly string[] Modules = ["/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.31/libcoreclr.so"];
    private static readonly DateTime Started = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly ProcessInfoSnapshot Info = new(1, "dotnet /sample/MultiVersionSample.dll --live-compatibility",
        "Linux", "x64", "8.0.31", "MultiVersionSample", "linux-x64");

    [Fact]
    public void TargetVersion_IsFromBoundIpcAndMappedModule_NotClrMdOrRequestedMajor()
    {
        var evidence = LiveCompatibilityEvidence.ValidateTarget(1, 8, Info, Arguments, Modules, Started, Started);
        evidence.ActualVersion.Should().Be("8.0.31");
        evidence.Source.Should().Be("diagnostic-ipc-process-info+procfs");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.0")]
    [InlineData("8")]
    [InlineData("9.0.20")]
    [InlineData("8.0.30")]
    public void MissingWrongOrUncorroboratedRuntimeFails(string version)
    {
        var action = () => LiveCompatibilityEvidence.ValidateTarget(
            1, 8, Info with { ClrProductVersionString = version }, Arguments, Modules, Started, Started);
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("command")]
    [InlineData("architecture")]
    [InlineData("os")]
    [InlineData("process-reused")]
    [InlineData("multiple-modules")]
    public void DifferentProcessOrAmbiguousRuntimeFails(string kind)
    {
        var info = kind switch
        {
            "pid" => Info with { ProcessId = 2 },
            "command" => Info with { CommandLine = "another-app" },
            "architecture" => Info with { ProcessArchitecture = "arm64" },
            "os" => Info with { OperatingSystem = "Windows" },
            _ => Info,
        };
        var modules = kind == "multiple-modules" ? Modules.Concat(Modules).ToArray() : Modules;
        var current = kind == "process-reused" ? Started.AddSeconds(1) : Started;
        var action = () => LiveCompatibilityEvidence.ValidateTarget(1, 8, info, Arguments, modules, Started, current);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RetainedClosedMethodNameSelectsByTokenMvidAndNormalizedSignature()
    {
        var frame = Frame();
        LiveCompatibilityEvidence.SelectGenericFrame([frame], FixtureMvid, FixtureToken).Should().BeSameAs(frame);
        frame.Identity!.MethodName.Should().Contain("[[System.Int32",
            "selection must not rewrite or require bare ClrMD method names");
    }

    [Theory]
    [InlineData("System.__Canon")]
    [InlineData("System.String")]
    [InlineData("System.Int64")]
    public void DifferentOrSharedCanonInstantiationCannotSatisfyKnownInt32(string argument)
    {
        var frame = Frame() with { DisplayName = RetainedSignature.Replace("System.Int32", argument, StringComparison.Ordinal) };
        var action = () => LiveCompatibilityEvidence.SelectGenericFrame([frame], FixtureMvid, FixtureToken);
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("mvid")]
    [InlineData("token")]
    [InlineData("type")]
    [InlineData("open")]
    [InlineData("ambiguous")]
    public void WrongOrAmbiguousMethodIdentityIsRejected(string kind)
    {
        var original = Frame();
        var frame = kind switch
        {
            "mvid" => original with { Identity = original.Identity! with { ModuleVersionId = Guid.Empty } },
            "token" => original with { Identity = original.Identity! with { MetadataToken = FixtureToken + 1 } },
            "type" => original with { TypeFullName = "AnotherFixture" },
            "open" => original with { DisplayName = "CompatibilityFixture.ClosedGenericHold(T)" },
            _ => original,
        };
        ManagedStackFrame[] frames = kind == "ambiguous" ? [frame, frame] : [frame];
        var action = () => LiveCompatibilityEvidence.SelectGenericFrame(frames, FixtureMvid, FixtureToken);
        action.Should().Throw<InvalidOperationException>();
    }

    private static ManagedStackFrame Frame() => new("ManagedMethod", RetainedSignature, "CompatibilityFixture",
        "MultiVersionSample.dll", 1234, 5678,
        new MethodIdentity(ModuleName: "MultiVersionSample.dll", ModulePath: LiveCompatibilityEvidence.SamplePath,
            ModuleVersionId: FixtureMvid, MetadataToken: FixtureToken, TypeFullName: "CompatibilityFixture",
            MethodName: "ClosedGenericHold[[System.Int32, System.Private.CoreLib]]", GenericArity: 0));
}
