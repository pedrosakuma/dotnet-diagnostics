using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalProjectedAllocation(
    long TotalBytes,
    long TotalEvents,
    string AttributionState,
    IReadOnlyList<AllocationDirectionalRankedRow> Types,
    IReadOnlyList<AllocationDirectionalRankedRow> Sites);

internal sealed record AllocationDirectionalProjectionResult(
    AllocationDirectionalProjectedAllocation? Allocation,
    AllocationDirectionalAttemptEvidence Evidence)
{
    public bool Succeeded => Allocation is not null && Evidence.FailureCategory is null;
}

internal static class AllocationDirectionalProjection
{
    public const string SchemaIdentity =
        "collect_sample:data.kind=allocation:data.allocation/v1";

    public static AllocationDirectionalProjectionResult Project(
        string attemptId,
        string workloadIdentity,
        string raw,
        bool isError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadIdentity);
        ArgumentNullException.ThrowIfNull(raw);

        var rawEvidence = RawEvidence(raw);
        if (isError)
        {
            var error = rawEvidence.ByteCount <= AllocationDirectionalBounds.MaximumRawResponseBytes
                ? ReadIsErrorMetadata(raw)
                : (Kind: (string?)null, DetailState: "not-inspected", DetailByteCount: 0);
            return Failure(
                attemptId,
                workloadIdentity,
                AllocationDirectionalFailureCategory.McpIsError,
                "mcp.call-tool-result-is-error",
                true,
                null,
                error.Kind,
                error.DetailState,
                error.DetailByteCount,
                rawEvidence);
        }

