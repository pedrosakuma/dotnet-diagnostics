using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.TestSupport.SqliteCapacity;

internal enum CapacityProfile { Numeric, RepeatedStacks, NovelStacks, Activity }

internal static class CapacityProtocol
{
    internal const int QueueRecords = 4_096;
    internal const int RecordReservationBytes = 512;
    internal const int BatchRecords = 256;
    internal const int BatchBytes = 131_072;
    internal const int BatchAgeMilliseconds = 100;
    internal const int StackLimit = 16_384;
    internal const int FrameLimit = 65_536;
    internal const long DictionaryBytes = 8 * 1_048_576;
    internal const long LogicalBytes = 96 * 1_048_576;
    internal const long PackageBytes = 256 * 1_048_576;
    internal const long WorkspaceBytes = 3L * 1_073_741_824;
    internal const long DiagnosticRssBytes = 512 * 1_048_576;
    internal const int CaseSeconds = 60;
    internal const int OutputBytes = 65_536;
    internal static readonly int[] Rates = [1_000, 10_000, 50_000, 100_000];
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    // Exact UTF-8, LF-separated text below is part of the prospective protocol digest.
    internal const string Configuration = """
        {"protocol":"sqlite-capacity-prototype/2","profiles":["Numeric","RepeatedStacks","NovelStacks","Activity"],"rates":[1000,10000,50000,100000],"windowSeconds":10,"repetitions":3,"warmupRecords":32,"queueRecords":4096,"recordReservationBytes":512,"batchRecords":256,"batchBytes":131072,"batchAgeMilliseconds":100,"batchFill":"available-before-age-check","stackLimit":16384,"frameLimit":65536,"dictionaryBytes":8388608,"logicalBytes":100663296,"packageBytes":268435456,"workspaceBytes":3221225472,"diagnosticRssBytes":536870912,"caseSeconds":60,"outputBytes":65536,"queryRepetitions":5,"journal":"wal","synchronous":2,"indexes":"after-capture","seed":0}
        """;

    internal const string Schema = """
        CREATE TABLE records(
          seq INTEGER PRIMARY KEY, nominal INTEGER NOT NULL, offered INTEGER NOT NULL,
          key_id INTEGER NOT NULL, thread_id INTEGER NOT NULL, stack_id INTEGER NOT NULL,
          trace_id INTEGER NOT NULL, value INTEGER NOT NULL, profile INTEGER NOT NULL);
        CREATE TABLE frames(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE stack_frames(stack_id INTEGER NOT NULL, ordinal INTEGER NOT NULL,
          frame_id INTEGER NOT NULL REFERENCES frames(id), PRIMARY KEY(stack_id,ordinal));
        CREATE TABLE attributes(seq INTEGER NOT NULL REFERENCES records(seq), key_id INTEGER NOT NULL,
          value INTEGER NOT NULL, PRIMARY KEY(seq,key_id));
        """;

    internal const string Indexes = """
        CREATE INDEX ix_time ON records(nominal,seq);
        CREATE INDEX ix_key_time ON records(key_id,nominal,seq);
        CREATE INDEX ix_thread_time ON records(thread_id,nominal,seq);
        CREATE INDEX ix_stack_time ON records(stack_id,nominal,seq);
        CREATE INDEX ix_trace_time ON records(trace_id,nominal,seq);
        """;

    internal static readonly string[] Queries =
    [
        "SELECT count(*) FROM records WHERE nominal >= $lo AND nominal < $hi",
        "SELECT count(*) FROM records WHERE key_id=7 AND nominal >= $lo AND nominal < $hi",
        "SELECT count(*) FROM records WHERE thread_id=3 AND nominal >= $lo AND nominal < $hi",
        "SELECT count(*) FROM records WHERE stack_id=$group AND nominal >= $lo AND nominal < $hi",
        "SELECT count(*) FROM records WHERE trace_id=$group AND nominal >= $lo AND nominal < $hi"
    ];
    internal static readonly string[] QueryIndexes = ["ix_time", "ix_key_time", "ix_thread_time", "ix_stack_time", "ix_trace_time"];
    internal static string Hash => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Configuration + "\n" + Schema + "\n" + Indexes + "\n" + string.Join('\n', Queries) + "\n")));

    internal static long NominalSlot(long sequence, int rate, long frequency)
        => checked(sequence * frequency / rate);

    internal static CapacityRecord Generate(CapacityProfile profile, long sequence, long nominal, long offered)
        => new(sequence, nominal, offered, sequence % 64, sequence % 8,
            profile switch { CapacityProfile.RepeatedStacks => sequence % 128, CapacityProfile.NovelStacks => sequence, _ => -1 },
            profile == CapacityProfile.Activity ? sequence / 8 : -1, sequence % 97, (long)profile);

    internal static string FrameName(long id) => "synthetic-frame-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static long AttributeValue(long sequence, int key) => checked(sequence * 4 + key);

    internal static void WriteReport(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ReportOptions);
        if (bytes.Length > OutputBytes)
            throw new InvalidOperationException("output-cap");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}

