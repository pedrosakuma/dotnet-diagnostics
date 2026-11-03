using System.Text.Json;
using System.Globalization;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalProjectionTests
{
    private static readonly string SuccessFixturePath = ScenarioManifestLoader.ScenarioPath(
        "Fixtures",
        "AllocationDirectional",
        "Harness",
        "allocation-success-envelopes.json");
    private static readonly string FailureFixturePath = ScenarioManifestLoader.ScenarioPath(
        "Fixtures",
        "AllocationDirectional",
        "Harness",
        "allocation-failure-provenance-cases.json");

    [Fact]
    public void RealEnvelopeShapes_ProjectFromDataAllocationWithExplicitSchema()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SuccessFixturePath));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();

        cases.Select(item => item.GetProperty("name").GetString()).Should().Equal(
            "primary",
            "changed",
            "increased",
            "low",
            "periodic-a",
            "periodic-b",
            "zero-weight",
            "missing-attribution");

        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!;
            var raw = item.GetProperty("response").GetRawText();
            var result = AllocationDirectionalProjection.Project(
                $"attempt-{name}",
                $"workload-{name}",
                raw,
                isError: false);

            result.Succeeded.Should().BeTrue(name);
            result.Evidence.FailureCategory.Should().BeNull();
            result.Evidence.Stage.Should().Be(AllocationDirectionalAttemptStage.Completed);
            result.Evidence.SchemaIdentity.Should().Be(
                AllocationDirectionalProjection.SchemaIdentity);
            result.Evidence.IsError.Should().BeFalse();
            result.Evidence.RawResponse.Should().NotBeNull();
            result.Evidence.RawResponse!.ByteCount.Should().BeGreaterThan(0);
            result.Evidence.RawResponse.RetentionState.Should().Be(
                AllocationDirectionalRawRetentionState.DigestOnly);
            result.Evidence.RawResponse.Truncated.Should().BeTrue();
            result.Evidence.RawResponse.Redacted.Should().BeTrue();
            result.Evidence.RawResponse.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        }

        var zero = ProjectCase(cases, "zero-weight").Allocation!;
        zero.TotalBytes.Should().Be(0);
        zero.TotalEvents.Should().Be(0);
        zero.Types.Should().BeEmpty();
        zero.Sites.Should().BeEmpty();

        var missingAttribution = ProjectCase(cases, "missing-attribution").Allocation!;
        missingAttribution.Types.Should().NotBeEmpty();
        missingAttribution.Sites.Should().BeEmpty();
        missingAttribution.AttributionState.Should().Be("no-site");
    }

    [Fact]
    public void HistoricalDataLevelProjectionFailsButCorrectedAllocationProjectionSucceeds()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SuccessFixturePath));
        var primary = document.RootElement.GetProperty("cases")[0]
            .GetProperty("response");

        var historicalProjection = () => primary.GetProperty("data")
            .GetProperty("totalBytes")
            .GetInt64();
        historicalProjection.Should().Throw<KeyNotFoundException>();

        var corrected = AllocationDirectionalProjection.Project(
            "attempt-primary",
            "workload-primary",
            primary.GetRawText(),
            isError: false);

        corrected.Succeeded.Should().BeTrue();
        corrected.Allocation!.TotalBytes.Should().Be(288_000);
    }

    [Fact]
    public void ProjectionFailures_AreStableMutuallyExclusiveAndNeverSuccessShaped()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FailureFixturePath));
        var cases = document.RootElement.GetProperty("projectionCases")
            .EnumerateArray()
            .ToArray();

        foreach (var item in cases)
        {
            var raw = FailureRaw(item);
            var expected = ParseEnum<AllocationDirectionalFailureCategory>(
                item.GetProperty("expectedCategory").GetString()!);
            var result = AllocationDirectionalProjection.Project(
                $"attempt-{item.GetProperty("name").GetString()}",
                "workload-fixture",
                raw,
                item.GetProperty("isError").GetBoolean());

            result.Succeeded.Should().BeFalse();
            result.Allocation.Should().BeNull();
            result.Evidence.FailureCategory.Should().Be(expected);
            result.Evidence.OperationId.Should().NotBeNullOrWhiteSpace();
            result.Evidence.RawResponse.Should().NotBeNull();
        }

        cases.Where(item => item.GetProperty("isError").GetBoolean())
            .Should().OnlyContain(item =>
                AllocationDirectionalProjection.Project(
                        $"attempt-{item.GetProperty("name").GetString()}",
                        "workload-fixture",
                        FailureRaw(item),
                        isError: true)
                    .Evidence.Stage == AllocationDirectionalAttemptStage.McpResponse);
    }

    [Fact]
    public void ExternalFailureStages_RetainExactSourceOwnedOperationIdentity()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FailureFixturePath));
        var cases = document.RootElement.GetProperty("externalCases")
            .EnumerateArray()
            .ToArray();

        foreach (var item in cases)
        {
            var stage = ParseEnum<AllocationDirectionalAttemptStage>(
                item.GetProperty("stage").GetString()!);
            var expectedCategory = ParseEnum<AllocationDirectionalFailureCategory>(
                item.GetProperty("expectedCategory").GetString()!);
            var operationId = item.GetProperty("operationId").GetString()!;
            Exception exception = expectedCategory switch
            {
                AllocationDirectionalFailureCategory.Timeout => new TimeoutException(),
                AllocationDirectionalFailureCategory.Cancellation =>
                    new OperationCanceledException(),
                _ => new InvalidOperationException("sensitive-message"),
            };
            var category = expectedCategory is
                AllocationDirectionalFailureCategory.DenominatorInvariant
                or AllocationDirectionalFailureCategory.RetainedValueInvariant
                or AllocationDirectionalFailureCategory.Cleanup
                    ? expectedCategory
                    : AllocationDirectionalAttemptClassifier.ClassifyException(
                        stage,
                        exception,
                        callerCancellationRequested:
                            expectedCategory
                                == AllocationDirectionalFailureCategory.Cancellation);
            var evidence = AllocationDirectionalProjection.ExternalFailure(
                $"attempt-{item.GetProperty("name").GetString()}",
                "workload-fixture",
                stage,
                operationId,
                category,
                exception);

            evidence.Stage.Should().Be(stage);
            evidence.OperationId.Should().Be(operationId);
            evidence.FailureCategory.Should().Be(expectedCategory);
            evidence.ExceptionType.Should().Be(exception.GetType().Name);
            evidence.RawResponse.Should().BeNull();
            evidence.IsError.Should().BeNull();
        }

        cases.Select(item => item.GetProperty("name").GetString()).Should().Contain(
            "process-start",
            "process-readiness",
            "warmup",
            "workload",
            "mcp-transport",
            "denominator-invariant",
            "retained-value-invariant",
            "timeout",
            "cancellation",
            "cleanup",
            "unexpected-harness-exception");
    }

    [Fact]
    public void CleanupClassification_IgnoresExpectedCollectCancellation()
    {
        AllocationDirectionalAttemptClassifier.IsCollectDrainFailure(
                collectTaskCompleted: true,
                new OperationCanceledException())
            .Should().BeFalse();
        AllocationDirectionalAttemptClassifier.IsCollectDrainFailure(
                collectTaskCompleted: true,
                new InvalidOperationException())
            .Should().BeFalse();
        AllocationDirectionalAttemptClassifier.IsCollectDrainFailure(
                collectTaskCompleted: false,
                new TimeoutException())
            .Should().BeTrue();

        var primary = AllocationDirectionalProjection.ExternalFailure(
            "attempt-timeout",
            "workload-timeout",
            AllocationDirectionalAttemptStage.McpTransport,
            "mcp.collect-sample-await",
            AllocationDirectionalFailureCategory.Timeout);
        var finalized = AllocationDirectionalAttemptClassifier.FinalizeCleanup(
            primary,
            passed: false,
            cleanupException: null);

        finalized.ReplacePassedObservation.Should().BeFalse();
        finalized.Evidence.FailureCategory.Should().Be(
            AllocationDirectionalFailureCategory.Timeout);
        finalized.Evidence.CleanupState.Should().Be(
            AllocationDirectionalCleanupState.Succeeded);
    }

    [Fact]
    public void CleanupClassification_ReplacesOnlyPassedObservation()
    {
        var successful = AllocationDirectionalProjection.Project(
            "attempt-success",
            "workload-success",
            """
            {
              "error": null,
              "data": {
                "kind": "allocation",
                "allocation": {
                  "totalEvents": 0,
                  "totalBytes": 0,
                  "topByBytes": [],
                  "topBySite": []
                }
              }
            }
            """,
            isError: false);
        var finalized = AllocationDirectionalAttemptClassifier.FinalizeCleanup(
            successful.Evidence,
            passed: true,
            new IOException("not retained"));

        finalized.ReplacePassedObservation.Should().BeTrue();
        finalized.Evidence.FailureCategory.Should().Be(
            AllocationDirectionalFailureCategory.Cleanup);
        finalized.Evidence.Stage.Should().Be(AllocationDirectionalAttemptStage.Cleanup);
        finalized.Evidence.CleanupState.Should().Be(
            AllocationDirectionalCleanupState.Failed);
    }

    [Fact]
    public void CleanupClassification_PreservesPrimaryFailureAndOwnedStartupCleanupFailure()
    {
        var primaryException = new InvalidOperationException("primary");
        var cleanupException = new IOException("cleanup");
        var split = AllocationDirectionalAttemptClassifier.SplitOwnedStartupFailure(
            AllocationDirectionalAttemptStage.ProcessStart,
            new AggregateException(primaryException, cleanupException));
        var primary = AllocationDirectionalProjection.ExternalFailure(
            "attempt-startup",
            "workload-startup",
            AllocationDirectionalAttemptStage.ProcessStart,
            "process.start-published",
            AllocationDirectionalAttemptClassifier.ClassifyException(
                AllocationDirectionalAttemptStage.ProcessStart,
                split.PrimaryException,
                callerCancellationRequested: false),
            split.PrimaryException);
        var finalized = AllocationDirectionalAttemptClassifier.FinalizeCleanup(
            primary,
            passed: false,
            split.CleanupException);

        split.PrimaryException.Should().BeSameAs(primaryException);
        split.CleanupException.Should().BeSameAs(cleanupException);
        finalized.ReplacePassedObservation.Should().BeFalse();
        finalized.Evidence.FailureCategory.Should().Be(
            AllocationDirectionalFailureCategory.ProcessStart);
        finalized.Evidence.CleanupState.Should().Be(
            AllocationDirectionalCleanupState.Failed);
    }

    [Fact]
    public void ErrorProvenance_IsBoundedAndDoesNotRetainMessagesPathsOrPayload()
    {
        const string raw = """
            {
              "error": {
                "kind": "PermissionDenied",
                "message": "credential=secret-token",
                "detail": "C:\\private\\target-name\\dump.dmp"
              }
            }
            """;

        var result = AllocationDirectionalProjection.Project(
            "attempt-error",
            "workload-error",
            raw,
            isError: true);
        var serialized = JsonSerializer.Serialize(
            result.Evidence,
            AllocationDirectionalJson.Options);

        result.Evidence.ErrorKind.Should().Be("PermissionDenied");
        result.Evidence.ErrorDetailState.Should().Be("present-redacted");
        result.Evidence.ErrorDetailByteCount.Should().BeGreaterThan(0);
        serialized.Should().NotContain("secret-token")
            .And.NotContain("private")
            .And.NotContain("target-name")
            .And.NotContain("dump.dmp")
            .And.NotContain("credential");
        result.Evidence.RawResponse!.RetentionState.Should().Be(
            AllocationDirectionalRawRetentionState.DigestOnly);
    }

    [Fact]
    public void UnsafeErrorKind_IsRedactedRatherThanRetained()
    {
        const string raw = """
            {
              "error": {
                "kind": "C:\\private\\target-name",
                "message": "not retained"
              }
            }
            """;

        var result = AllocationDirectionalProjection.Project(
            "attempt-error-kind",
            "workload-error-kind",
            raw,
            isError: false);

        result.Evidence.ErrorKind.Should().Be("redacted");
    }

    [Fact]
    public void Ledger_RetainsExactlyOneStartAndTerminalRowPerAttempt()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"allocation-directional-ledger-{Guid.NewGuid():n}.jsonl");
        try
        {
            var timestamp = DateTimeOffset.Parse(
                "2026-11-03T12:00:00Z",
                CultureInfo.InvariantCulture);
            var first = FailedObservation("attempt-01");
            var second = FailedObservation("attempt-02");
            AllocationDirectionalLedger.AppendStarted(path, first.AttemptId, timestamp);
            AllocationDirectionalLedger.AppendCompleted(path, first, timestamp.AddSeconds(1));
            AllocationDirectionalLedger.AppendStarted(path, second.AttemptId, timestamp.AddSeconds(2));
            AllocationDirectionalLedger.AppendCompleted(path, second, timestamp.AddSeconds(3));

            AllocationDirectionalLedger.ValidateExactTerminalAccounting(
                path,
                [first.AttemptId, second.AttemptId]);

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(4);
            lines.Should().OnlyContain(line =>
                JsonDocument.Parse(line).RootElement.GetProperty("schemaVersion").GetInt32()
                    == AllocationDirectionalLedger.CurrentSchemaVersion);
            using var terminal = JsonDocument.Parse(lines[1]);
            terminal.RootElement.GetProperty("attemptId").GetString().Should().Be("attempt-01");
            terminal.RootElement.GetProperty("state").GetString().Should().Be("completed");
            terminal.RootElement.GetProperty("failureCategory").GetString().Should()
                .Be("timeout");
            terminal.RootElement.GetProperty("operationId").GetString().Should()
                .Be("mcp.collect-sample-await");
            terminal.RootElement.GetProperty("cleanupState").GetString().Should()
                .Be("succeeded");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ledger_RejectsRetryOrReplacementAccounting()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"allocation-directional-ledger-{Guid.NewGuid():n}.jsonl");
        try
        {
            var timestamp = DateTimeOffset.Parse(
                "2026-11-03T12:00:00Z",
                CultureInfo.InvariantCulture);
            var observation = FailedObservation("attempt-01");
            AllocationDirectionalLedger.AppendStarted(path, observation.AttemptId, timestamp);
            AllocationDirectionalLedger.AppendCompleted(
                path,
                observation,
                timestamp.AddSeconds(1));
            AllocationDirectionalLedger.AppendStarted(
                path,
                observation.AttemptId,
                timestamp.AddSeconds(2));
            AllocationDirectionalLedger.AppendCompleted(
                path,
                observation,
                timestamp.AddSeconds(3));

            var validate = () => AllocationDirectionalLedger.ValidateExactTerminalAccounting(
                path,
                [observation.AttemptId]);

            validate.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AllocationDirectionalProjectionResult ProjectCase(
        JsonElement[] cases,
        string name)
    {
        var item = cases.Single(candidate =>
            candidate.GetProperty("name").GetString() == name);
        return AllocationDirectionalProjection.Project(
            $"attempt-{name}",
            $"workload-{name}",
            item.GetProperty("response").GetRawText(),
            isError: false);
    }

    private static T ParseEnum<T>(string value)
        where T : struct, Enum
        => JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(value),
            AllocationDirectionalJson.Options);

    private static string FailureRaw(JsonElement item)
    {
        if (item.TryGetProperty("raw", out var rawValue))
        {
            return rawValue.GetString()!;
        }

        if (item.TryGetProperty("rawKind", out var rawKind)
            && rawKind.GetString() == "oversize")
        {
            return new string(
                'x',
                AllocationDirectionalBounds.MaximumRawResponseBytes + 1);
        }

        return item.GetProperty("response").GetRawText();
    }

    private static AllocationDirectionalObservation FailedObservation(string attemptId)
        => new(
            attemptId,
            AllocationDirectionalAttemptOutcome.Aborted,
            AllocationDirectionalOrigin.LiveMcp,
            "workload-fixture",
            "primary",
            "steady",
            1,
            4,
            0,
            0,
            0,
            0,
            0,
            "unavailable",
            [],
            [],
            nameof(AllocationDirectionalFailureCategory.Timeout),
            ["Retained without retry."])
        {
            AttemptEvidence = AllocationDirectionalProjection.WithCleanup(
                AllocationDirectionalProjection.ExternalFailure(
                    attemptId,
                    "workload-fixture",
                    AllocationDirectionalAttemptStage.McpTransport,
                    "mcp.collect-sample-await",
                    AllocationDirectionalFailureCategory.Timeout),
                AllocationDirectionalCleanupState.Succeeded),
        };
}
