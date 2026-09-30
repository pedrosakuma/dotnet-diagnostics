using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

internal static class HistoricalAcceptanceDiagnostics
{
    internal const int MaximumDepth = 4;
    internal const int MaximumFields = 15;
    internal const int MaximumStringLength = 64;
    internal const int MaximumBytes = 4096;
    internal const int MaximumLines = 1;
    private static readonly string[] NumericFields =
    [
        "WorkerSamples", "WorkerWallNs", "WorkerCpuNs", "WorkerPeakRss", "WorkerMaximumObservationGapNs",
    ];
    private static readonly Dictionary<string, string[]> StateFields = new(StringComparer.Ordinal)
    {
        ["WorkerSenderStatus"] = Enum.GetNames<TaskStatus>(),
        ["WorkerReceiverStatus"] = Enum.GetNames<TaskStatus>(),
    };

    internal static async Task RunAsync(Func<Task> action, Action<string> write, HttpReadinessDiagnostics? readiness = null)
    {
        try { await action(); }
        catch (Exception error)
        {
            // Failure reporting must never replace the acceptance exception or bypass its cleanup.
            try { write(Format(error, readiness)); }
            catch (Exception) { }
            throw;
        }
    }

    internal static string Format(Exception error, HttpReadinessDiagnostics? readiness = null)
    {
        var chain = new List<object>();
        var admitted = 0;
        var rejected = 0;
        var fieldsTruncated = false;
        var depth = 0;
        Exception? current = error;
        while (current is not null && depth < MaximumDepth)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var name in NumericFields)
                Add(name, current.Data[name], static value => value is long);
            foreach (var (name, allowed) in StateFields)
                Add(name, current.Data[name], value => value is string text && text.Length <= MaximumStringLength &&
                    allowed.Contains(text, StringComparer.Ordinal));
            chain.Add(new { depth, kind = current is CaptureStoreException ? "CaptureStoreException" : "Exception", fields });
            current = current.InnerException;
            depth++;

            void Add(string name, object? value, Func<object, bool> valid)
            {
                if (value is null) return;
                if (!valid(value)) { rejected++; return; }
                if (admitted == MaximumFields) { fieldsTruncated = true; return; }
                fields.Add(name, value);
                admitted++;
            }
        }
        // Finite state vocabularies redact all unrecognized strings, including bearer,
        // secret, path and payload patterns, rather than retaining partially masked values.
        var json = JsonSerializer.Serialize(new
        {
            schema = "historical-acceptance-failure/v1",
            chain,
            readiness,
            sampleStreams = "not-retained",
            rejectedValues = rejected,
            fieldsTruncated,
            chainTruncated = current is not null,
            truncated = fieldsTruncated || current is not null,
        });
        return Encoding.UTF8.GetByteCount(json) <= MaximumBytes
            ? json : "{\"schema\":\"historical-acceptance-failure/v1\",\"truncated\":true,\"reason\":\"DiagnosticByteLimit\"}";
    }
}
