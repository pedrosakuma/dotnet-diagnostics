using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuEfficiency;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>
/// Versioned, allowlisted compatibility snapshots, not an occurrence log. Decoding performs
/// no file access, process attachment, symbol resolution, or runtime type-name activation.
/// </summary>
internal static class CaptureArtifactCodec
{
    internal const int FormatVersion = 1;
    internal const int MaximumDepth = 64;

    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static readonly Dictionary<string, Codec> Codecs = new(StringComparer.Ordinal)
    {
        [CollectionHandleKinds.Counters] = new Codec<CounterSnapshot>(),
        [CollectionHandleKinds.ExceptionSnapshot] = new Codec<ExceptionSnapshot>(),
        [CollectionHandleKinds.CrashGuardSnapshot] = new Codec<CrashGuardSnapshot>(),
        [CollectionHandleKinds.GcEvents] = new Codec<GcSummary>(),
        [CollectionHandleKinds.GcDatas] = new Codec<GcDatasSnapshot>(),
        [CollectionHandleKinds.EventSource] = new Codec<EventSourceCapture>(),
        [CollectionHandleKinds.EventCatalog] = new Codec<EventCatalogSnapshot>(),
        [CollectionHandleKinds.Activities] = new Codec<ActivityCapture>(),
        [CollectionHandleKinds.LogSnapshot] = new Codec<LogSnapshot>(),
        [CollectionHandleKinds.JitSnapshot] = new Codec<JitSnapshot>(),
        [CollectionHandleKinds.ThreadPoolSnapshot] = new Codec<ThreadPoolEventSnapshot>(),
        [CollectionHandleKinds.ContentionSnapshot] = new Codec<ContentionSnapshot>(),
        [CollectionHandleKinds.DbSnapshot] = new Codec<DbSnapshot>(),
        [CollectionHandleKinds.KestrelSnapshot] = new Codec<KestrelSnapshot>(),
        [CollectionHandleKinds.NetworkingSnapshot] = new Codec<NetworkingSnapshot>(),
        [CollectionHandleKinds.InFlightRequests] = new Codec<InFlightRequestSnapshot>(),
        [CollectionHandleKinds.StartupSnapshot] = new Codec<StartupSnapshot>(),
        ["cpu-sample"] = new Codec<CpuSampleTraceArtifact>(),
        ["allocation-sample"] = new Codec<AllocationSampleArtifact>(),
        [SamplerUseCases.OffCpuHandleKind] = new Codec<OffCpuSnapshotArtifact>(),
        [SamplerUseCases.NativeAllocHandleKind] = new Codec<CpuSampleTraceArtifact>(),
        [SamplerUseCases.NativeLockContentionHandleKind] = new Codec<NativeLockContentionArtifact>(),
        [SamplerUseCases.CpuEfficiencyHandleKind] = new Codec<CpuEfficiencySample>(),
        [MethodParameterCaptureUseCases.HandleKind] = new Codec<MethodParameterCaptureArtifact>(),
        [SamplerUseCases.ThreadSnapshotKind] = new ThreadCodec(),
        [HeapInspectionUseCases.HeapSnapshotKind] = new Codec<HeapSnapshotArtifact>(),
        // This inspect_process result has no existing handle; its view name is the discriminator.
        ["requests-now"] = new Codec<RequestsNowSnapshot>(),
    };

