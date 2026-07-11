using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal sealed class ClrMdRuntimeSession : IDisposable
{
    private ClrMdRuntimeSession(DataTarget target, ClrInfo clrInfo, ClrRuntime runtime, int processId)
    {
        Target = target;
        ClrInfo = clrInfo;
        Runtime = runtime;
        ProcessId = processId;
    }

    public DataTarget Target { get; }

    public ClrInfo ClrInfo { get; }

    public ClrRuntime Runtime { get; }

    public int ProcessId { get; }

    public static ClrMdRuntimeSession AttachLive(int processId)
    {
        var target = DataTarget.AttachToProcess(processId, suspend: true);
        try
        {
            var clrInfo = target.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException($"Process {processId} does not expose a CLR runtime (NativeAOT or non-managed).");
            var runtime = clrInfo.CreateRuntime();
            return new ClrMdRuntimeSession(target, clrInfo, runtime, processId);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public static ClrMdRuntimeSession LoadDump(string dumpFilePath)
    {
        var target = DataTarget.LoadDump(dumpFilePath);
        try
        {
            var clrInfo = target.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException("Dump does not contain a CLR runtime.");
            var runtime = clrInfo.CreateRuntime();
            return new ClrMdRuntimeSession(target, clrInfo, runtime, unchecked((int)target.DataReader.ProcessId));
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public static ClrMdRuntimeSession OpenSnapshot(HeapSnapshotArtifact snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Origin switch
        {
            HeapSnapshotOrigin.Dump when !string.IsNullOrWhiteSpace(snapshot.DumpFilePath) && File.Exists(snapshot.DumpFilePath)
                => LoadDump(snapshot.DumpFilePath),
            HeapSnapshotOrigin.Live when snapshot.ProcessId > 0
                => AttachLive(snapshot.ProcessId),
            HeapSnapshotOrigin.Dump => throw new InvalidOperationException("The originating dump file is unavailable for this heap snapshot handle."),
            _ => throw new InvalidOperationException("The originating live process is unavailable for this heap snapshot handle."),
        };
    }

    public void Dispose()
    {
        Runtime.Dispose();
        Target.Dispose();
    }
}
