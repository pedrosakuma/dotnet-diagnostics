using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Safety;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliPortableCaptureTests : IDisposable
{
    private const string Id = "5a6d39be6c50493eaf1d1c341de51700";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cli-portable-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("captures export", "1-16")]
    [InlineData("captures import", "--file")]
    [InlineData("captures import --file -", "--file")]
    [InlineData("captures import-result", "--operation-id")]
    [InlineData("captures import --file x --operation-id " + Id, "supplied together")]
    [InlineData("captures import --file x --requested-utc 2026-09-24T00:00:00Z", "supplied together")]
    [InlineData("captures import --file x --entry " + Id, "only supported")]
    [InlineData("captures export --entry label --file x", "exact capture GUID")]
    [InlineData("captures export --entry " + Id + " --file x --capture-id " + Id, "not --capture-id")]
    [InlineData("captures import-result --file x", "not --file")]
    [InlineData("captures list --file x", "require captures")]
    [InlineData("processes --entry " + Id, "require captures")]
    public void InvalidPortableArgumentsFailBeforeExecution(string command, string message)
    {
        CliCommandExecution.TryPrepareOneShot(SessionRepl.Tokenize(command), out _, out var response).Should().BeFalse();
        response!.Text.Should().Contain(message);
    }

    [Fact]
    public void LabelsAreDataAndRepeatableSelectionsPreserveOrderInBothContexts()
    {
        string[] args = ["captures", "export", "--entry", Id + "=same / label = data",
            "--entry", Id + "=same / label = data", "--file", "path with spaces/out.ddcapture"];
        CliCommandExecution.TryPrepareOneShot(args, out var one, out var response).Should().BeTrue(response?.Text);
        var inherited = SessionRepl.InheritCaptureOptions(args,
            new CliOptions { Command = "session", CaptureRoot = "store with spaces", Persist = true });
        CliCommandExecution.TryPrepareSession(inherited.ToArray(), 1234, out var session, out response).Should().BeTrue(response?.Text);
        one!.Options.CaptureEntries.Should().Equal(session!.Options.CaptureEntries);
        session.Options.CaptureRoot.Should().Be("store with spaces");
        session.Options.Persist.Should().BeFalse();
        session.Options.Pid.Should().BeNull();
        session.Options.CaptureFile.Should().Be("path with spaces/out.ddcapture");
        CliCommandCatalog.ValueFlags.Should().Contain(["--entry", "--file", "--operation-id", "--requested-utc"]);
        CliHelp.ForCommand("captures").Should().Contain("import-result").And.Contain("--requested-utc");
    }

    [Theory]
    [InlineData(17, "ok")]
    [InlineData(1, "\n")]
    public void EntryBoundsAreValidated(int count, string label)
    {
        var options = new CliOptions { Command = "captures", CaptureAction = "export",
            CaptureFile = "out.ddcapture", CaptureEntries = Enumerable.Repeat(Id + "=" + label, count).ToArray() };
        CliCommands.TryValidateCommand(options, out _).Should().BeFalse();
        CliCommands.TryValidateCommand(options with { CaptureEntries = [Id + "=" + new string('a', 257)] }, out _).Should().BeFalse();
        CliCommands.TryValidateCommand(options with { CaptureEntries = [Id + "=" + new string('\u00e9', 129)] }, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("relative-worker", "/missing-library")]
    [InlineData("/missing-worker", "relative-library")]
    [InlineData("/missing-worker", "/missing-library")]
    public void MissingOrRelativeWorkerConfigurationIsExplicitlyUnavailable(string? worker, string? library)
    {
        var action = () => CliCommands.ConfiguredImportWorker(worker, library);
        action.Should().Throw<CaptureStoreException>().Which.Code.Should().Be(CaptureErrorCode.UnsupportedFormat);
    }

    [Fact]
    public void LocalBinaryByteCeilingIsInclusiveAndReportsActualBound()
    {
        const long maximum = 512L * 1024 * 1024;
        CliCommands.CheckPortableFileBytes(maximum);
        var action = () => CliCommands.CheckPortableFileBytes(maximum + 1);
        var error = action.Should().Throw<CaptureStoreException>().Which;
        error.Code.Should().Be(CaptureErrorCode.CapacityExceeded);
        error.Data["PortableLimit"].Should().Be("MaxArchiveBytes");
        error.Data["PortableObserved"].Should().Be(maximum + 1);
        error.Data["PortableMaximum"].Should().Be(maximum);
    }

    [Fact]
    public void InvalidLocalFilePathFailsValidationBeforeStoreAccess()
    {
        CliCommands.TryValidateCommand(new() { Command = "captures", CaptureAction = "import",
            CaptureRoot = _root, CaptureFile = "bad\0path" }, out var error).Should().BeFalse();
        error.Should().Contain("--file");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    public async Task PortableSafetyIsExplicitLocalHighRiskAndExplanationHasNoSideEffects(string action)
    {
        string[] selection = action == "export" ? ["--entry", Id] : [];
        string[] args = ["captures", action, "--file", Path.Combine(_root, "bundle.ddcapture"),
            "--capture-root", _root, .. selection];
        var options = CliOptions.Parse(args, out _)!;
        var safety = CliInvocationSafety.Resolve(options);
        safety.RiskLevel.Should().Be(InvocationRiskLevel.High);
        safety.TargetImpact.Should().BeEmpty();
        safety.DataExposure.Should().Contain(DataExposure.PossibleSecrets);
        safety.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        var output = new StringWriter();
        var error = new StringWriter();
        (await CliHost.RunAsync(args, output, error, CancellationToken.None)).Should().Be(2);
        error.ToString().Should().Contain("--acknowledge-risk high");
        output.GetStringBuilder().Clear();
        (await CliHost.RunAsync([.. args, "--explain-risk", "--json"], output, error, CancellationToken.None)).Should().Be(0);
        output.ToString().Should().Contain("\"executed\": false");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Theory]
    [InlineData("export", InvocationRiskLevel.High, InvocationApprovalPolicy.Acknowledge)]
    [InlineData("import", InvocationRiskLevel.High, InvocationApprovalPolicy.Acknowledge)]
    [InlineData("import-result", InvocationRiskLevel.Moderate, InvocationApprovalPolicy.Warn)]
    public void PortableSafetyUsesCanonicalDescriptorWithoutLocalOverride(
        string action, InvocationRiskLevel risk, InvocationApprovalPolicy approval)
    {
        var options = new CliOptions { Command = "captures", CaptureAction = action };
        var request = CliInvocationSafety.CreateRequest(options);
        var canonical = InvocationSafetyResolver.Resolve(request);
        CliInvocationSafety.Resolve(options).Should().BeEquivalentTo(canonical);
        CliInvocationSafety.ResolveForPreflight(request).Should().BeEquivalentTo(canonical);
        canonical.RiskLevel.Should().Be(risk);
        canonical.ApprovalPolicy.Should().Be(approval);
        canonical.TargetImpact.Should().BeEmpty();
        canonical.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        if (action == "import-result")
            canonical.SideEffects.Should().Contain(InvocationSideEffect.DeletesArtifact);
        else
            canonical.DataExposure.Should().Contain(DataExposure.PossibleSecrets);
    }

    [Fact]
    public async Task ReceiptExplanationWarnsAboutCleanupWithoutExecuting()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliHost.RunAsync(["captures", "import-result", "--capture-root", _root,
            "--operation-id", Id, "--requested-utc", DateTimeOffset.UtcNow.ToString("O"),
            "--explain-risk", "--json"], output, error, CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
        output.ToString().Should().Contain("moderate").And.Contain("writes-artifact").And.Contain("deletes-artifact");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task SessionExportUsesInheritedRootAndSameNoOverwriteOutcome()
    {
        var capture = await SeedAsync();
        var outputPath = Path.Combine(_root, "session bundle.ddcapture");
        var input = new StringReader($"captures export --entry {capture.CaptureId}=session --file \"{outputPath}\" --acknowledge-risk high --json\nexit\n");
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliHost.RunAsync(["session", "--capture-root", _root], input, output, error, CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        File.Exists(outputPath).Should().BeTrue(output.ToString() + error);
        output.ToString().Should().Contain("\"outputPublished\": true");
    }

    [Fact]
    public async Task ExportPublishesNoOverwriteBinaryAndKeepsSourceImmutable()
    {
        var capture = await SeedAsync();
        var sourceFiles = Directory.GetFiles(Path.Combine(_root, "captures"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith("capture.sqlite", StringComparison.Ordinal) ||
                path.EndsWith("manifest.json", StringComparison.Ordinal) || path.EndsWith("seal.json", StringComparison.Ordinal))
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        sourceFiles.Should().NotBeEmpty();
        var output = Path.Combine(_root, "bundle with spaces.ddcapture");
        var result = await RunAsync(new() { Command = "captures", CaptureAction = "export",
            CaptureRoot = _root, CaptureFile = output, CaptureEntries = [capture.CaptureId + "=duplicate", capture.CaptureId + "=duplicate"] });
        result.IsError.Should().BeFalse(result.Human);
        var outcome = Payload(result);
        outcome.OutputPublished.Should().BeTrue();
        outcome.Export!.ArchiveSha256.Should().Be(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant());
        using (var zip = ZipFile.OpenRead(output))
            zip.Entries.Count.Should().Be(8);
        foreach (var file in sourceFiles)
            SHA256.HashData(File.ReadAllBytes(file.Key)).Should().Equal(file.Value);
        var bytes = File.ReadAllBytes(output);
        var duplicate = await RunAsync(new() { Command = "captures", CaptureAction = "export", CaptureRoot = _root,
            CaptureFile = output, CaptureEntries = [capture.CaptureId] });
        duplicate.IsError.Should().BeTrue();
        Payload(duplicate).OutputPublished.Should().BeFalse();
        File.ReadAllBytes(output).Should().Equal(bytes);
        Directory.GetFiles(_root, "*.pending").Should().BeEmpty();
    }

    [Fact]
    public async Task MissingSelectionCleansOwnedSiblingAndNeverPublishes()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "absent.ddcapture");
        var result = await RunAsync(new() { Command = "captures", CaptureAction = "export", CaptureRoot = _root,
            CaptureFile = output, CaptureEntries = [Id] });
        result.IsError.Should().BeTrue();
        Payload(result).OutputPublished.Should().BeFalse();
        File.Exists(output).Should().BeFalse();
        Directory.GetFiles(_root, "*.pending").Should().BeEmpty();
    }

    [Fact]
    public async Task PreCancelledExportHasIdentityWithoutPublicationOrStoreCreation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await RunAsync(new() { Command = "captures", CaptureAction = "export", CaptureRoot = _root,
            CaptureFile = Path.Combine(_root, "cancelled.ddcapture"), CaptureEntries = [Id] }, cancellation.Token);
        result.Cancelled.Should().BeTrue();
        Payload(result).Operation.Id.Should().HaveLength(32);
        Payload(result).OutputPublished.Should().BeFalse();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task OutputInsideManagedStoreAndForeignOwnerAreRejected()
    {
        var capture = await SeedAsync(new("foreign-owner"));
        var inside = await RunAsync(new() { Command = "captures", CaptureAction = "export", CaptureRoot = _root,
            CaptureFile = Path.Combine(_root, "captures", "bad.ddcapture"), CaptureEntries = [capture.CaptureId] });
        Payload(inside).Failure!.Code.Should().Be(CaptureErrorCode.UnsafePath);
        var foreign = await RunAsync(new() { Command = "captures", CaptureAction = "export", CaptureRoot = _root,
            CaptureFile = Path.Combine(_root, "foreign.ddcapture"), CaptureEntries = [capture.CaptureId] });
        foreign.IsError.Should().BeTrue();
        Payload(foreign).OutputPublished.Should().BeFalse();
    }

    [Fact]
    public async Task ResultLookupPreservesExactOperationPairAndStructuredFailure()
    {
        var requested = DateTimeOffset.UtcNow;
        var result = await RunAsync(new() { Command = "captures", CaptureAction = "import-result", CaptureRoot = _root,
            OperationId = Id, RequestedUtc = requested.ToString("O") });
        result.IsError.Should().BeTrue();
        Payload(result).Operation.Should().Be(new PortableOperationKey(Id, requested));
        Payload(result).Failure.Should().NotBeNull();
        result.Human.Should().Contain(Id);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 130)]
    public async Task PartialRenderingRetainsPublishedMappingAndNonzeroExit(bool cancelled, int expectedExit)
    {
        var failure = new PortableFailure(cancelled ? CaptureErrorCode.Incomplete : CaptureErrorCode.Forbidden,
            cancelled ? "Cancelled" : "Denied", "second", null, null, null);
        var mapping = new PortableEntryMapping("first", "../same label", Id, "new-local-id", []);
        var import = new PortableImportResult(Id, "bundle", "hash", false, cancelled,
            [new("first", PortableEntryState.Published, mapping, null),
             new("second", cancelled ? PortableEntryState.Cancelled : PortableEntryState.Failed, null, failure)], failure);
        var result = CliCommands.RenderPortableCaptureOutcome(new(new(Id, DateTimeOffset.UtcNow),
            null, false, import, [], failure, null, null), cancelled);
        var output = new StringWriter();
        var exit = await CliCommandExecution.WriteCompletedResultAsync(result, new() { Command = "captures", Json = true },
            output, new StringWriter(), new(CliExecutionContext.OneShot, false, false));
        exit.Should().Be(expectedExit);
        using var json = JsonDocument.Parse(output.ToString());
        var entries = json.RootElement.GetProperty("data").GetProperty("import").GetProperty("entries");
        entries[0].GetProperty("mapping").GetProperty("localCaptureId").GetString().Should().Be("new-local-id");
        entries[1].TryGetProperty("mapping", out var unpublished).Should().BeFalse();
        result.Human.Should().Contain("new-local-id").And.Contain("second");
    }

    private async Task<CaptureInfo> SeedAsync(CaptureAccess? access = null)
    {
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(_root));
        await using var writer = await store.CreateAsync(new("portable fixture"), access ?? CliCaptureRootProvider.CurrentAccess());
        var artifact = writer.AddArtifact("counters", "records");
        writer.TryAppend(artifact, new(Name: "offline value", NumericValue: 42)).Should().BeTrue();
        return await writer.CompleteAsync();
    }

    private static CliPortableCaptureOutcome Payload(CliCommandResult result)
        => ((DiagnosticResult<CliPortableCaptureOutcome>)result.Envelope).Data!;

    private static async Task<CliCommandResult> RunAsync(CliOptions options, CancellationToken token = default)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        return await CliCommands.RunAsync(services, options, token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
