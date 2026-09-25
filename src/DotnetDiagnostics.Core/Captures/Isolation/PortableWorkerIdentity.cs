namespace DotnetDiagnostics.Core.Captures;

internal sealed record PortableWorkerIdentity(int ProcessId, string BootId, string PidNamespace)
{
    internal static PortableWorkerIdentity Capture(int processId)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0) throw Unconfirmed();
        using var boot = new FileStream("/proc/sys/kernel/random/boot_id", FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[64];
        var count = boot.Read(bytes);
        if (count == bytes.Length || boot.ReadByte() != -1) throw Unconfirmed();
        var bootId = CapturePackage.Utf8.GetString(bytes[..count]).TrimEnd('\n');
        var pidNamespace = new FileInfo("/proc/self/ns/pid").LinkTarget;
        if (!Guid.TryParseExact(bootId, "D", out _) || pidNamespace is null ||
            pidNamespace.Length > 64 || !pidNamespace.StartsWith("pid:[", StringComparison.Ordinal))
            throw Unconfirmed();
        return new(processId, bootId, pidNamespace);
    }

    internal void RequireGone()
    {
        var current = Capture(ProcessId);
        if (current.BootId != BootId) return;
        if (current.PidNamespace != PidNamespace) throw Unconfirmed();
        // A reused PID is conservatively retained too. Never signal a persisted PID,
        // adopt another process, or treat an inaccessible procfs entry as death.
        try
        {
            using var process = new FileStream(
                FormattableString.Invariant($"/proc/{ProcessId}/stat"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (FileNotFoundException) { return; }
        catch (IOException ex) { throw Unconfirmed(ex); }
        catch (UnauthorizedAccessException ex) { throw Unconfirmed(ex); }
        throw Unconfirmed();
    }

    private static CaptureStoreException Unconfirmed(Exception? cause = null) =>
        CapturePackage.Error(CaptureErrorCode.StorageFailure,
            "ImportWorkerUnconfirmed: retain private staging and reservation; worker exit cannot be established.", cause);
}
