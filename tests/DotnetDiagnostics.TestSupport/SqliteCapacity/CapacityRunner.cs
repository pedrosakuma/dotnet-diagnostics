using System.Diagnostics;
using System.Text.Json;

namespace DotnetDiagnostics.TestSupport.SqliteCapacity;

internal static class CapacityRunner
{
    internal static CapacityResult RunWorker(string root, CapacityProfile profile, int rate, bool producerOnly,
        long deadline, int componentRecords = 0)
    {
        if (!Enum.IsDefined(profile) || !CapacityProtocol.Rates.Contains(rate)
            || componentRecords is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(rate));
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("worker-requires-empty-owned-directory");
        var processStart = Stopwatch.GetTimestamp();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime.Ticks;
        var allocatedStart = GC.GetTotalAllocatedBytes();
        var window = componentRecords == 0 ? 10 * Stopwatch.Frequency : Stopwatch.Frequency / 10;
        var result = new CapacityResult
        {
            Profile = profile.ToString(), ProducerOnly = producerOnly, ComponentOnly = componentRecords != 0,
            DeadlineTimestamp = deadline,
            Planned = componentRecords == 0 ? 10L * rate : componentRecords,
            AcquisitionWindowTicks = window,
            QueryLow = componentRecords == 0 ? window / 4 : 0,
            QueryHigh = componentRecords == 0 ? window * 3 / 4 : window,
            QueryStack = componentRecords == 0 && profile == CapacityProfile.NovelStacks ? 5L * rate : 3,
            QueryTrace = componentRecords == 0 ? 5L * rate / 8 : 3
        };
        var queue = new CapacityQueue();
        using var stop = new CancellationTokenSource();
        Task? producer = null;
        try
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("worker-deadline");
            using var store = new CapacityStore(Path.Combine(root, "records.sqlite"), result, processStart, deadline);
            var sourceStart = Stopwatch.GetTimestamp();
            store.StartAcquisition(sourceStart);
            producer = Task.Run(() => Produce(queue, result, profile, rate, sourceStart, stop.Token));
            var batch = new List<CapacityRecord>(CapacityProtocol.BatchRecords);
            long oldest = 0;
            long previousSecond = -1;
            while (!queue.IsDrained || batch.Count != 0)
            {
                var now = Stopwatch.GetTimestamp();
                if (now >= deadline)
                    throw new TimeoutException("worker-deadline");
                var second = Math.Min(60, (now - sourceStart) / Stopwatch.Frequency);
                if (second != previousSecond)
                {
                    result.SampledQueueDepthPerSecond[second] = queue.Count;
                    previousSecond = second;
                }
                var tookRecord = queue.TryTake(out var record);
                if (tookRecord)
                {
                    if (batch.Count == 0) oldest = sourceStart + record.Offered;
                    batch.Add(record);
                }
                var age = Stopwatch.GetTimestamp() - oldest;
                if (batch.Count != 0 && (batch.Count == CapacityProtocol.BatchRecords
                    || queue.IsDrained
                    || age >= Stopwatch.Frequency * CapacityProtocol.BatchAgeMilliseconds / 1_000))
                {
                    store.Commit(batch);
                    batch.Clear();
                }
                else if (!tookRecord)
                    Thread.Sleep(1);
            }
            producer.GetAwaiter().GetResult();
            var drainEnd = Stopwatch.GetTimestamp();
            result.WriterThroughDrainTicks = drainEnd - sourceStart;
            result.DrainTicks = Math.Max(0, result.WriterThroughDrainTicks - result.SourceElapsedTicks);
            store.Seal();
            result.Outcome = result.NotOffered > 0 ? "source-undercoverage"
                : result.QueueRejected > 0 ? "queue-overload"
                : result.DictionaryRejected > 0 ? "dictionary-cap"
                : result.LogicalCapRejected > 0 ? "logical-cap"
                : producerOnly ? "control-complete" : "complete-not-production-approval";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result.Outcome = "failed";
            result.Failure = exception.GetType().Name + ":" + exception.Message[..Math.Min(exception.Message.Length, 512)];
        }
        finally
        {
            stop.Cancel();
            if (producer is not null)
                producer.GetAwaiter().GetResult();
            result.Unknown = result.Admitted - result.Committed - result.ControlConsumed
                - result.DictionaryRejected - result.LogicalCapRejected;
            result.QueuePeakRecords = queue.PeakRecords;
            result.WorkerElapsedTicks = Stopwatch.GetTimestamp() - processStart;
            result.WorkerCpuTicks = process.TotalProcessorTime.Ticks - cpuStart;
            result.WorkerAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedStart;
            process.Refresh();
            result.WorkerFinalRssBytes = process.WorkingSet64;
        }
        CapacityProtocol.WriteReport(Path.Combine(root, "worker.json"), result);
        return result;
    }

    private static void Produce(CapacityQueue queue, CapacityResult result, CapacityProfile profile,
        int rate, long start, CancellationToken stop)
    {
        try
        {
            for (long sequence = 0; sequence < result.Planned; sequence++)
            {
                var nominal = CapacityProtocol.NominalSlot(sequence, rate, Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() - start < nominal && !stop.IsCancellationRequested)
                {
                    if (nominal - (Stopwatch.GetTimestamp() - start) > Stopwatch.Frequency / 500)
                        Thread.Sleep(1);
                    else
                        Thread.SpinWait(64);
                }
                var offered = Stopwatch.GetTimestamp() - start;
                if (stop.IsCancellationRequested || offered >= result.AcquisitionWindowTicks)
                    break; // NotOffered preserves every unvisited nominal slot; never reschedule against the writer.
                var record = CapacityProtocol.Generate(profile, sequence, nominal, offered);
                result.Offered++;
                result.OfferedLogicalRecordBytes += record.LogicalBytes;
                if (result.FirstOfferedTick < 0) result.FirstOfferedTick = offered;
                result.LastOfferedTick = offered;
                result.OfferedPerSecond[Math.Min(10, offered / Stopwatch.Frequency)]++;
                result.OfferLateness.AddTicks(Math.Max(0, offered - nominal));
                if (queue.TryOffer(record))
                {
                    result.Admitted++;
                    result.AdmittedLogicalRecordBytes += record.LogicalBytes;
                }
                else result.QueueRejected++;
            }
        }
        finally
        {
            result.SourceElapsedTicks = Stopwatch.GetTimestamp() - start;
            result.QueueAtSourceEnd = queue.Count;
            queue.Complete();
        }
    }

    internal static CapacityVerification VerifyWorker(string root, long deadline)
    {
        var path = Path.Combine(root, "worker.json");
        if (new FileInfo(path).Length > CapacityProtocol.OutputBytes)
            throw new InvalidOperationException("worker-output-cap");
        var expected = JsonSerializer.Deserialize<CapacityResult>(File.ReadAllBytes(path))
            ?? throw new InvalidOperationException("missing-worker-result");
        if (expected.Outcome == "failed" || expected.Unknown != 0 || expected.ProducerOnly)
            throw new InvalidOperationException("not-a-sealed-database-result");
        return CapacityReadonly.Verify(Path.GetFullPath(Path.Combine(root, "records.sqlite")), expected, deadline);
    }
}
