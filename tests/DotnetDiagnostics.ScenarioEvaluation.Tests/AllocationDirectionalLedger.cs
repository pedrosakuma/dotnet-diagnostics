using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalLedgerEntry(
    int SchemaVersion,
    string AttemptId,
    string State,
    AllocationDirectionalAttemptOutcome? Outcome,
    AllocationDirectionalFailureCategory? FailureCategory,
    AllocationDirectionalAttemptStage? Stage,
    string? OperationId,
    AllocationDirectionalCleanupState? CleanupState,
    string? RawResponseSha256,
    int? RawResponseByteCount,
    DateTimeOffset RecordedAtUtc);

internal static class AllocationDirectionalLedger
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void AppendStarted(
        string path,
        string attemptId,
        DateTimeOffset recordedAtUtc)
        => Append(
            path,
            new(
                CurrentSchemaVersion,
                attemptId,
                "started",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                recordedAtUtc));

    public static void AppendCompleted(
        string path,
        AllocationDirectionalObservation observation,
        DateTimeOffset recordedAtUtc)
    {
        var evidence = observation.AttemptEvidence;
        Append(
            path,
            new(
                CurrentSchemaVersion,
                observation.AttemptId,
                "completed",
                observation.Outcome,
                evidence?.FailureCategory,
                evidence?.Stage,
                evidence?.OperationId,
                evidence?.CleanupState,
                evidence?.RawResponse?.Sha256,
                evidence?.RawResponse?.ByteCount,
                recordedAtUtc));
    }

    public static void ValidateExactTerminalAccounting(
        string path,
        IReadOnlyCollection<string> expectedAttemptIds)
    {
        var entries = File.ReadLines(path)
            .Select(line => JsonSerializer.Deserialize<AllocationDirectionalLedgerEntry>(
                    line,
                    Options)
                ?? throw new InvalidDataException(
                    "Allocation directional ledger contained an empty row."))
            .ToArray();
        if (entries.Length != expectedAttemptIds.Count * 2)
        {
            throw new InvalidDataException(
                "Allocation directional ledger must contain exactly one start and one terminal row per live attempt.");
        }

        var expected = expectedAttemptIds.ToHashSet(StringComparer.Ordinal);
        if (expected.Count != expectedAttemptIds.Count)
        {
            throw new InvalidDataException(
                "Allocation directional expected attempt identities must be unique.");
        }

        for (var index = 0; index < entries.Length; index += 2)
        {
            var started = entries[index];
            var completed = entries[index + 1];
            if (!expected.Remove(started.AttemptId)
                || !string.Equals(
                    started.AttemptId,
                    completed.AttemptId,
                    StringComparison.Ordinal)
                || !string.Equals(started.State, "started", StringComparison.Ordinal)
                || !string.Equals(completed.State, "completed", StringComparison.Ordinal)
                || started.Outcome is not null
                || completed.Outcome is null
                || completed.Stage is null
                || completed.OperationId is null
                || completed.CleanupState is null)
            {
                throw new InvalidDataException(
                    "Allocation directional ledger ordering or no-retry accounting is invalid.");
            }
        }

        if (expected.Count != 0)
        {
            throw new InvalidDataException(
                "Allocation directional ledger omitted a predeclared live attempt.");
        }
    }

    public static string Serialize(
        AllocationDirectionalLedgerEntry entry)
        => JsonSerializer.Serialize(entry, Options);

    private static void Append(
        string path,
        AllocationDirectionalLedgerEntry entry)
        => File.AppendAllText(
            path,
            Serialize(entry) + Environment.NewLine);
}