internal readonly record struct CapacityRecord(
    long Sequence, long Nominal, long Offered, long Key, long Thread, long Stack, long Trace, long Value, long Profile)
{
    internal int LogicalBytes => Profile == (long)CapacityProfile.Activity ? 136 : 72;
    internal long[] Columns() => [Sequence, Nominal, Offered, Key, Thread, Stack, Trace, Value, Profile];

    internal void AppendTo(IncrementalHash records, IncrementalHash attributes)
    {
        CapacityHash.Append(records, Sequence, Nominal, Offered, Key, Thread, Stack, Trace, Value, Profile);
        if (Profile == (long)CapacityProfile.Activity)
            for (var key = 0; key < 4; key++)
                CapacityHash.Append(attributes, Sequence, key, CapacityProtocol.AttributeValue(Sequence, key));
    }
}

internal static class CapacityHash
{
    internal static void Append(IncrementalHash hash, params ReadOnlySpan<long> values)
    {
        Span<byte> bytes = stackalloc byte[8];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }

    internal static string Finish(IncrementalHash hash) => Convert.ToHexStringLower(hash.GetHashAndReset());
}

internal sealed class CapacityHistogram
{
    // Upper bounds in microseconds, followed by an overflow bucket. No per-event samples.
    public long[] UpperMicroseconds { get; } = [10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000];
    public long[] Counts { get; } = new long[8];
    public long Count { get; private set; }
    public double MaximumMicroseconds { get; private set; }

    internal void AddTicks(long ticks)
    {
        var us = ticks * 1_000_000d / Stopwatch.Frequency;
        var bucket = 0;
        while (bucket < UpperMicroseconds.Length && us > UpperMicroseconds[bucket])
            bucket++;
        Counts[bucket]++;
        Count++;
        MaximumMicroseconds = Math.Max(MaximumMicroseconds, us);
    }
}

internal sealed class CapacityQueue
{
    private readonly Queue<CapacityRecord> _records = new();
    private readonly object _gate = new();
    internal int PeakRecords { get; private set; }
    internal bool Completed { get; private set; }

    internal bool TryOffer(CapacityRecord record)
    {
        lock (_gate)
        {
            if (Completed || record.LogicalBytes > CapacityProtocol.RecordReservationBytes
                || _records.Count >= CapacityProtocol.QueueRecords)
                return false;
            _records.Enqueue(record);
            PeakRecords = Math.Max(PeakRecords, _records.Count);
            Monitor.Pulse(_gate);
            return true;
        }
    }

    internal bool TryTake(out CapacityRecord record)
    {
        lock (_gate)
            return _records.TryDequeue(out record);
    }

    internal bool IsDrained
    {
        get { lock (_gate) return Completed && _records.Count == 0; }
    }

    internal int Count { get { lock (_gate) return _records.Count; } }

    internal void Complete()
    {
        lock (_gate)
        {
            Completed = true;
            Monitor.Pulse(_gate);
        }
    }
}