    internal static byte[] Encode(string kind, object artifact, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var codec = Find(kind);
        codec.Validate(artifact);
        using var buffer = new BoundedSnapshotEncoding(maxBytes);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaximumDepth }))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WritePropertyName("snapshot");
            codec.Write(writer, artifact, maxBytes);
            writer.WriteEndObject();
            writer.Flush();
        }
        // Reject invalid in-memory DTO states as well: never persist something whose
        // required nulls would be rejected (or normalized by constructors) on restoration.
        ValidateJson(buffer.WrittenSpan);
        var validationReader = new Utf8JsonReader(buffer.WrittenSpan, new JsonReaderOptions { MaxDepth = MaximumDepth });
        ReadHeader(ref validationReader, kind);
        codec.ValidatePayload(ref validationReader);
        return buffer.ToArray();
    }

    internal static object Decode(string kind, int representationVersion, ReadOnlySpan<byte> bytes, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var codec = Find(kind);
        if (representationVersion != FormatVersion)
            throw new NotSupportedException($"Unsupported capture snapshot representation version {representationVersion}.");
        if (bytes.Length > maxBytes)
            throw new InvalidDataException($"Capture snapshot exceeds maxBytes={maxBytes}.");

        // Validate the entire input, including depth and duplicate members, before allocating DTOs.
        ValidateJson(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumDepth });
        ReadHeader(ref reader, kind);
        var result = codec.Read(ref reader);
        Expect(ref reader, JsonTokenType.EndObject);
        if (reader.Read()) throw new JsonException("Trailing snapshot data.");
        return result;
    }

    private static void ReadHeader(ref Utf8JsonReader reader, string kind)
    {
        Expect(ref reader, JsonTokenType.StartObject);
        ExpectProperty(ref reader, "kind");
        Expect(ref reader, JsonTokenType.String);
        if (!reader.ValueTextEquals(kind))
            throw new JsonException("Snapshot kind does not match the requested codec.");
        ExpectProperty(ref reader, "snapshot");
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a snapshot object.");
    }

    internal static IReadOnlyList<string> GetSupportedSnapshotViews(string kind, object artifact)
    {
        Find(kind).Validate(artifact);
        return kind switch
        {
            "cpu-sample" or "allocation-sample" or SamplerUseCases.NativeAllocHandleKind
                or SamplerUseCases.NativeLockContentionHandleKind => CpuSampleQueryDispatcher.SessionViews,
            SamplerUseCases.OffCpuHandleKind => OffCpuQueryDispatcher.SessionViews,
            SamplerUseCases.ThreadSnapshotKind => ThreadSnapshotQueryDispatcher.SessionViews,
            HeapInspectionUseCases.HeapSnapshotKind => HeapViews((HeapSnapshotArtifact)artifact),
            CollectionHandleKinds.GcDatas => GcDatasQueryDispatcher.SessionViews,
            CollectionHandleKinds.EventCatalog => EventCatalogQueryDispatcher.SessionViews,
            MethodParameterCaptureUseCases.HandleKind => MethodParameterCaptureQueryDispatcher.ViewsFor(kind),
            // There is no existing query_snapshot dispatcher for these aggregate/returned-only results.
            SamplerUseCases.CpuEfficiencyHandleKind or "requests-now" => [],
            // gc-overlay requires a separately retained GC artifact, not this snapshot alone.
            CollectionHandleKinds.Activities => CollectionQueryDispatcher.ViewsFor(kind).Where(v => v != "gc-overlay").ToArray(),
            _ => CollectionQueryDispatcher.ViewsFor(kind),
        };
    }

    private static List<string> HeapViews(HeapSnapshotArtifact snapshot)
    {
        var views = new List<string> { "top-types" };
        if (snapshot.Origin == HeapSnapshotOrigin.GcDump) return views;
        if (snapshot.RetentionPaths is not null) views.Add("retention-paths");
        if (snapshot.RootsByKind is not null) views.Add("roots-by-kind");
        if (snapshot.FinalizableObjectsByType is not null) views.Add("finalizer-queue");
        if (snapshot.Segments is not null) views.Add("fragmentation");
        if (snapshot.StaticFields is not null) views.Add("static-fields");
        if (snapshot.DelegateTargets is not null) views.Add("delegate-targets");
        if (snapshot.GcHandles is not null) views.Add("gchandles");
        if (snapshot.AsyncOperations is not null) views.Add("async");
        if (snapshot.Timers is not null) views.Add("timers");
        if (snapshot.AssemblyLoadContexts is not null) views.Add("alc");
        return views;
    }

    private static Codec Find(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Codecs.TryGetValue(kind, out var codec)
            ? codec : throw new NotSupportedException($"Unsupported capture snapshot kind '{kind}'.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            MaxDepth = MaximumDepth,
            IgnoreReadOnlyProperties = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new CallTreeSnapshotEncoding());
        options.Converters.Add(new StringSnapshotEncoding());
        options.Converters.Add(new SymbolMapSnapshotEncoding<Memory.SourceLocation>());
        options.Converters.Add(new SymbolMapSnapshotEncoding<Memory.MethodIdentity>());
        options.TypeInfoResolver = CaptureSnapshotEncodingContext.Default.WithAddedModifier(static info =>
        {
            // Version 1 writes every stored property, including explicit nulls. Missing fields
            // must not silently become zero/default or manufacture evidence on restoration.
            for (var i = info.Properties.Count - 1; i >= 0; i--)
            {
                var property = info.Properties[i];
                // IgnoreReadOnlyProperties does not exclude computed collection properties.
                // Their canonical stored inputs, not the computed projection, are persisted.
                if (property.Get is not null && property.Set is null)
                    info.Properties.RemoveAt(i);
                else if (property.Get is not null && property.Set is not null)
                    property.IsRequired = true;
            }
        });
        options.MakeReadOnly();
        return options;
    }

    private static void ValidateJson(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumDepth });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new JsonException("Duplicate snapshot property.");
        }
        if (reader.BytesConsumed == 0) throw new JsonException("Empty snapshot.");
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (!reader.Read() || reader.TokenType != type) throw new JsonException($"Expected {type}.");
    }

    private static void ExpectProperty(ref Utf8JsonReader reader, string name)
    {
        Expect(ref reader, JsonTokenType.PropertyName);
        if (!reader.ValueTextEquals(name)) throw new JsonException($"Expected property '{name}'.");
    }

    private abstract class Codec(Type artifactType)
    {
        internal void Validate(object artifact)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (artifact.GetType() != artifactType)
                throw new ArgumentException($"Expected the allowlisted artifact type {artifactType.Name}.", nameof(artifact));
        }
        internal abstract void Write(Utf8JsonWriter writer, object artifact, int maxBytes);
        internal abstract object Read(ref Utf8JsonReader reader);
        internal abstract void ValidatePayload(ref Utf8JsonReader reader);
    }

    private sealed class Codec<T>() : Codec(typeof(T)) where T : class
    {
        internal override void Write(Utf8JsonWriter writer, object artifact, int maxBytes)
            => JsonSerializer.Serialize(writer, (T)artifact, Options);
        internal override object Read(ref Utf8JsonReader reader)
        {
            var validationReader = reader;
            ValidatePayload(ref validationReader);
            return JsonSerializer.Deserialize<T>(ref reader, Options) ?? throw new JsonException("Null snapshot.");
        }
        internal override void ValidatePayload(ref Utf8JsonReader reader)
            => SnapshotEncodingValidation.Validate(ref reader, typeof(T), Options);
    }

    private sealed class ThreadCodec() : Codec(typeof(ThreadSnapshotArtifact))
    {
        internal override void Write(Utf8JsonWriter writer, object artifact, int maxBytes)
        {
            var snapshot = (ThreadSnapshotArtifact)artifact;
            if (snapshot.Threads is null) throw new JsonException("Null thread list.");
            if (snapshot.Threads.Count > maxBytes) throw new InvalidDataException("Thread rows exceed snapshot byte budget.");
            JsonSerializer.Serialize(writer, ThreadSnapshotEncoding.From(snapshot), Options);
        }
        internal override object Read(ref Utf8JsonReader reader)
        {
            var validationReader = reader;
            ValidatePayload(ref validationReader);
            return (JsonSerializer.Deserialize<ThreadSnapshotEncoding>(ref reader, Options)
                ?? throw new JsonException("Null thread snapshot.")).Restore();
        }
        internal override void ValidatePayload(ref Utf8JsonReader reader)
            => SnapshotEncodingValidation.Validate(ref reader, typeof(ThreadSnapshotEncoding), Options);
    }
}
