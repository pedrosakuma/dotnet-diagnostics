namespace DotnetDiagnostics.Core.Captures;

internal sealed record PortableWorkerIdentity(int ProcessId, string BootId, string PidNamespace, long StartTime)
{
    internal PortableWorkerIdentity(int processId, string bootId, string pidNamespace) : this(processId, bootId, pidNamespace, 0) { }

    internal static PortableWorkerIdentity Capture(int processId)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0) throw Unconfirmed();
        var (bootId, pidNamespace) = CaptureHostIdentity();
        long startTime;
        try { startTime = WorkerProcfsSampler.ReadStartTime(processId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Unconfirmed(ex); }
        return new(processId, bootId, pidNamespace, startTime);
    }

    private static (string BootId, string PidNamespace) CaptureHostIdentity()
    {
        using var boot = new FileStream("/proc/sys/kernel/random/boot_id", FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[64];
        var count = boot.Read(bytes);
        if (count == bytes.Length || boot.ReadByte() != -1) throw Unconfirmed();
        var bootId = CapturePackage.Utf8.GetString(bytes[..count]).TrimEnd('\n');
        var pidNamespace = new FileInfo("/proc/self/ns/pid").LinkTarget;
        if (!Guid.TryParseExact(bootId, "D", out _) || pidNamespace is null ||
            pidNamespace.Length > 64 || !pidNamespace.StartsWith("pid:[", StringComparison.Ordinal))
            throw Unconfirmed();
        return (bootId, pidNamespace);
    }

    internal void RequireGone()
    {
        var (bootId, pidNamespace) = CaptureHostIdentity();
        if (bootId != BootId) return;
        if (pidNamespace != PidNamespace) throw Unconfirmed();
        // A start-time mismatch proves PID reuse. Legacy identities without start time
        // remain conservative. Persisted PIDs are inspected only and never signalled.
        try
        {
            var currentStartTime = WorkerProcfsSampler.ReadStartTime(ProcessId);
            if (StartTime > 0 && currentStartTime != StartTime) return;
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException ex) { throw Unconfirmed(ex); }
        catch (UnauthorizedAccessException ex) { throw Unconfirmed(ex); }
        throw Unconfirmed();
    }

    private static CaptureStoreException Unconfirmed(Exception? cause = null) =>
        CapturePackage.Error(CaptureErrorCode.StorageFailure,
            "ImportWorkerUnconfirmed: retain private staging and reservation; worker exit cannot be established.", cause);
}