internal sealed class CapacityResult
{
    public string ProtocolHash { get; set; } = CapacityProtocol.Hash;
    public string Profile { get; set; } = "";
    public bool ProducerOnly { get; set; }
    public bool ComponentOnly { get; set; }
    public string Outcome { get; set; } = "incomplete";
    public string? Failure { get; set; }
    public long Frequency { get; set; } = Stopwatch.Frequency;
    public long DeadlineTimestamp { get; set; }
    public long Planned { get; set; }
    public long Offered { get; set; }
    public long OfferedLogicalRecordBytes { get; set; }
    public long Admitted { get; set; }
    public long AdmittedLogicalRecordBytes { get; set; }
    public long QueueRejected { get; set; }
    public long DictionaryRejected { get; set; }
    public long LogicalCapRejected { get; set; }
    public long Committed { get; set; }
    public long ControlConsumed { get; set; }
    public long Unknown { get; set; }
    public long NotOffered => Planned - Offered;
    public long SqlRows { get; set; }
    public long RetainedLogicalRecordBytes { get; set; }
    public long CommittedLogicalRecordBytes => ProducerOnly ? 0 : RetainedLogicalRecordBytes;
    public long ControlLogicalRecordBytes => ProducerOnly ? RetainedLogicalRecordBytes : 0;
    public long DictionaryLogicalBytes { get; set; }
    public int QueuePeakRecords { get; set; }
    public int QueueAtSourceEnd { get; set; }
    public long[] SampledQueueDepthPerSecond { get; set; } = Enumerable.Repeat(-1L, 61).ToArray();
    public long QueuePeakReservationBytes => (long)QueuePeakRecords * CapacityProtocol.RecordReservationBytes;
    public long FirstOfferedTick { get; set; } = -1;
    public long LastOfferedTick { get; set; } = -1;
    public long SourceElapsedTicks { get; set; }
    public long AcquisitionWindowTicks { get; set; }
    public long[] OfferedPerSecond { get; set; } = new long[11];
    public long[] CommittedPerSecond { get; set; } = new long[61];
    public double ObservedOfferedPerSecond => SourceElapsedTicks == 0 ? 0 : Offered * (double)Frequency / SourceElapsedTicks;
    public double ObservedCommittedPerSecond => WriterThroughDrainTicks == 0 ? 0 : Committed * (double)Frequency / WriterThroughDrainTicks;
    public double ObservedCommittedSqlRowsPerSecond => WriterThroughDrainTicks == 0 ? 0 : SqlRows * (double)Frequency / WriterThroughDrainTicks;
    public double ObservedCommittedLogicalBytesPerSecond => ProducerOnly || WriterThroughDrainTicks == 0
        ? 0 : (RetainedLogicalRecordBytes + DictionaryLogicalBytes) * (double)Frequency / WriterThroughDrainTicks;
    public double OfferedPerDeclaredWindowSecond => AcquisitionWindowTicks == 0 ? 0 : Offered * (double)Frequency / AcquisitionWindowTicks;
    public long WriterThroughDrainTicks { get; set; }
    public long DrainTicks { get; set; }
    public long IndexTicks { get; set; }
    public long CheckpointTicks { get; set; }
    public long FinalizationTicks { get; set; }
    public long WorkerElapsedTicks { get; set; }
    public long WorkerCpuTicks { get; set; }
    public long WorkerAllocatedBytes { get; set; }
    public long WorkerFinalRssBytes { get; set; }
    public long DatabaseBytes { get; set; }
    public long PeakSampledPackageBytes { get; set; }
    public long PeakSampledWalBytes { get; set; }
    public long DatabaseBytesBeforeIndexes { get; set; }
    public long IndexPageBytes { get; set; }
    public string SqliteVersion { get; set; } = "";
    public string ProviderVersion { get; set; } = "";
    public string RuntimeVersion { get; set; } = Environment.Version.ToString();
    public Dictionary<string, string> Pragmas { get; set; } = [];
    public CapacityHistogram OfferLateness { get; set; } = new();
    public CapacityHistogram CommitLatency { get; set; } = new();
    public CapacityHistogram OfferedToCommit { get; set; } = new();
    public long[] QueryOracle { get; set; } = new long[5];
    public string RecordHash { get; set; } = "";
    public string AttributeHash { get; set; } = "";
    public string DictionaryHash { get; set; } = "";
    public long QueryLow { get; set; }
    public long QueryHigh { get; set; }
    public long QueryStack { get; set; } = 3;
    public long QueryTrace { get; set; } = 3;
    public string[] Unknowns { get; set; } =
    [
        "No runtime/provider source events: offered counts are synthetic logical observations.",
        "Transient native SQLite/temp/OS-cache peaks and instantaneous physical quotas are unknown.",
        "Worker CPU/allocation/RSS includes producer, writer and oracle; writer-thread-only CPU is unavailable.",
        "No target latency, live impact, actual collector demand or production viability is measured.",
        "Read-only reopen is warm/unspecified OS cache, not a cold-cache measurement."
    ];
}