        if (rawEvidence.ByteCount > AllocationDirectionalBounds.MaximumRawResponseBytes)
        {
            return Failure(
                attemptId,
                workloadIdentity,
                AllocationDirectionalFailureCategory.MalformedEnvelope,
                "projection.raw-response-size",
                isError,
                null,
                null,
                "not-inspected",
                0,
                rawEvidence);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return Failure(
                attemptId,
                workloadIdentity,
                AllocationDirectionalFailureCategory.MalformedEnvelope,
                "projection.parse-json",
                isError,
                null,
                null,
                "not-inspected",
                0,
                rawEvidence);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.MalformedEnvelope,
                    "projection.root-object",
                    isError,
                    null,
                    null,
                    "not-inspected",
                    0,
                    rawEvidence);
            }

            var error = ReadError(root);
            if (error.Present)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.McpErrorEnvelope,
                    "mcp.structured-error-envelope",
                    false,
                    null,
                    error.Kind,
                    error.DetailState,
                    error.DetailByteCount,
                    rawEvidence);
            }

            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.MalformedEnvelope,
                    "projection.data-object",
                    false,
                    null,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            if (!data.TryGetProperty("kind", out var kind))
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.MissingRequiredField,
                    "projection.data.kind",
                    false,
                    null,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            if (kind.ValueKind != JsonValueKind.String)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.FieldTypeMismatch,
                    "projection.data.kind-type",
                    false,
                    null,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            if (!string.Equals(kind.GetString(), "allocation", StringComparison.Ordinal))
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.WrongSchema,
                    "projection.data.kind-value",
                    false,
                    null,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            if (!data.TryGetProperty("allocation", out var allocation)
                || allocation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.AllocationUnavailable,
                    "projection.data.allocation",
                    false,
                    SchemaIdentity,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            if (allocation.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalFailureCategory.FieldTypeMismatch,
                    "projection.data.allocation-type",
                    false,
                    SchemaIdentity,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            var required = ReadRequiredFields(allocation);
            if (required.FailureCategory is { } fieldFailure)
            {
                return Failure(
                    attemptId,
                    workloadIdentity,
                    fieldFailure,
                    required.OperationId!,
                    false,
                    SchemaIdentity,
                    null,
                    "absent",
                    0,
                    rawEvidence);
            }

            return new(
                new(
                    required.TotalBytes,
                    required.TotalEvents,
                    required.Sites.Length > 0 ? "complete" : "no-site",
                    required.Types,
                    required.Sites),
                Evidence(
                    attemptId,
                    workloadIdentity,
                    AllocationDirectionalAttemptStage.Completed,
                    "projection.data.allocation-complete",
                    null,
                    false,
                    SchemaIdentity,
                    null,
                    "absent",
                    0,
                    rawEvidence,
                    null,
                    AllocationDirectionalCleanupState.NotStarted));
        }
    }

    public static AllocationDirectionalAttemptEvidence ExternalFailure(
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalAttemptStage stage,
        string operationId,
        AllocationDirectionalFailureCategory category,
        Exception? exception = null,
        AllocationDirectionalCleanupState cleanupState =
            AllocationDirectionalCleanupState.NotStarted)
    {
        ValidateOperationId(operationId);
        return Evidence(
            attemptId,
            workloadIdentity,
            stage,
            operationId,
            category,
            null,
            null,
            null,
            "not-applicable",
            0,
            null,
            exception?.GetType().Name,
            cleanupState);
    }

    public static AllocationDirectionalAttemptEvidence WithCleanup(
        AllocationDirectionalAttemptEvidence evidence,
        AllocationDirectionalCleanupState cleanupState)
        => evidence with { CleanupState = cleanupState };

    private static (
        long TotalBytes,
        long TotalEvents,
        AllocationDirectionalRankedRow[] Types,
        AllocationDirectionalRankedRow[] Sites,
        AllocationDirectionalFailureCategory? FailureCategory,
        string? OperationId) ReadRequiredFields(JsonElement allocation)
    {
        if (!allocation.TryGetProperty("totalBytes", out var totalBytes))
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.MissingRequiredField,
                "projection.data.allocation.totalBytes");
        }

        if (totalBytes.ValueKind != JsonValueKind.Number
            || !totalBytes.TryGetInt64(out var totalBytesValue)
            || totalBytesValue < 0)
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.FieldTypeMismatch,
                "projection.data.allocation.totalBytes-type");
        }

        if (!allocation.TryGetProperty("totalEvents", out var totalEvents))
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.MissingRequiredField,
                "projection.data.allocation.totalEvents");
        }

        if (totalEvents.ValueKind != JsonValueKind.Number
            || !totalEvents.TryGetInt64(out var totalEventsValue)
            || totalEventsValue < 0)
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.FieldTypeMismatch,
                "projection.data.allocation.totalEvents-type");
        }

        if (!allocation.TryGetProperty("topByBytes", out var topByBytes))
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.MissingRequiredField,
                "projection.data.allocation.topByBytes");
        }

        if (topByBytes.ValueKind != JsonValueKind.Array)
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.FieldTypeMismatch,
                "projection.data.allocation.topByBytes-type");
        }

        var types = ReadRows(topByBytes, siteRows: false);
        if (types.FailureCategory is { } typeFailure)
        {
            return FieldFailure(typeFailure, types.OperationId!);
        }

        if (!allocation.TryGetProperty("topBySite", out var topBySite))
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.MissingRequiredField,
                "projection.data.allocation.topBySite");
        }

        if (topBySite.ValueKind != JsonValueKind.Array)
        {
            return FieldFailure(
                AllocationDirectionalFailureCategory.FieldTypeMismatch,
                "projection.data.allocation.topBySite-type");
        }

        var sites = ReadRows(topBySite, siteRows: true);
        if (sites.FailureCategory is { } siteFailure)
        {
            return FieldFailure(siteFailure, sites.OperationId!);
        }

        return (
            totalBytesValue,
            totalEventsValue,
            types.Rows,
            sites.Rows,
            null,
            null);
    }

    private static (
        AllocationDirectionalRankedRow[] Rows,
        AllocationDirectionalFailureCategory? FailureCategory,
        string? OperationId) ReadRows(
        JsonElement rows,
        bool siteRows)
    {
        var result = new List<AllocationDirectionalRankedRow>();
        foreach (var row in rows.EnumerateArray()
                     .Take(AllocationDirectionalBounds.MaximumRankedRows))
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                return RowFailure("row-object");
            }

            string? key;
            if (siteRows)
            {
                if (!row.TryGetProperty("frame", out var frame))
                {
                    return RowFailure("frame", missing: true);
                }

                if (frame.ValueKind != JsonValueKind.Object)
                {
                    return RowFailure("frame-type");
                }

                if (!frame.TryGetProperty("method", out var method))
                {
                    return RowFailure("frame.method", missing: true);
                }

                if (method.ValueKind != JsonValueKind.String)
                {
                    return RowFailure("frame.method-type");
                }

                key = method.GetString();
            }
            else
            {
                if (!row.TryGetProperty("typeName", out var typeName))
                {
                    return RowFailure("typeName", missing: true);
                }

                if (typeName.ValueKind != JsonValueKind.String)
                {
                    return RowFailure("typeName-type");
                }

                key = typeName.GetString();
            }

            if (!row.TryGetProperty("totalBytes", out var totalBytes))
            {
                return RowFailure("totalBytes", missing: true);
            }

            if (totalBytes.ValueKind != JsonValueKind.Number
                || !totalBytes.TryGetInt64(out var bytes)
                || bytes < 0)
            {
                return RowFailure("totalBytes-type");
            }

            if (!row.TryGetProperty("eventCount", out var eventCount))
            {
                return RowFailure("eventCount", missing: true);
            }

            if (eventCount.ValueKind != JsonValueKind.Number
                || !eventCount.TryGetInt64(out var ticks)
                || ticks < 0)
            {
                return RowFailure("eventCount-type");
            }

            result.Add(new(key ?? "<unknown>", bytes, ticks));
        }

        return (result.ToArray(), null, null);

        (
            AllocationDirectionalRankedRow[] Rows,
            AllocationDirectionalFailureCategory? FailureCategory,
            string? OperationId) RowFailure(
            string field,
            bool missing = false)
            => (
                [],
                missing
                    ? AllocationDirectionalFailureCategory.MissingRequiredField
                    : AllocationDirectionalFailureCategory.FieldTypeMismatch,
                $"projection.data.allocation.{(siteRows ? "topBySite" : "topByBytes")}.{field}");
    }

    private static (
        bool Present,
        string? Kind,
        string DetailState,
        int DetailByteCount) ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)
            || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (false, null, "absent", 0);
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return (true, "mcp_error", "malformed-redacted", 0);
        }

        var kind = error.TryGetProperty("kind", out var kindElement)
            && kindElement.ValueKind == JsonValueKind.String
                ? SafeErrorKind(kindElement.GetString())
                : "mcp_error";
        if (!error.TryGetProperty("detail", out var detail)
            || detail.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (true, kind, "absent", 0);
        }

        var byteCount = Encoding.UTF8.GetByteCount(detail.GetRawText());
        return (
            true,
            kind,
            "present-redacted",
            Math.Min(byteCount, AllocationDirectionalBounds.MaximumRawResponseBytes));
    }

    private static (
        string? Kind,
        string DetailState,
        int DetailByteCount) ReadIsErrorMetadata(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "not-inspected", 0);
            }

            var error = ReadError(document.RootElement);
            return error.Present
                ? (error.Kind, error.DetailState, error.DetailByteCount)
                : (null, "absent", 0);
        }
        catch (JsonException)
        {
            return (null, "not-inspected", 0);
        }
    }

    private static string SafeErrorKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > AllocationDirectionalBounds.MaximumErrorKindLength
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('_' or '-')))
        {
            return "redacted";
        }

        return value;
    }

    private static AllocationDirectionalProjectionResult Failure(
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalFailureCategory category,
        string operationId,
        bool? isError,
        string? schemaIdentity,
        string? errorKind,
        string errorDetailState,
        int errorDetailByteCount,
        AllocationDirectionalRawResponseEvidence rawResponse)
        => new(
            null,
            Evidence(
                attemptId,
                workloadIdentity,
                category is AllocationDirectionalFailureCategory.McpIsError
                    or AllocationDirectionalFailureCategory.McpErrorEnvelope
                        ? AllocationDirectionalAttemptStage.McpResponse
                        : AllocationDirectionalAttemptStage.Projection,
                operationId,
                category,
                isError,
                schemaIdentity,
                errorKind,
                errorDetailState,
                errorDetailByteCount,
                rawResponse,
                null,
                AllocationDirectionalCleanupState.NotStarted));

    private static AllocationDirectionalAttemptEvidence Evidence(
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalAttemptStage stage,
        string operationId,
        AllocationDirectionalFailureCategory? failureCategory,
        bool? isError,
        string? schemaIdentity,
        string? errorKind,
        string errorDetailState,
        int errorDetailByteCount,
        AllocationDirectionalRawResponseEvidence? rawResponse,
        string? exceptionType,
        AllocationDirectionalCleanupState cleanupState)
    {
        ValidateOperationId(operationId);
        return new(
            attemptId,
            workloadIdentity,
            stage,
            operationId,
            failureCategory,
            isError,
            schemaIdentity,
            errorKind,
            errorDetailState,
            errorDetailByteCount,
            rawResponse,
            exceptionType,
            cleanupState);
    }

    private static void ValidateOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)
            || operationId.Length > AllocationDirectionalBounds.MaximumOperationIdLength
            || operationId.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationId),
                "Operation identifiers must be bounded source-owned tokens.");
        }
    }

    private static AllocationDirectionalRawResponseEvidence RawEvidence(string raw)
    {
        const int CharacterChunkSize = 2048;
        var buffer = ArrayPool<byte>.Shared.Rent(
            Encoding.UTF8.GetMaxByteCount(CharacterChunkSize));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var offset = 0;
            while (offset < raw.Length)
            {
                var count = Math.Min(CharacterChunkSize, raw.Length - offset);
                if (offset + count < raw.Length
                    && char.IsHighSurrogate(raw[offset + count - 1]))
                {
                    count--;
                }

                var written = Encoding.UTF8.GetBytes(
                    raw.AsSpan(offset, count),
                    buffer);
                hash.AppendData(buffer, 0, written);
                offset += count;
            }

            var byteCount = Encoding.UTF8.GetByteCount(raw);
            return new(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                byteCount,
                AllocationDirectionalRawRetentionState.DigestOnly,
                Truncated: byteCount > 0,
                Redacted: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (
        long TotalBytes,
        long TotalEvents,
        AllocationDirectionalRankedRow[] Types,
        AllocationDirectionalRankedRow[] Sites,
        AllocationDirectionalFailureCategory? FailureCategory,
        string? OperationId) FieldFailure(
        AllocationDirectionalFailureCategory category,
        string operationId)
        => (0, 0, [], [], category, operationId);
}
