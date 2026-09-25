using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Compatibility checks for trusted store sources, not an untrusted SQLite admission boundary.</summary>
internal static class PortableSourceValidation
{
    private static readonly string[] Tables = ["format", "strings", "artifacts", "occurrences", "fields", "snapshots"];

    internal static void Validate(CaptureReader reader, CaptureStoreOptions storeOptions,
        PortableCaptureOptions options, long metadataBytes, CancellationToken token)
    {
        long rows = 0;
        foreach (var table in Tables)
        {
            token.ThrowIfCancellationRequested();
            using var command = reader.PortableConnection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM (SELECT 1 FROM {table} LIMIT $limit);";
            command.Parameters.AddWithValue("$limit", (long)options.MaxRowsPerTable + 1);
            var count = (long)command.ExecuteScalar()!;
            token.ThrowIfCancellationRequested();
            PortableBounds.Check("MaxRowsPerTable", count, options.MaxRowsPerTable);
            rows = checked(rows + count);
            PortableBounds.Check("MaxRowsPerCapture", rows, options.MaxRowsPerCapture);
        }
        using (var command = reader.PortableConnection.CreateCommand())
        {
            command.CommandText = "SELECT length(CAST(sql AS BLOB)) FROM sqlite_schema LIMIT 33;";
            using var schema = command.ExecuteReader();
            long count = 0, sqlBytes = 0;
            while (schema.Read())
            {
                token.ThrowIfCancellationRequested();
                PortableBounds.Check("SchemaEntries", ++count, 32);
                sqlBytes += schema.IsDBNull(0) ? 0 : schema.GetInt64(0);
                PortableBounds.Check("SchemaSqlBytes", sqlBytes, 32 * 1024);
            }
        }
        long logicalBytes = 0, totalTokens = 0, records = 0;
        foreach (var artifact in reader.Info.Artifacts)
        {
            token.ThrowIfCancellationRequested();
            if (!CaptureArtifactCodec.SupportsKind(artifact.Kind) && artifact.Kind is not ("batch" or "sweep" or "gc-activities"))
                throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Artifact kind has no portable representation.");
            var snapshot = reader.ReadSnapshot(artifact.ArtifactId);
            token.ThrowIfCancellationRequested();
            if (snapshot is not null)
            {
                var tokens = CountJson(snapshot.Utf8Json.Span, options.MaxTokensPerSnapshot, 64);
                totalTokens = checked(totalTokens + tokens);
                PortableBounds.Check("MaxTokensPerCapture", totalTokens, options.MaxTokensPerCapture);
                // Reserve a conservative owned-graph/decoder workspace before calling the existing codecs.
                PortableBounds.Check("HostRetainedBytes", checked(metadataBytes + snapshot.Utf8Json.Length * 8L + tokens * 256L),
                    PortableBounds.HostBytes);
                ValidateSnapshot(reader.Info, artifact, snapshot, storeOptions);
                logicalBytes = checked(logicalBytes + snapshot.Utf8Json.Length);
            }
            token.ThrowIfCancellationRequested();
            ValidateRecords(reader, artifact, storeOptions, metadataBytes, ref logicalBytes, ref records, token);
            PortableBounds.Check("MaxLogicalBytes", logicalBytes, storeOptions.MaxLogicalBytes);
        }
        if (records != reader.Info.Quality.Persisted)
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Persisted quality does not match the retained occurrence population.");
    }

    private static void ValidateSnapshot(CaptureInfo capture, CaptureArtifactInfo artifact,
        CaptureSnapshot original, CaptureStoreOptions options)
    {
        try
        {
            var snapshot = original.Version == DurableCaptureSnapshotMetadata.Version
                ? DurableCaptureSnapshotMetadata.Decode(artifact.Kind, original, options.MaxSnapshotBytes).Snapshot : original;
            if (snapshot.Version == 0)
            {
                if (!CaptureArtifactCodec.SupportsKind(artifact.Kind) && artifact.Kind is not ("batch" or "sweep" or "gc-activities"))
                    throw new NotSupportedException("Unknown metadata-only artifact kind.");
            }
            else if (snapshot.Version == DurableCaptureCompositionCodec.SnapshotVersion)
                _ = DurableCaptureCompositionCodec.Decode(artifact.Kind, artifact.ArtifactId, snapshot, capture, options);
            else
                _ = CaptureArtifactCodec.Decode(artifact.Kind, snapshot.Version, snapshot.Utf8Json.Span, options.MaxSnapshotBytes);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidDataException or
            ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Snapshot cannot be reopened by the portable codec allowlist.", ex);
        }
    }

    internal static long CountJson(ReadOnlySpan<byte> bytes, long maximum, int depth)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = depth });
        long count = 0;
        while (reader.Read()) PortableBounds.Check("JsonTokens", ++count, maximum);
        if (count == 0) throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Empty JSON metadata.");
        return count;
    }

    private static void ValidateRecords(CaptureReader reader, CaptureArtifactInfo artifact, CaptureStoreOptions options,
        long metadataBytes, ref long logicalBytes, ref long records, CancellationToken token)
    {
        long after = 0, stackBytes = 0;
        var stacks = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var page = reader.Query(new(artifact.ArtifactId, AfterRecordId: after, PageSize: Math.Min(256, options.MaxQueryPageSize)));
            token.ThrowIfCancellationRequested();
            foreach (var entry in page.Records)
            {
                token.ThrowIfCancellationRequested();
                var record = entry.Record;
                long size = 128 + TextBytes(record.Category) + TextBytes(record.Name) + TextBytes(record.Unit);
                foreach (var field in record.Fields ?? [])
                    size = checked(size + 64 + TextBytes(field.Name) + TextBytes(field.StringValue) + TextBytes(field.Unit));
                PortableBounds.Check("MaxRecordBytes", size, options.MaxRecordBytes);
                PortableBounds.Check("MaxFields", record.Fields?.Count ?? 0, options.MaxFields);
                logicalBytes = checked(logicalBytes + size);
                PortableBounds.Check("MaxLogicalBytes", logicalBytes, options.MaxLogicalBytes);
                records++;
                if (artifact.Kind == "cpu-sample" && record.Category == CpuReplayStackObservationWriter.DefinitionCategory)
                {
                    if (record.Name is null || stacks.Contains(record.Name))
                        throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "CPU stack definition is missing or ambiguous.");
                    stackBytes += TextBytes(record.Name) + 128;
                    PortableBounds.Check("PortableMetadataBytes", metadataBytes + stackBytes, 4 * 1024 * 1024);
                    PortableBounds.Check("CpuStackDefinitions", stacks.Count + 1L, CpuReplayStackObservationWriter.MaximumStackDefinitions);
                    stacks.Add(record.Name);
                }
                else if (artifact.Kind == "cpu-sample" && record.Category == CpuReplayStackObservationWriter.SampleCategory &&
                    (record.Name is null || !stacks.Contains(record.Name)))
                    throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "CPU sample has no preceding retained stack definition.");
            }
            if (page.NextAfterRecordId is not { } next) return;
            if (next <= after) throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Record continuation did not advance.");
            after = next;
        }
    }

    private static long TextBytes(string? text) => text is null ? 0 : 24 + 2L * text.Length + CapturePackage.Utf8.GetByteCount(text);
}
