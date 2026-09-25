using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Safety;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliDurableCaptureValidationTests
{
    private const string CaptureId = "5a6d39be6c50493eaf1d1c341de51700";
    private const string ArtifactId = "f8cab99b6c3249fa8e0dfbe055cbdb66";

    [Fact]
    public void PersistenceSafetyMergesCollectionRiskAndDurableSideEffects()
    {
        var options = new CliOptions { Command = "inspect-heap", Persist = true, Sources = ["live"] };
        var safety = CliInvocationSafety.Resolve(options);
        safety.RiskLevel.Should().Be(InvocationRiskLevel.High);
        safety.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        safety.TargetImpact.Should().Contain(TargetImpact.PtraceAttach);
        var query = CliInvocationSafety.Resolve(new CliOptions
        {
            Command = "query", CaptureId = CaptureId, ArtifactId = ArtifactId, View = "records",
        });
        query.TargetImpact.Should().BeEmpty();
        query.SideEffects.Should().BeEmpty();
        query.DataExposure.Should().Contain(DataExposure.PossibleSecrets);
    }

    [Fact]
    public async Task DeleteNeedsAcknowledgementAndExplainRiskNeverCreatesStore()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".validation", Guid.NewGuid().ToString("N"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(
            ["captures", "delete", "--capture-id", CaptureId, "--capture-root", root],
            stdout, stderr, CancellationToken.None);
        exit.Should().Be(2);
        stderr.ToString().Should().Contain("--acknowledge-risk high");
        Directory.Exists(root).Should().BeFalse();
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        exit = await CliHost.RunAsync(
            ["collect", "--kind", "counters", "--persist", "--capture-root", root, "--explain-risk", "--json"],
            stdout, stderr, CancellationToken.None);
        exit.Should().Be(0);
        stdout.ToString().Should().Contain("writes-artifact").And.Contain(Path.Combine(root, "captures"))
            .And.Contain("\"executed\": false");
        Directory.Exists(root).Should().BeFalse();
    }

    [Theory]
    [InlineData("collect --kind gc")]
    [InlineData("inspect-heap --source live")]
    [InlineData("session")]
    public void PersistenceIsOptIn(string command)
    {
        CliOptions.Parse(SessionRepl.Tokenize(command), out var error)!.Persist.Should().BeFalse();
        error.Should().BeNull();
        var options = CliOptions.Parse(SessionRepl.Tokenize(command + " --persist --capture-root ./evidence"), out error)!;
        error.Should().BeNull();
        options.Persist.Should().BeTrue();
        CliCommands.TryValidateCommand(options, out error).Should().BeTrue(error);
    }

    [Theory]
    [InlineData("captures", "requires one action")]
    [InlineData("captures unknown", "requires one action")]
    [InlineData("captures show", "requires --capture-id")]
    [InlineData("dump --persist", "--persist requires")]
    [InlineData("query --artifact-id f8cab99b6c3249fa8e0dfbe055cbdb66", "--artifact-id requires")]
    [InlineData("captures show --capture-id ../../outside", "exact lower-case GUID")]
    [InlineData("captures list --page-size 101", "between 1 and 100")]
    [InlineData("query --view records --page-size 10", "--page-size requires")]
    [InlineData("collect --kind gc --name ignored", "--from, --to, --name")]
    public void InvalidOptionsFailBeforeInvocation(string command, string message)
    {
        CliCommandExecution.TryPrepareOneShot(SessionRepl.Tokenize(command), out _, out var response).Should().BeFalse();
        response!.Text.Should().Contain(message);
    }

    [Theory]
    [InlineData("--handle ephemeral")]
    [InlineData("--latest-of-kind gc")]
    [InlineData("--pid 1234")]
    [InlineData("--gc-handle ephemeral")]
    public void DurableSelectorCannotBeCombinedWithSessionOrLiveSelectors(string selector)
    {
        var command = $"query --capture-id {CaptureId} --artifact-id {ArtifactId} --view records {selector}";
        CliCommandExecution.TryPrepareOneShot(SessionRepl.Tokenize(command), out _, out var response).Should().BeFalse();
        response!.Text.Should().Contain("cannot be combined");
    }

    [Theory]
    [InlineData("--from not-a-date", "ISO-8601")]
    [InlineData("--from 2026-09-24T10:00:00", "ISO-8601")]
    [InlineData("--from 2026-09-24T12:00:00Z --to 2026-09-24T11:00:00Z", "must not be later")]
    [InlineData("--after-record-id -1", "nonnegative")]
    [InlineData("--page-size 1001", "between 1 and 1000")]
    [InlineData("--category first --category second", "at most one")]
    public void RecordsAreBoundedAndTyped(string filter, string message)
    {
        var command = $"query --capture-id {CaptureId} --artifact-id {ArtifactId} --view records {filter}";
        CliCommandExecution.TryPrepareOneShot(SessionRepl.Tokenize(command), out _, out var response).Should().BeFalse();
        response!.Text.Should().Contain(message);
    }

    [Fact]
    public void RecordFiltersParseWithoutSql()
    {
        var command = $"query --capture-id {CaptureId} --artifact-id {ArtifactId} --view records"
            + " --from 2026-09-24T10:00:00Z --to 2026-09-24T11:00:00+00:00"
            + " --thread-id 42 --category gc --name pause --after-record-id 5 --page-size 10";
        CliCommandExecution.TryPrepareOneShot(SessionRepl.Tokenize(command), out var prepared, out var response)
            .Should().BeTrue(response?.Text);
        prepared!.Options.AfterRecordId.Should().Be(5);
        prepared.Options.RecordName.Should().Be("pause");
        prepared.Options.Categories.Should().Equal("gc");
    }

    [Fact]
    public void SessionInheritanceIsLimitedToEligibleCommandsAndRespectsRootOverride()
    {
        var session = new CliOptions { Command = "session", Persist = true, CaptureRoot = "./persistent" };
        var inherited = SessionRepl.InheritCaptureOptions(["collect", "--kind", "gc"], session);
        var options = CliOptions.Parse(inherited, out _)!;
        options.Persist.Should().BeTrue();
        options.CaptureRoot.Should().Be("./persistent");
        var overridden = CliOptions.Parse(SessionRepl.InheritCaptureOptions(
            ["captures", "list", "--capture-root", "./other"], session), out _)!;
        overridden.Persist.Should().BeFalse();
        overridden.CaptureRoot.Should().Be("./other");
        SessionRepl.InheritCaptureOptions(["dump", "--out", "./dump"], session)
            .Should().Equal("dump", "--out", "./dump");
    }

    [Fact]
    public void HelpAndCompletionAdvertiseDurableSurface()
    {
        CliHelp.ForCommand("collect").Should().Contain("--persist");
        CliHelp.ForCommand("inspect-heap").Should().Contain("--persist");
        CliHelp.ForCommand("query").Should().Contain("--capture-id").And.Contain("--after-record-id");
        CliHelp.ForCommand("captures").Should().Contain("recover").And.Contain("--after-capture-id");
        SessionReplCompletion.GetCandidates(["captures"], "", null).Should().BeEquivalentTo("list", "show", "delete", "recover");
        SessionReplCompletion.GetCandidates(["query", "--capture-id"], "", null).Should().BeEmpty();
        foreach (var shell in CliCompletionScripts.Shells)
        {
            CliCompletionScripts.ForShell(shell).Should().Contain("captures").And.Contain("--persist")
                .And.Contain("--artifact-id").And.Contain("recover");
        }
    }

    [Fact]
    public void RootSelectionDoesNotCreateDirectoriesOrFollowEphemeralOverrides()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".validation", Guid.NewGuid().ToString("N"));
        var options = new CliOptions { CaptureRoot = root, OutDir = Path.Combine(root, "dump") };
        new CliCaptureRootProvider(options.CaptureRoot).Root.Should().Be(root);
        Directory.Exists(root).Should().BeFalse();
        CliCaptureRootProvider.CurrentAccess().Should().Be(CliCaptureRootProvider.CurrentAccess());
        CliCaptureRootProvider.CurrentAccess().AllOwners.Should().BeFalse();
    }
}
