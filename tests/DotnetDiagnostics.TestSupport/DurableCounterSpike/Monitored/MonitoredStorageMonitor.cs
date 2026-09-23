using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal enum MonitoredProcessRole
{
    Harness,
    Diagnostic,
    Target,
}

internal sealed record MonitoredProcessIdentity(
    int ProcessId,
    ulong LinuxStartTimeTicks,
    MonitoredProcessRole Role)
{
    internal static MonitoredProcessIdentity Capture(Process process, MonitoredProcessRole role)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new MonitoredProcessIdentity(
            process.Id,
            LinuxProcessIdentity.ReadStartTime(process.Id),
            role);
    }
}

internal static class LinuxProcessIdentity
{
    internal static ulong ReadStartTime(int processId)
    {
        LinuxStatxHandleMetadataObserver.EnsureSupportedPlatform(OperatingSystem.IsLinux());
        var statPath = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat";
        string stat;
        try
        {
            stat = File.ReadAllText(statPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DurableStorageExperimentException(
                "ProcessIdentityUnavailable",
                $"Could not read Linux process identity for pid {processId}: {exception.GetType().Name}.");
        }
        var close = stat.LastIndexOf(')');
        if (close < 0 || close + 2 >= stat.Length)
        {
            throw new DurableStorageExperimentException(
                "InvalidProcessIdentity",
                $"Linux process identity for pid {processId} had an invalid stat shape.");
        }
        var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int startTimeIndexAfterCommand = 19;
        if (fields.Length <= startTimeIndexAfterCommand
            || !ulong.TryParse(
                fields[startTimeIndexAfterCommand],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var startTime))
        {
            throw new DurableStorageExperimentException(
                "InvalidProcessIdentity",
                $"Linux process identity for pid {processId} did not contain start time.");
        }
        return startTime;
    }

    internal static bool Matches(MonitoredProcessIdentity identity)
    {
        try
        {
            return ReadStartTime(identity.ProcessId) == identity.LinuxStartTimeTicks;
        }
        catch (DurableStorageExperimentException exception)
            when (exception.Code == "ProcessIdentityUnavailable")
        {
            return false;
        }
    }
}

internal static class OwnedProcessTerminator
{
    internal static void KillExact(Process process, MonitoredProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(identity);
        if (process.Id != identity.ProcessId || !LinuxProcessIdentity.Matches(identity))
        {
            throw new DurableStorageExperimentException(
                "OwnedProcessIdentityMismatch",
                "Refused to signal a process whose pid/start-time identity no longer matches.");
        }
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }
}

internal sealed record PinnedDescriptorSnapshot(
    string Target,
    int Flags,
    ApparentFileIdentity Identity,
    DurableStorageNativeObservation? RegularFileMetadata);

internal sealed class PinnedProcessDescriptor : IDisposable
{
    internal PinnedProcessDescriptor(
        SafeFileHandle handle,
        PinnedDescriptorSnapshot snapshot)
    {
        Handle = handle;
        Snapshot = snapshot;
    }

    internal SafeFileHandle Handle { get; }
    internal PinnedDescriptorSnapshot Snapshot { get; }

    public void Dispose() => Handle.Dispose();
}

internal sealed record DescriptorObservationFailure(int Operation, int Error, int Descriptor)
{
    internal int[] Encode(MonitoredProcessRole role) => [(int)role, Operation, Error, Descriptor];
}

internal sealed record DescriptorCoherenceProof(
    MonitoredProcessIdentity Owner, PinnedDescriptorSnapshot First, PinnedDescriptorSnapshot Second)
{
    internal int Changes => (First.Identity != Second.Identity ? 1 : 0)
        | (First.Flags != Second.Flags ? 2 : 0)
        | (!string.Equals(First.Target, Second.Target, StringComparison.Ordinal) ? 4 : 0);

    internal bool Valid => Changes is >= 1 and <= 7 && ValidSnapshot(First) && ValidSnapshot(Second);

    private static bool ValidSnapshot(PinnedDescriptorSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.Target) && Path.IsPathFullyQualified(snapshot.Target)
            && !snapshot.Target.EndsWith(" (deleted)", StringComparison.Ordinal)
            && snapshot.Flags >= 0 && !string.IsNullOrWhiteSpace(snapshot.Identity.Value)
            && snapshot.RegularFileMetadata is { LinkCount: 1, Length: >= 0 } native
            && native.Identity == snapshot.Identity;
}

internal static class LinuxProcessDescriptorObserver
{
    private const int OpenPath = 0x200000;
    private const int OpenCloseOnExec = 0x80000;

    internal static PinnedProcessDescriptor OpenCoherent(
        int processId,
        string descriptorPath,
        int maximumPathUtf8Bytes, MonitoredProcessIdentity? sampledOwner = null)
        => OpenCoherentCore(processId, descriptorPath, maximumPathUtf8Bytes, null, null, sampledOwner);

    internal static PinnedProcessDescriptor OpenCoherentForComponent(
        int processId, string descriptorPath, int maximumPathUtf8Bytes,
        Action<int>? beforeOperation, Func<int, int?>? openError = null,
        MonitoredProcessIdentity? sampledOwner = null)
        => OpenCoherentCore(processId, descriptorPath, maximumPathUtf8Bytes, beforeOperation, openError, sampledOwner);

    private static PinnedProcessDescriptor OpenCoherentCore(
        int processId, string descriptorPath, int maximumPathUtf8Bytes,
        Action<int>? beforeOperation, Func<int, int?>? openError, MonitoredProcessIdentity? sampledOwner)
    {
        SafeFileHandle? pinned = null;
        var knownUnlinked = false;
        try
        {
            if (sampledOwner is not null && (sampledOwner.ProcessId != processId
                || !LinuxProcessIdentity.Matches(sampledOwner)))
                throw PrevalidationProtocol.Error("UnexpectedProcessIdentityLoss",
                    "The sampled descriptor owner no longer matches its exact process identity.");
            beforeOperation?.Invoke(1);
            pinned = OpenPathHandle(descriptorPath, 1, openError?.Invoke(1));
            var first = CaptureSnapshot(
                pinned,
                processId,
                Path.GetFileName(descriptorPath),
                maximumPathUtf8Bytes, 2, beforeOperation, value => knownUnlinked |= value);
            beforeOperation?.Invoke(3);
            using var verification = OpenPathHandle(descriptorPath, 3, openError?.Invoke(3));
            var second = CaptureSnapshot(
                verification,
                processId,
                Path.GetFileName(descriptorPath),
                maximumPathUtf8Bytes, 4, beforeOperation, value => knownUnlinked |= value);
            ValidateCoherent(first, second, sampledOwner,
                int.Parse(Path.GetFileName(descriptorPath), CultureInfo.InvariantCulture));
            return new PinnedProcessDescriptor(pinned, first);
        }
        catch (Exception exception)
        {
            if (exception is DurableStorageExperimentException storage)
                storage.KnownUnlinkedDescriptor = knownUnlinked;
            pinned?.Dispose();
            throw;
        }
    }

    internal static void ValidateCoherent(
        PinnedDescriptorSnapshot first,
        PinnedDescriptorSnapshot second,
        MonitoredProcessIdentity? sampledOwner = null,
        int descriptor = -1)
    {
        if (first.Identity != second.Identity
            || first.Flags != second.Flags
            || !string.Equals(first.Target, second.Target, StringComparison.Ordinal)
            || first.RegularFileMetadata?.LinkCount != second.RegularFileMetadata?.LinkCount)
        {
            var proof = sampledOwner is null ? null : new DescriptorCoherenceProof(sampledOwner, first, second);
            throw new DurableStorageExperimentException(
                "DescriptorIdentityChangedDuringObservation",
                "A process descriptor changed target, flags, native identity, or link count while it was being pinned.")
            {
                CoherenceProof = proof,
                DescriptorFailure = proof is null || proof.Changes == 0 ? null : new(5, proof.Changes, descriptor),
            };
        }
    }

    private static PinnedDescriptorSnapshot CaptureSnapshot(
        SafeFileHandle handle,
        int processId,
        string descriptor,
        int maximumPathUtf8Bytes,
        int flagsOperation,
        Action<int>? beforeOperation,
        Action<bool> observeUnlinked)
    {
        var pinnedPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)}";
        var target = new FileInfo(pinnedPath).LinkTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new DurableStorageExperimentException(
                "DescriptorTargetUnavailable",
                "A pinned process descriptor did not expose its target.");
        }
        if (Encoding.UTF8.GetByteCount(target) > maximumPathUtf8Bytes)
        {
            throw new DurableStorageExperimentException(
                "ObservedPathLimitExceeded",
                "A pinned process descriptor target exceeded the bounded UTF-8 path limit.");
        }
        observeUnlinked(target.EndsWith(" (deleted)", StringComparison.Ordinal));
        beforeOperation?.Invoke(flagsOperation);
        var flags = ReadTargetDescriptorFlags(processId, descriptor, flagsOperation);
        var identity = LinuxAnyHandleIdentityObserver.Observe(handle);
        DurableStorageNativeObservation? metadata = null;
        try
        {
            metadata = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            observeUnlinked(metadata.Value.LinkCount == 0);
        }
        catch (DurableStorageExperimentException exception)
            when (exception.Code == "UnsupportedFileKind")
        {
        }
        return new PinnedDescriptorSnapshot(target, flags, identity, metadata);
    }

    private static SafeFileHandle OpenPathHandle(string path, int operation, int? injectedError)
    {
        var descriptor = injectedError is null ? Open(path, OpenPath | OpenCloseOnExec) : -1;
        var error = injectedError ?? Marshal.GetLastPInvokeError();
        if (descriptor < 0)
        {
            throw new DurableStorageExperimentException(
                "DescriptorObservationUnavailable",
                "Could not pin a process descriptor; open(O_PATH) failed.")
            {
                DescriptorFailure = new(operation, error,
                    int.Parse(Path.GetFileName(path), CultureInfo.InvariantCulture)),
            };
        }
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static int ReadTargetDescriptorFlags(int processId, string descriptor, int operation)
    {
        var path = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/fdinfo/{descriptor}";
        try
        {
            foreach (var line in File.ReadLines(path).Take(32))
            {
                if (line.StartsWith("flags:", StringComparison.Ordinal))
                {
                    return Convert.ToInt32(line["flags:".Length..].Trim(), 8);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or FormatException
            or OverflowException)
        {
            throw new DurableStorageExperimentException(
                $"DescriptorFlags{exception.GetType().Name}",
                "Could not read flags for a pinned process descriptor.")
            {
                DescriptorFailure = new(operation, exception.HResult,
                    int.Parse(descriptor, CultureInfo.InvariantCulture)),
            };
        }
        throw new DurableStorageExperimentException(
            "DescriptorFlagsUnavailable",
            "A pinned process descriptor did not expose bounded flags metadata.");
    }

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int Open(
        string path,
        int flags);
}

internal static class LinuxAnyHandleIdentityObserver
{
    private const int StatxBufferSize = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const uint StatxIno = 0x00000100;

    internal static ApparentFileIdentity Observe(SafeFileHandle handle)
    {
        var buffer = new byte[StatxBufferSize];
        if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath,
                StatxType | StatxIno,
                buffer) != 0)
        {
            throw new DurableStorageExperimentException(
                "DescriptorIdentityUnavailable",
                $"statx failed for a pinned descriptor with errno {Marshal.GetLastPInvokeError()}.");
        }
        var mask = BitConverter.ToUInt32(buffer, 0x00);
        if ((mask & (StatxType | StatxIno)) != (StatxType | StatxIno))
        {
            throw new DurableStorageExperimentException(
                "DescriptorIdentityUnavailable",
                "statx did not return type and inode for a pinned descriptor.");
        }
        var inode = BitConverter.ToUInt64(buffer, 0x20);
        var deviceMajor = BitConverter.ToUInt32(buffer, 0x88);
        var deviceMinor = BitConverter.ToUInt32(buffer, 0x8C);
        return new ApparentFileIdentity(string.Create(
            CultureInfo.InvariantCulture,
            $"linux:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}"));
    }

    [DllImport(
        "libc",
        EntryPoint = "statx",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        [Out] byte[] buffer);
}

internal static class LinuxThreadCpuClock
{
    private const int ClockThreadCpuTimeId = 3;

    internal static long ReadTicks()
    {
        if (ClockGetTime(ClockThreadCpuTimeId, out var time) != 0)
        {
            throw new DurableStorageExperimentException(
                "MonitorCpuClockUnavailable",
                $"clock_gettime(CLOCK_THREAD_CPUTIME_ID) failed with errno {Marshal.GetLastPInvokeError()}.");
        }
        return checked(time.Seconds * TimeSpan.TicksPerSecond + time.Nanoseconds / 100);
    }

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clockId, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Timespec
    {
        internal readonly long Seconds;
        internal readonly long Nanoseconds;
    }
}

internal sealed record RuntimeOnlyClassification(
    bool ProofAttempted,
    bool IsRuntimeOnly,
    string? FailureCode);

internal static class LinuxRuntimeMemoryClassifier
{
    internal const long TmpfsMagic = 0x01021994;
    private const int MaximumMapLines = 32_768;

    internal static RuntimeOnlyClassification Classify(
        MonitoredProcessIdentity process,
        SafeFileHandle descriptor,
        string target,
        DurableStorageNativeObservation descriptorMetadata,
        IReadOnlyList<MonitoredRuntimeOnlyDescriptorProof> proofs)
    {
        var proof = proofs.SingleOrDefault(item =>
            string.Equals(item.DescriptorTarget, target, StringComparison.Ordinal));
        if (proof is null)
        {
            return new RuntimeOnlyClassification(false, false, null);
        }
        if (!LinuxProcessIdentity.Matches(process))
        {
            return Failure("RuntimeProofProcessIdentityLost");
        }
        if (proof.RequireDeletedDescriptor
            && !target.EndsWith(" (deleted)", StringComparison.Ordinal))
        {
            return Failure("RuntimeProofDescriptorNotDeleted");
        }
        if (proof.RequireZeroLinks && descriptorMetadata.LinkCount != 0)
        {
            return Failure("RuntimeProofDescriptorStillLinked");
        }
        if (LinuxFileSystemIdentity.ReadMagic(descriptor) != proof.BackingFileSystemMagic)
        {
            return Failure("RuntimeProofFileSystemMismatch");
        }

        DurableStorageNativeObservation runtimeMetadata;
        try
        {
            using var runtimeHandle = File.OpenHandle(
                proof.RuntimeNativeBinary.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            runtimeMetadata = LinuxStatxHandleMetadataObserver.Instance.Observe(runtimeHandle);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DurableStorageExperimentException)
        {
            return Failure("RuntimeProofNativeBinaryUnavailable");
        }

        IReadOnlyList<LinuxProcessMap> maps;
        try
        {
            maps = LinuxProcessMap.Read(process.ProcessId, MaximumMapLines);
        }
        catch (DurableStorageExperimentException)
        {
            return Failure("RuntimeProofMappingsUnavailable");
        }
        var runtimeMapped = maps.Any(map =>
            map.Executable
            && map.Identity == runtimeMetadata.Identity
            && MonitoredPathRules.PathsEqual(map.Path, proof.RuntimeNativeBinary.Path));
        if (proof.RequireExecutableRuntimeMapping && !runtimeMapped)
        {
            return Failure("RuntimeProofNativeBinaryNotExecutableMapped");
        }
        var backingMapped = maps.Any(map =>
            map.Executable
            && map.Identity == descriptorMetadata.Identity
            && string.Equals(map.Path, target, StringComparison.Ordinal));
        if (proof.RequireExecutableBackingMapping && !backingMapped)
        {
            return Failure("RuntimeProofBackingNotExecutableMapped");
        }
        return new RuntimeOnlyClassification(true, true, null);
    }

    internal static MonitoredRuntimeOnlyDescriptorProof DiscoverCurrentProcessProof()
    {
        using var process = Process.GetCurrentProcess();
        var identity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Harness);
        var runtimeMap = LinuxProcessMap.Read(identity.ProcessId, MaximumMapLines)
            .FirstOrDefault(static map =>
                map.Executable
                && string.Equals(Path.GetFileName(map.Path), "libcoreclr.so", StringComparison.Ordinal)
                && File.Exists(map.Path))
            ?? throw new DurableStorageExperimentException(
                "RuntimeProofNativeBinaryUnavailable",
                "The current managed process did not expose an executable libcoreclr mapping.");
        var proof = new MonitoredRuntimeOnlyDescriptorProof(
            "dotnet-runtime-memory-proof/1",
            "dotnet-doublemapper",
            "/memfd:doublemapper (deleted)",
            "tmpfs",
            TmpfsMagic,
            new MonitoredBinaryIdentity(
                runtimeMap.Path,
                MonitoredFile.HashFile(runtimeMap.Path)),
            RequireDeletedDescriptor: true,
            RequireZeroLinks: true,
            RequireExecutableBackingMapping: true,
            RequireExecutableRuntimeMapping: true,
            "The exact deleted tmpfs memfd is excluded only when the verified managed process maps both this backing identity and the pinned libcoreclr identity executable.");

        foreach (var descriptorPath in Directory.GetFiles(
                     $"/proc/{identity.ProcessId.ToString(CultureInfo.InvariantCulture)}/fd"))
        {
            PinnedProcessDescriptor descriptor;
            try
            {
                descriptor = LinuxProcessDescriptorObserver.OpenCoherent(
                    identity.ProcessId,
                    descriptorPath,
                    maximumPathUtf8Bytes: 4_096);
            }
            catch (DurableStorageExperimentException)
            {
                continue;
            }
            using (descriptor)
            {
                if (!string.Equals(
                        descriptor.Snapshot.Target,
                        proof.DescriptorTarget,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                var classification = Classify(
                    identity,
                    descriptor.Handle,
                    descriptor.Snapshot.Target,
                    descriptor.Snapshot.RegularFileMetadata
                        ?? throw new DurableStorageExperimentException(
                            "RuntimeProofDescriptorNotRegular",
                            "The runtime double-mapper descriptor is not a regular file."),
                    [proof]);
                if (classification.IsRuntimeOnly)
                {
                    return proof;
                }
            }
        }
        throw new DurableStorageExperimentException(
            "RuntimeProofDoubleMapperUnavailable",
            "The current managed process did not expose a positively classifiable /memfd:doublemapper descriptor.");
    }

    private static RuntimeOnlyClassification Failure(string code)
        => new(true, false, code);
}

internal sealed record LinuxProcessMap(
    ApparentFileIdentity Identity,
    bool Executable,
    string Path)
{
    internal static IReadOnlyList<LinuxProcessMap> Read(int processId, int maximumLines)
    {
        var path = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/maps";
        var result = new List<LinuxProcessMap>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (result.Count == maximumLines)
                {
                    throw new DurableStorageExperimentException(
                        "RuntimeProofMappingLimitExceeded",
                        $"Process {processId} exceeded the bounded mapping inventory.");
                }
                var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 6
                    || fields[1].Length < 3
                    || !fields[1].Contains('x', StringComparison.Ordinal)
                    || !TryCreateIdentity(fields[3], fields[4], out var identity))
                {
                    continue;
                }
                var mappedPath = fields[5];
                if (Encoding.UTF8.GetByteCount(mappedPath) > 4_096)
                {
                    throw new DurableStorageExperimentException(
                        "ObservedPathLimitExceeded",
                        "A process mapping path exceeded 4,096 UTF-8 bytes.");
                }
                result.Add(new LinuxProcessMap(identity, Executable: true, mappedPath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DurableStorageExperimentException(
                "RuntimeProofMappingsUnavailable",
                $"Could not read process mappings for pid {processId}: {exception.GetType().Name}.");
        }
        return result;
    }

    private static bool TryCreateIdentity(
        string device,
        string inodeText,
        out ApparentFileIdentity identity)
    {
        identity = default;
        var separator = device.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !uint.TryParse(device.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var major)
            || !uint.TryParse(device.AsSpan(separator + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minor)
            || !ulong.TryParse(inodeText, NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
        {
            return false;
        }
        identity = new ApparentFileIdentity(string.Create(
            CultureInfo.InvariantCulture,
            $"linux:{major:x8}:{minor:x8}:{inode:x16}"));
        return true;
    }
}

internal static class LinuxFileSystemIdentity
{
    internal static long ReadMagic(SafeFileHandle handle)
    {
        if (FStatFs(handle.DangerousGetHandle().ToInt32(), out var stat) != 0)
        {
            throw new DurableStorageExperimentException(
                "FileSystemIdentityUnavailable",
                $"fstatfs failed with errno {Marshal.GetLastPInvokeError()}.");
        }
        return stat.Type;
    }

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(int fileDescriptor, out LinuxStatFs stat);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatFs
    {
        internal long Type;
        internal long BlockSize;
        internal ulong Blocks;
        internal ulong BlocksFree;
        internal ulong BlocksAvailable;
        internal ulong Files;
        internal ulong FilesFree;
        internal int FileSystemId0;
        internal int FileSystemId1;
        internal long NameLength;
        internal long FragmentSize;
        internal long Flags;
        internal long Spare0;
        internal long Spare1;
        internal long Spare2;
        internal long Spare3;
    }
}

internal sealed record MonitoredSweepSummary(
    [property: JsonPropertyName("s")] int Sequence,
    [property: JsonPropertyName("k")] string ObservationKind,
    [property: JsonPropertyName("st")] long StartedTimestamp,
    [property: JsonPropertyName("en")] long CompletedTimestamp,
    [property: JsonPropertyName("g")] double GapMilliseconds,
    [property: JsonPropertyName("b")] string Boundary,
    [property: JsonPropertyName("a")] bool ActiveStorageStage,
    [property: JsonPropertyName("observedSweepBytes")] long ObservedSweepBytes,
    [property: JsonPropertyName("maximumObservedSweepBytes")] long MaximumObservedSweepBytes,
    [property: JsonPropertyName("p")] long PackageBytes,
    [property: JsonPropertyName("r")] long RecoveryBytes,
    [property: JsonPropertyName("h")] long HistoryBytes,
    [property: JsonPropertyName("w")] long WorkspaceBytes,
    [property: JsonPropertyName("e")] long EvidenceBytes,
    [property: JsonPropertyName("u")] long OpenUnlinkedBytes,
    [property: JsonPropertyName("rb")] long RuntimeOnlyExcludedBytes,
    [property: JsonPropertyName("i")] int IdentityCount,
    [property: JsonPropertyName("mi")] int MaximumIdentityCount,
    [property: JsonPropertyName("di")] int DescriptorOnlyIdentityCount,
    [property: JsonPropertyName("mdi")] int MaximumDescriptorOnlyIdentityCount,
    [property: JsonPropertyName("rc")] int RuntimeOnlyExcludedCount,
    [property: JsonPropertyName("uc")] int UnclassifiedCount,
    [property: JsonPropertyName("er")] int ErrorCount,
    [property: JsonPropertyName("c")] bool Complete,
    [property: JsonPropertyName("al")] string? Alarm,
    [property: JsonPropertyName("na")] bool NonAtomic,
    [property: JsonPropertyName("dc")] long DiagnosticCpuTicks,
    [property: JsonPropertyName("dr")] long DiagnosticPeakRssBytes,
    [property: JsonPropertyName("tc")] long TargetCpuTicks,
    [property: JsonPropertyName("tr")] long TargetPeakRssBytes,
    [property: JsonPropertyName("hc")] long HarnessCpuTicks,
    [property: JsonPropertyName("hr")] long HarnessPeakRssBytes,
    [property: JsonPropertyName("mc")] long MonitorCpuTicks,
    [property: JsonPropertyName("ma")] long MonitorAllocatedBytes)
{
    [JsonPropertyName("ci"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentContextIdentities { get; init; }
    [JsonPropertyName("cr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentContextRootedIdentities { get; init; }
    [JsonPropertyName("rh"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RetainedHistoryBytes { get; init; }
    [JsonPropertyName("ec"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstErrorCode { get; init; }
    [JsonPropertyName("nc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? FirstDescriptorFailure { get; init; }
    [JsonPropertyName("sl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossMeasurement? SampledLoss { get; init; }
}

internal static class MonitoredSweepSummaryEncoding
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new SampledSweepConverter() } };

    internal const string Schema = "durable-monitored-sweep-summary/1";
    internal const int MaximumBoundaryUtf8Bytes = 64;
    internal const int MaximumAlarmUtf8Bytes = 64;
    internal const string FieldMap =
        "s=Sequence\n"
        + "k=ObservationKind\n"
        + "st=StartedTimestamp\n"
        + "en=CompletedTimestamp\n"
        + "g=GapMilliseconds(all-observation gap)\n"
        + "b=Boundary\n"
        + "a=ActiveStorageStage\n"
        + "observedSweepBytes=ObservedSweepBytes(non-atomic observation)\n"
        + "maximumObservedSweepBytes=MaximumObservedSweepBytes(non-atomic maximum)\n"
        + "p=PackageBytes\n"
        + "r=RecoveryBytes\n"
        + "h=HistoryBytes\n"
        + "w=WorkspaceBytes\n"
        + "e=EvidenceBytes\n"
        + "u=OpenUnlinkedBytes\n"
        + "rb=RuntimeOnlyExcludedBytes\n"
        + "i=IdentityCount\n"
        + "mi=MaximumIdentityCount\n"
        + "di=DescriptorOnlyIdentityCount\n"
        + "mdi=MaximumDescriptorOnlyIdentityCount\n"
        + "rc=RuntimeOnlyExcludedCount\n"
        + "uc=UnclassifiedCount\n"
        + "er=ErrorCount\n"
        + "c=Complete\n"
        + "al=Alarm\n"
        + "na=NonAtomic\n"
        + "dc=DiagnosticCpuTicks\n"
        + "dr=DiagnosticPeakRssBytes\n"
        + "tc=TargetCpuTicks\n"
        + "tr=TargetPeakRssBytes\n"
        + "hc=HarnessCpuTicks\n"
        + "hr=HarnessPeakRssBytes\n"
        + "mc=MonitorCpuTicks\n"
        + "ma=MonitorAllocatedBytes\n";

    internal static string FieldMapSha256 { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(FieldMap))).ToLowerInvariant();

    internal static bool IsBoundedToken(string value, int maximumBytes)
        => !string.IsNullOrEmpty(value)
            && value.Length <= maximumBytes
            && value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    internal static MonitoredSweepSummary CreateWorstCaseFixture()
        => new(
            int.MaxValue,
            "boundary",
            long.MaxValue,
            long.MaxValue,
            double.MaxValue,
            new string('b', MaximumBoundaryUtf8Bytes),
            ActiveStorageStage: false,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            Complete: false,
            new string('a', MaximumAlarmUtf8Bytes),
            NonAtomic: false,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue,
            long.MaxValue);

    internal static byte[] EncodeLine(MonitoredSweepSummary summary)
    {
        ValidateDescriptorFailure(summary);
        PrevalidationProtocol.Require(summary.FirstErrorCode is null
            || IsBoundedToken(summary.FirstErrorCode, MaximumAlarmUtf8Bytes),
            "PrevalidationErrorCodeEncodingLimit");
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            summary,
            JsonOptions);
        var framed = new byte[checked(payload.Length + 1)];
        payload.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        return framed;
    }

    internal static void ValidateDescriptorFailure(MonitoredSweepSummary summary)
    {
        var context = summary.FirstDescriptorFailure;
        PrevalidationProtocol.Require(context is null
            || context.Length == 4 && context[0] is >= 0 and <= 2
                && context[3] >= 0
                && (context[1] is 1 or 3 && context[2] is > 0 and <= 4095
                    || context[1] is 2 or 4
                    || summary.SampledLoss is not null && context[1] == 5 && context[2] is >= 1 and <= 7)
                && !summary.Complete && summary.ErrorCount > 0 && summary.FirstErrorCode is not null,
            "PrevalidationDescriptorContextMismatch");
    }
}

internal sealed record MonitoredSweepResult(
    MonitoredSweepSummary Summary,
    IReadOnlyList<string> Errors,
    IReadOnlyList<MonitoredObservedIdentityEvidence> IdentityEvidence);

internal sealed record MonitoredObservedIdentityEvidence(
    string Identity,
    long Length,
    string Role,
    bool Charged,
    bool IsUnlinked,
    bool SeenInRoot,
    bool SeenInDescriptor);

internal sealed class BoundedOutputBudget
{
    private readonly long _maximumBytes;
    private readonly int _maximumSummaryRecords;
    private readonly int _maximumBoundarySummaryRecords;
    private readonly int _maximumControlRecords;
    private long _usedBytes;
    private long _summaryBytes;
    private long _controlBytes;
    private int _summaryRecords;
    private int _boundarySummaryRecords;
    private int _controlRecords;

    internal BoundedOutputBudget(
        long maximumBytes,
        int maximumSummaryRecords = int.MaxValue,
        int maximumBoundarySummaryRecords = int.MaxValue,
        int maximumControlRecords = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSummaryRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBoundarySummaryRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumControlRecords);
        _maximumBytes = maximumBytes;
        _maximumSummaryRecords = maximumSummaryRecords;
        _maximumBoundarySummaryRecords = maximumBoundarySummaryRecords;
        _maximumControlRecords = maximumControlRecords;
    }

    internal long UsedBytes => Interlocked.Read(ref _usedBytes);
    internal long SummaryBytes => Interlocked.Read(ref _summaryBytes);
    internal long ControlBytes => Interlocked.Read(ref _controlBytes);
    internal int SummaryRecords => Volatile.Read(ref _summaryRecords);
    internal int BoundarySummaryRecords => Volatile.Read(ref _boundarySummaryRecords);
    internal int ControlRecords => Volatile.Read(ref _controlRecords);

    internal void Consume(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            var current = Interlocked.Read(ref _usedBytes);
            if (bytes > _maximumBytes - current)
            {
                throw new DurableStorageExperimentException(
                    "CombinedOutputLimit",
                    $"Combined monitor-summary and worker-stdout evidence exceeded the {_maximumBytes}-byte limit.");
            }
            if (Interlocked.CompareExchange(ref _usedBytes, current + bytes, current) == current)
            {
                return;
            }
        }
    }

    internal void ConsumeSummary(long bytes, bool boundary)
    {
        var records = Interlocked.Increment(ref _summaryRecords);
        if (records > _maximumSummaryRecords)
        {
            throw new DurableStorageExperimentException(
                "MonitorSummaryCountLimit",
                $"Monitor summaries exceeded the {_maximumSummaryRecords}-record execution limit.");
        }
        if (boundary)
        {
            var boundaryRecords = Interlocked.Increment(ref _boundarySummaryRecords);
            if (boundaryRecords > _maximumBoundarySummaryRecords)
            {
                throw new DurableStorageExperimentException(
                    "MonitorBoundarySummaryCountLimit",
                    $"Boundary summaries exceeded the {_maximumBoundarySummaryRecords}-record execution limit.");
            }
        }
        Consume(bytes);
        Interlocked.Add(ref _summaryBytes, bytes);
    }

    internal void ConsumeControl(long bytes)
    {
        var records = Interlocked.Increment(ref _controlRecords);
        if (records > _maximumControlRecords)
        {
            throw new DurableStorageExperimentException(
                "WorkerControlRecordLimit",
                $"Worker control records exceeded the {_maximumControlRecords}-record execution limit.");
        }
        Consume(bytes);
        Interlocked.Add(ref _controlBytes, bytes);
    }
}

internal interface IMonitoredSummaryWriter : IAsyncDisposable
{
    int Records { get; }
    long Bytes { get; }
    int MaximumObservedRecordBytes { get; }
    ValueTask WriteAsync<T>(T value, CancellationToken cancellationToken);
}

internal sealed class BoundedJsonLineWriter : IMonitoredSummaryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { Converters = { new SampledSweepConverter() } };
    private readonly FileStream _stream;
    private readonly int _maximumRecordBytes;
    private readonly int _maximumRecords;
    private readonly long _maximumBytes;
    private readonly BoundedOutputBudget _combinedBudget;
    private int _records;
    private long _bytes;

    internal BoundedJsonLineWriter(
        string path,
        int maximumRecordBytes,
        int maximumRecords,
        long maximumBytes,
        BoundedOutputBudget? combinedBudget = null)
    {
        _maximumRecordBytes = maximumRecordBytes;
        _maximumRecords = maximumRecords;
        _maximumBytes = maximumBytes;
        _combinedBudget = combinedBudget
            ?? new BoundedOutputBudget(maximumBytes, maximumRecords);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4_096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
    }

    public int Records => _records;
    public long Bytes => _bytes;
    public int MaximumObservedRecordBytes { get; private set; }

    public async ValueTask WriteAsync<T>(T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var encodedLength = checked(bytes.Length + 1);
        if (encodedLength > _maximumRecordBytes)
        {
            throw Error(
                "MonitorSummaryRecordLimit",
                $"A newline-framed monitor summary exceeded the {_maximumRecordBytes}-byte UTF-8 limit.");
        }
        if (_records >= _maximumRecords)
        {
            throw Error(
                "MonitorSummaryCountLimit",
                $"Monitor summaries exceeded the {_maximumRecords}-record limit.");
        }
        if (encodedLength > _maximumBytes - _bytes)
        {
            throw Error(
                "MonitorOutputLimit",
                $"Monitor summaries exceeded the {_maximumBytes}-byte output limit.");
        }
        _combinedBudget.ConsumeSummary(
            encodedLength,
            value is MonitoredSweepSummary { ObservationKind: "boundary" });
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
        _records++;
        _bytes += encodedLength;
        MaximumObservedRecordBytes = Math.Max(MaximumObservedRecordBytes, encodedLength);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.FlushAsync().ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}

internal sealed class MonitoredStorageMonitor : IAsyncDisposable
{
    private const long PackageThresholdBytes = 268_435_456;
    private const long HistoryThresholdBytes = 2_147_483_648;
    private const long WorkspaceThresholdBytes = 3_221_225_472;
    private const long DiagnosticRssThresholdBytes = 536_870_912;
    private const long HarnessRssThresholdBytes = 268_435_456;
    private const long TargetRssThresholdBytes = 805_306_368;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumActiveGap = TimeSpan.FromMilliseconds(1_000);

    private readonly object _gate = new();
    private readonly MonitoredAttributionMap _attribution;
    private readonly IMonitoredSummaryWriter _writer;
    private readonly int _maximumEstablishedIdentities;
    private readonly Dictionary<int, TrackedProcess> _processes = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly TaskCompletionSource<string> _terminalIssue =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private long _previousCompletedTimestamp;
    private long _previousPeriodicCompletedTimestamp;
    private long _maximumObservedSweepBytes;
    private long _diagnosticPeakRss;
    private long _targetPeakRss;
    private long _harnessPeakRss;
    private int _maximumIdentityCount;
    private int _maximumDescriptorOnlyIdentityCount;
    private double _maximumObservedGapMilliseconds;
    private double _maximumPeriodicGapMilliseconds;
    private int _periodicSummaryRecords;
    private int _sequence;
    private long _monitorCpuTicks;
    private long _monitorAllocatedBytes;
    private string _boundary = "poll";
    private bool _activeStorageStage;
    private bool _incomplete;
    private string? _terminalAlarm;
    private readonly bool _includeIdentityEvidence;
    private readonly PrevalidationObservationScope? _prevalidationScope;
    private int _maximumCurrentContextIdentities;
    private MonitoredProcessIdentity? _geometryFixtureOwner;
    private readonly long[] _retiredCpuTicks = new long[3];
    private readonly bool _sampledLoss;
    private SampledLossMeasurement? _lossTotals;
    internal SampledLossMeasurement? LossTotals => _lossTotals;

    internal MonitoredStorageMonitor(
        MonitoredAttributionMap attribution,
        MonitoredEvidenceEncoding encoding,
        string evidencePath,
        BoundedOutputBudget? combinedOutputBudget = null,
        int? maximumEstablishedIdentities = null,
        bool includeIdentityEvidence = false,
        PrevalidationObservationScope? prevalidationScope = null,
        bool sampledLoss = false)
    {
        LinuxStatxHandleMetadataObserver.EnsureSupportedPlatform(OperatingSystem.IsLinux());
        _attribution = attribution;
        _maximumEstablishedIdentities = ValidateEstablishedIdentities(
            attribution,
            maximumEstablishedIdentities);
        _includeIdentityEvidence = includeIdentityEvidence;
        _prevalidationScope = prevalidationScope;
        _sampledLoss = sampledLoss;
        _lossTotals = sampledLoss ? SampledLossMeasurement.Empty() : null;
        _writer = new BoundedJsonLineWriter(
            evidencePath,
            encoding.MaximumSummaryUtf8Bytes,
            encoding.MaximumSummaryRecordsPerExecution,
            checked((long)encoding.MaximumSummaryUtf8Bytes
                * encoding.MaximumSummaryRecordsPerExecution),
            combinedOutputBudget);
    }

    internal MonitoredStorageMonitor(
        MonitoredAttributionMap attribution,
        IMonitoredSummaryWriter writer,
        int? maximumEstablishedIdentities = null,
        bool includeIdentityEvidence = false,
        bool sampledLoss = false)
    {
        LinuxStatxHandleMetadataObserver.EnsureSupportedPlatform(OperatingSystem.IsLinux());
        _attribution = attribution;
        _maximumEstablishedIdentities = ValidateEstablishedIdentities(
            attribution,
            maximumEstablishedIdentities);
        _includeIdentityEvidence = includeIdentityEvidence;
        _sampledLoss = sampledLoss;
        _lossTotals = sampledLoss ? SampledLossMeasurement.Empty() : null;
        _writer = writer;
    }

    internal bool IsIncomplete
    {
        get
        {
            lock (_gate)
            {
                return _incomplete;
            }
        }
    }

    internal string? TerminalAlarm
    {
        get
        {
            lock (_gate)
            {
                return _terminalAlarm;
            }
        }
    }

    internal int MaximumIdentityCount => _maximumIdentityCount;
    internal int MaximumCurrentContextIdentities => _prevalidationScope is null
        ? _maximumIdentityCount : _maximumCurrentContextIdentities;
    internal int MaximumDescriptorOnlyIdentityCount => _maximumDescriptorOnlyIdentityCount;
    internal long MaximumObservedSweepBytes => _maximumObservedSweepBytes;
    internal double MaximumObservedGapMilliseconds => _maximumObservedGapMilliseconds;
    internal double MaximumPeriodicGapMilliseconds => _maximumPeriodicGapMilliseconds;
    internal int PeriodicSummaryRecords => _periodicSummaryRecords;
    internal int SummaryRecords => _writer.Records;
    internal long SummaryBytes => _writer.Bytes;
    internal int MaximumSummaryRecordBytes => _writer.MaximumObservedRecordBytes;
    internal Task<string> TerminalIssue => _terminalIssue.Task;
    internal Action<string, int>? BeforeDescriptorOperationForComponent { private get; set; }

    internal void RegisterGeometryFixtureOwner(MonitoredProcessIdentity owner)
    {
        PrevalidationProtocol.Require(_prevalidationScope?.GeometryFixtures == true
            && owner.Role == MonitoredProcessRole.Harness && LinuxProcessIdentity.Matches(owner),
            "PrevalidationGeometryOwnerMismatch");
        lock (_gate)
        {
            PrevalidationProtocol.Require(_geometryFixtureOwner is null, "PrevalidationGeometryOwnerReuse");
            _geometryFixtureOwner = owner;
        }
    }

    internal void AddProcess(MonitoredProcessIdentity identity)
    {
        lock (_gate)
        {
            if (_sampledLoss && (_processes.Count >= 4 || !Enum.IsDefined(identity.Role)))
                throw Error("SampledProcessLimit", "Sampled observation permits at most four owned processes.");
            if (_processes.TryGetValue(identity.ProcessId, out var existing)
                && existing.Identity != identity)
            {
                MarkIncomplete("ProcessIdentityAmbiguous");
                throw Error(
                    "ProcessIdentityAmbiguous",
                    $"Pid {identity.ProcessId} was already tracked with a different start time.");
            }
            if (existing is not null)
            {
                MarkIncomplete("DuplicateProcessIdentity");
                throw Error(
                    "DuplicateProcessIdentity",
                    $"Pid {identity.ProcessId} was registered more than once.");
            }
            using var process = Process.GetProcessById(identity.ProcessId);
            process.Refresh();
            _processes[identity.ProcessId] = new TrackedProcess(
                identity,
                process.TotalProcessorTime.Ticks);
        }
    }

    internal void MarkIntentionalTermination(MonitoredProcessIdentity identity)
    {
        lock (_gate)
        {
            var tracked = RequireTracked(identity);
            tracked.IntentionalTermination = true;
        }
    }

    internal async Task PrepareTerminationAsync(MonitoredProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        // Drain the current sweep (and its pinned handles) before releasing a
        // process to exit. The authoritative periodic loop is never restarted.
        await _sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarkIntentionalTermination(identity);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    internal async Task KillOwnedAsync(MonitoredProcessIdentity identity, CancellationToken cancellationToken)
    {
        await _sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarkIntentionalTermination(identity);
            using var process = Process.GetProcessById(identity.ProcessId);
            OwnedProcessTerminator.KillExact(process, identity);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            ConfirmTerminatedAndRemove(identity);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    internal async Task ConfirmTerminatedAsync(MonitoredProcessIdentity identity, CancellationToken cancellationToken)
    {
        await _sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ConfirmTerminatedAndRemove(identity); }
        finally { _sweepGate.Release(); }
    }

    internal void ConfirmTerminatedAndRemove(MonitoredProcessIdentity identity)
    {
        lock (_gate)
        {
            var tracked = RequireTracked(identity);
            if (!tracked.IntentionalTermination || (_prevalidationScope is null
                ? LinuxProcessIdentity.Matches(identity)
                : LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity)))
            {
                MarkIncomplete("IntentionalTerminationNotConfirmed");
                throw Error(
                    "IntentionalTerminationNotConfirmed",
                    "A tracked process cannot be removed before exact-identity death is confirmed.");
            }
            _processes.Remove(identity.ProcessId);
            if (_prevalidationScope is not null)
            {
                _retiredCpuTicks[(int)identity.Role] += tracked.LastCpuTicks;
            }
        }
    }

    internal void MarkOwnedCleanup(IReadOnlyList<MonitoredProcessIdentity> identities)
    {
        lock (_gate)
        {
            foreach (var identity in identities)
            {
                if (_processes.TryGetValue(identity.ProcessId, out var tracked) && tracked.Identity == identity)
                {
                    tracked.IntentionalTermination = true;
                }
            }
        }
    }

    internal void RemoveConfirmedCleanup(IReadOnlyList<MonitoredProcessIdentity> identities)
    {
        lock (_gate)
        {
            foreach (var identity in identities)
            {
                if (_processes.TryGetValue(identity.ProcessId, out var tracked) && tracked.Identity == identity)
                {
                    ConfirmTerminatedAndRemove(identity);
                }
            }
        }
    }

    internal void SetStage(string boundary, bool activeStorageStage)
    {
        if (!MonitoredSweepSummaryEncoding.IsBoundedToken(
                boundary,
                MonitoredSweepSummaryEncoding.MaximumBoundaryUtf8Bytes))
        {
            throw Error("InvalidMonitorBoundary", "Monitor boundary names must contain 1-64 ASCII letters, digits, or -_.: characters.");
        }
        lock (_gate)
        {
            _boundary = boundary;
            _activeStorageStage = activeStorageStage;
        }
    }

    internal Task StartAsync()
    {
        lock (_gate)
        {
            _loop ??= RunLoopAsync(_stop.Token);
            return _loop;
        }
    }

    internal async Task<MonitoredSweepResult> ObserveBoundaryAsync(
        string boundary,
        bool activeStorageStage,
        CancellationToken cancellationToken)
    {
        SetStage(boundary, activeStorageStage);
        return await SweepAndWriteAsync(cancellationToken, observationKind: "boundary").ConfigureAwait(false);
    }

    internal async Task StopAsync()
    {
        _stop.Cancel();
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
        }
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }
    }

    internal async Task<MonitoredSweepResult> SweepAndWriteAsync(
        CancellationToken cancellationToken,
        string observationKind = "boundary")
    {
        await _sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = Sweep(observationKind);
            await _writer.WriteAsync(result.Summary,
                _prevalidationScope is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (DurableStorageExperimentException exception)
        {
            lock (_gate)
            {
                MarkIncomplete(exception.Code);
            }
            throw;
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    internal MonitoredSweepResult Sweep(string observationKind = "boundary")
    {
        var cpuBefore = LinuxThreadCpuClock.ReadTicks();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = SweepCore(observationKind);
        var cpuTicks = Math.Max(0, LinuxThreadCpuClock.ReadTicks() - cpuBefore);
        var allocatedBytes = Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        var totalCpuTicks = Interlocked.Add(ref _monitorCpuTicks, cpuTicks);
        var totalAllocatedBytes = Interlocked.Add(ref _monitorAllocatedBytes, allocatedBytes);
        return result with
        {
            Summary = result.Summary with
            {
                MonitorCpuTicks = totalCpuTicks,
                MonitorAllocatedBytes = totalAllocatedBytes,
            },
        };
    }

    private MonitoredSweepResult SweepCore(string observationKind)
    {
        var started = Stopwatch.GetTimestamp();
        var errors = new List<string>(8);
        var loss = _sampledLoss ? SampledLossMeasurement.Empty() : null;
        int[]? firstDescriptorFailure = null;
        var observations = new Dictionary<ApparentFileIdentity, ObservedIdentity>();
        string boundary;
        bool active;
        TrackedProcess[] processes;
        lock (_gate)
        {
            boundary = _boundary;
            active = _activeStorageStage;
            processes = _processes.Values.ToArray();
        }

        var runtimeOnlyExcludedBytes = 0L;
        var runtimeOnlyExcludedCount = 0;
        foreach (var root in _attribution.Roots.Where(static root => root.Charged))
        {
            ObserveRoot(root, observations, errors);
        }
        foreach (var process in processes)
        {
            ObserveProcess(
                process,
                observations,
                errors,
                ref runtimeOnlyExcludedBytes,
                ref runtimeOnlyExcludedCount,
                ref firstDescriptorFailure,
                ref loss);
        }
        if (_prevalidationScope?.CurrentHistoryRoot is { } retainedRoot)
        {
            try
            {
                if (Directory.EnumerateDirectories(retainedRoot).Take(65).Count() > 64)
                {
                    errors.Add("RetainedHistorySlotLimitExceeded");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add("RetainedHistoryEnumerationIncomplete");
            }
        }

        long packageBytes = 0;
        long recoveryBytes = 0;
        long historyBytes = 0;
        long workspaceBytes = 0;
        long evidenceBytes = 0;
        long openUnlinkedBytes = 0;
        var unclassified = 0;
        foreach (var observation in observations.Values)
        {
            if (!observation.Charged)
            {
                continue;
            }
            switch (observation.Role)
            {
                case "package":
                    packageBytes = checked(packageBytes + observation.Length);
                    break;
                case "recovery":
                    recoveryBytes = checked(recoveryBytes + observation.Length);
                    break;
                case "history":
                    historyBytes = checked(historyBytes + observation.Length);
                    break;
                case "workspace":
                case "completed-context":
                    workspaceBytes = checked(workspaceBytes + observation.Length);
                    break;
                case "evidence":
                case "outputs":
                    evidenceBytes = checked(evidenceBytes + observation.Length);
                    break;
                default:
                    workspaceBytes = checked(workspaceBytes + observation.Length);
                    unclassified++;
                    break;
            }
            if (observation.IsUnlinked)
            {
                openUnlinkedBytes = checked(openUnlinkedBytes + observation.Length);
            }
        }

        var observedBytes = checked(packageBytes + recoveryBytes + historyBytes + workspaceBytes + evidenceBytes);
        var retainedHistoryBytes = observations.Values.Where(static item =>
            item.InRetainedHistory && item.Role != "completed-context").Sum(static item => item.Length);
        _maximumObservedSweepBytes = Math.Max(_maximumObservedSweepBytes, observedBytes);
        _maximumIdentityCount = Math.Max(_maximumIdentityCount, observations.Count);
        var descriptorOnlyIdentityCount = observations.Values.Count(static item =>
            item.SeenInDescriptor && !item.SeenInRoot);
        var current = observations.Values.Where(static item => item.Role != "completed-context").ToArray();
        var currentCount = _prevalidationScope is null ? observations.Count : current.Length;
        var currentRootedCount = current.Count(static item => item.SeenInRoot);
        _maximumCurrentContextIdentities = Math.Max(_maximumCurrentContextIdentities, currentCount);
        _maximumDescriptorOnlyIdentityCount = Math.Max(
            _maximumDescriptorOnlyIdentityCount,
            descriptorOnlyIdentityCount);
        if (observations.Count > _attribution.MaximumTrackedIdentitiesPerSweep)
        {
            errors.Add("TrackedIdentityLimitExceeded");
        }
        else if (currentCount > _maximumEstablishedIdentities)
        {
            errors.Add("EstablishedIdentityGeometryExceeded");
        }
        if (descriptorOnlyIdentityCount > _attribution.MaximumDescriptorOnlyIdentitiesPerSweep)
        {
            errors.Add("DescriptorOnlyIdentityLimitExceeded");
        }
        if (_prevalidationScope is not null && currentRootedCount > MonitoredRunnerGeometry.MaximumRootedIdentities)
        {
            errors.Add("CurrentContextRootedGeometryExceeded");
        }

        var processMetrics = ObserveProcessMetrics(processes, errors);
        var completed = Stopwatch.GetTimestamp();
        var gap = _previousCompletedTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_previousCompletedTimestamp,
                _prevalidationScope is null ? started : completed).TotalMilliseconds;
        _previousCompletedTimestamp = completed;
        _maximumObservedGapMilliseconds = Math.Max(_maximumObservedGapMilliseconds, gap);
        if (string.Equals(observationKind, "periodic", StringComparison.Ordinal))
        {
            var periodicGap = _previousPeriodicCompletedTimestamp == 0
                ? 0
                : Stopwatch.GetElapsedTime(
                    _previousPeriodicCompletedTimestamp,
                    started).TotalMilliseconds;
            _previousPeriodicCompletedTimestamp = completed;
            _maximumPeriodicGapMilliseconds = Math.Max(
                _maximumPeriodicGapMilliseconds,
                periodicGap);
            _periodicSummaryRecords++;
        }
        if ((active || _prevalidationScope is not null) && (gap > MaximumActiveGap.TotalMilliseconds
            || _prevalidationScope is not null
                && Stopwatch.GetElapsedTime(started, completed) > MaximumActiveGap))
        {
            errors.Add("MaximumActiveStageGapExceeded");
        }

        var alarm = DetermineAlarm(
            packageBytes,
            recoveryBytes,
            _prevalidationScope is null ? historyBytes : retainedHistoryBytes,
            workspaceBytes,
            processMetrics);
        if (_prevalidationScope is not null && observedBytes > WorkspaceThresholdBytes)
        {
            alarm = "ObservedSuiteWorkspaceThresholdExceeded";
        }
        if (alarm is not null
            && !MonitoredSweepSummaryEncoding.IsBoundedToken(
                alarm,
                MonitoredSweepSummaryEncoding.MaximumAlarmUtf8Bytes))
        {
            errors.Add("MonitorAlarmEncodingLimitExceeded");
            alarm = "MonitorAlarmEncodingLimitExceeded";
        }
        var hardIncomplete = errors.Count != 0 || unclassified != 0;
        var complete = !hardIncomplete && (loss is null || loss.Lost == 0);
        if (hardIncomplete || alarm is not null)
        {
            lock (_gate)
            {
                if (hardIncomplete)
                {
                    MarkIncomplete(errors.FirstOrDefault() ?? "UnclassifiedChargedResource");
                }
                if (alarm is not null)
                {
                    MarkAlarm(alarm);
                }
            }
        }

        var summary = new MonitoredSweepSummary(
            checked(++_sequence),
            observationKind,
            started,
            completed,
            gap,
            boundary,
            active,
            observedBytes,
            _maximumObservedSweepBytes,
            packageBytes,
            recoveryBytes,
            historyBytes,
            workspaceBytes,
            evidenceBytes,
            openUnlinkedBytes,
            runtimeOnlyExcludedBytes,
            observations.Count,
            _maximumIdentityCount,
            descriptorOnlyIdentityCount,
            _maximumDescriptorOnlyIdentityCount,
            runtimeOnlyExcludedCount,
            unclassified,
            errors.Count,
            complete,
            alarm,
            NonAtomic: true,
            processMetrics.DiagnosticCpuTicks,
            processMetrics.DiagnosticPeakRssBytes,
            processMetrics.TargetCpuTicks,
            processMetrics.TargetPeakRssBytes,
            processMetrics.HarnessCpuTicks,
            processMetrics.HarnessPeakRssBytes,
            Interlocked.Read(ref _monitorCpuTicks),
            Interlocked.Read(ref _monitorAllocatedBytes))
        {
            CurrentContextIdentities = _prevalidationScope is null ? null : currentCount,
            CurrentContextRootedIdentities = _prevalidationScope is null ? null : currentRootedCount,
            RetainedHistoryBytes = _prevalidationScope is null ? null : retainedHistoryBytes,
            FirstErrorCode = (_prevalidationScope is null && !_sampledLoss) || !hardIncomplete ? null
                : PrevalidationFailureCodes.Normalize(errors.FirstOrDefault() ?? "UnclassifiedChargedResource"),
            FirstDescriptorFailure = _prevalidationScope is null && !_sampledLoss ? null : firstDescriptorFailure,
            SampledLoss = loss,
        };
        if (loss is not null)
        {
            SampledLossProtocol.ValidateSummary(summary);
            _lossTotals = SampledLossMeasurement.Merge(_lossTotals!, loss);
        }
        var identityEvidence = _includeIdentityEvidence
            ? observations.Select(static pair => new MonitoredObservedIdentityEvidence(
                    pair.Key.Value,
                    pair.Value.Length,
                    pair.Value.Role,
                    pair.Value.Charged,
                    pair.Value.IsUnlinked,
                    pair.Value.SeenInRoot,
                    pair.Value.SeenInDescriptor))
                .OrderBy(static item => item.Identity, StringComparer.Ordinal)
                .ToArray()
            : [];
        return new MonitoredSweepResult(summary, errors, identityEvidence);
    }

    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (failure is null)
            {
                failure = exception;
            }
            else
            {
                failure.Data["MonitoredStorageMonitor.WriterDisposeFailure"] = exception;
            }
        }
        finally
        {
            _stop.Dispose();
            _sweepGate.Dispose();
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunPeriodicLoopAsync(token => SweepAndWriteAsync(token, observationKind: "periodic"),
                Task.Delay, cancellationToken).ConfigureAwait(false);
        }
        catch (DurableStorageExperimentException exception)
        {
            lock (_gate)
            {
                MarkIncomplete(exception.Code);
            }
        }
    }

    internal static async Task RunPeriodicLoopAsync(Func<CancellationToken, Task> observe,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await observe(cancellationToken).ConfigureAwait(false);
            await delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ObserveRoot(
        MonitoredAttributionRoot root,
        Dictionary<ApparentFileIdentity, ObservedIdentity> observations,
        List<string> errors)
    {
        var resolvedRoot = Path.GetFullPath(root.Path);
        if (!Directory.Exists(resolvedRoot))
        {
            errors.Add("DeclaredRootMissing");
            return;
        }
        try
        {
            MonitoredPathRules.RejectLinks(
                Path.GetPathRoot(resolvedRoot)!,
                resolvedRoot);
            foreach (var path in MonitoredPathRules.EnumerateFilesRejectingLinks(
                         resolvedRoot,
                         _attribution.MaximumTrackedIdentitiesPerSweep,
                         _attribution.MaximumObservedPathUtf8Bytes))
            {
                if (observations.Count >= _attribution.MaximumTrackedIdentitiesPerSweep)
                {
                    errors.Add("TrackedIdentityLimitExceeded");
                    return;
                }
                ObservePath(path, root.Role, root.Charged, isUnlinked: false, observations, errors);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DurableStorageExperimentException)
        {
            errors.Add(exception is DurableStorageExperimentException storage
                ? storage.Code
                : $"RootEnumeration{exception.GetType().Name}");
        }
    }

    private void ObserveProcess(
        TrackedProcess tracked,
        Dictionary<ApparentFileIdentity, ObservedIdentity> observations,
        List<string> errors,
        ref long runtimeOnlyExcludedBytes,
        ref int runtimeOnlyExcludedCount,
        ref int[]? firstDescriptorFailure,
        ref SampledLossMeasurement? loss)
    {
        if (!LinuxProcessIdentity.Matches(tracked.Identity))
        {
            if (!tracked.IntentionalTermination)
            {
                errors.Add("UnexpectedProcessIdentityLoss");
            }
            return;
        }

        var fdRoot = $"/proc/{tracked.Identity.ProcessId.ToString(CultureInfo.InvariantCulture)}/fd";
        string[] descriptors;
        try
        {
            descriptors = _sampledLoss
                ? Directory.EnumerateFiles(fdRoot).Take(_attribution.MaximumTrackedIdentitiesPerSweep + 1).ToArray()
                : Directory.GetFiles(fdRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"DescriptorEnumeration{exception.GetType().Name}");
            return;
        }
        if (descriptors.Length > _attribution.MaximumTrackedIdentitiesPerSweep)
        {
            errors.Add("DescriptorIdentityLimitExceeded");
            return;
        }
        var offset = (int)tracked.Identity.Role * 5;
        if (loss is not null) loss.Counts[offset] = checked(loss.Counts[offset] + descriptors.Length);

        foreach (var descriptor in descriptors)
        {
            if (observations.Count >= _attribution.MaximumTrackedIdentitiesPerSweep)
            {
                errors.Add("TrackedIdentityLimitExceeded");
                return;
            }
            try
            {
                var errorsBefore = errors.Count;
                using var pinned = BeforeDescriptorOperationForComponent is { } beforeOperation
                    ? LinuxProcessDescriptorObserver.OpenCoherentForComponent(
                        tracked.Identity.ProcessId, descriptor, _attribution.MaximumObservedPathUtf8Bytes,
                        phase => beforeOperation(descriptor, phase), sampledOwner: _sampledLoss ? tracked.Identity : null)
                    : LinuxProcessDescriptorObserver.OpenCoherent(
                        tracked.Identity.ProcessId, descriptor, _attribution.MaximumObservedPathUtf8Bytes,
                        _sampledLoss ? tracked.Identity : null);
                var target = pinned.Snapshot.Target;
                var flags = pinned.Snapshot.Flags;
                if (IsSimpleRuntimeOnlyTarget(target))
                {
                    if (loss is not null) loss.Counts[offset + 1]++;
                    continue;
                }
                if (pinned.Snapshot.RegularFileMetadata is not { } native)
                {
                    errors.Add("UnsupportedFileKind");
                    continue;
                }
                var writable = (flags & 3) != 0;
                var deleted = target.EndsWith(" (deleted)", StringComparison.Ordinal);
                var normalizedTarget = deleted ? target[..^" (deleted)".Length] : target;
                var role = ClassifyPath(normalizedTarget);
                var descriptorFixture = false;
                if (_prevalidationScope?.DescriptorFixtures?.TryGetValue(native.Identity, out var fixture) == true
                    && fixture.Owner == tracked.Identity && fixture.Target == target
                    && fixture.Length == native.Length && native.LinkCount == 0 && deleted)
                {
                    role = "workspace";
                    descriptorFixture = true;
                }
                // The coordinator can classify creation itself without waiting for the child's
                // evidence file. This is the fixed geometry owner/name/size tuple, not a native
                // temp exemption; the child's proof also preserves the pinned inode identities.
                if (_geometryFixtureOwner == tracked.Identity
                    && PrevalidationGeometry.IsDeclaredDescriptorTarget(target)
                    && native.LinkCount == 0 && native.Length == 512 && deleted)
                {
                    role = "workspace";
                    descriptorFixture = true;
                }
                if (_prevalidationScope is not null && role == "completed-context"
                    && (!observations.TryGetValue(native.Identity, out var prior) || !prior.SeenInRoot))
                {
                    role = null;
                }
                if (_prevalidationScope is not null && role == "completed-context" && writable)
                {
                    errors.Add("WritableCompletedContextDescriptor");
                }
                if (!writable && IsReadOnlyDependency(normalizedTarget)
                    && (_prevalidationScope is null && !_sampledLoss || !deleted && native.LinkCount == 1))
                {
                    if (loss is not null && errors.Count == errorsBefore) loss.Counts[offset + 1]++;
                    continue;
                }
                if (role is null && !writable)
                {
                    errors.Add("UnclassifiedReadOnlyDescriptor");
                }
                if (native.LinkCount > 1)
                {
                    errors.Add("HardLinkRejected");
                    continue;
                }
                if (target.StartsWith("/memfd:", StringComparison.Ordinal))
                {
                    var classification = LinuxRuntimeMemoryClassifier.Classify(
                        tracked.Identity,
                        pinned.Handle,
                        target,
                        native,
                        _attribution.RuntimeOnlyDescriptorProofs);
                    if (classification.IsRuntimeOnly)
                    {
                        runtimeOnlyExcludedBytes = checked(
                            runtimeOnlyExcludedBytes + native.Length);
                        runtimeOnlyExcludedCount++;
                        if (loss is not null && errors.Count == errorsBefore) loss.Counts[offset + 1]++;
                        continue;
                    }
                    if (classification.ProofAttempted)
                    {
                        errors.Add(classification.FailureCode ?? "RuntimeOnlyClassificationFailed");
                    }
                }
                if ((_prevalidationScope is not null || _sampledLoss) && (native.LinkCount == 0 || deleted) && !descriptorFixture)
                {
                    errors.Add("UnclassifiedUnlinkedDescriptor");
                }
                AddObservation(
                    observations,
                    native,
                    role ?? (writable ? "unclassified-writable-fd" : "unclassified-readonly-fd"),
                    charged: true,
                    isUnlinked: native.LinkCount == 0 || deleted,
                    seenInRoot: false,
                    seenInDescriptor: true,
                    inRetainedHistory: IsRetainedHistoryPath(normalizedTarget));
                if (loss is not null && errors.Count == errorsBefore && role is not null)
                    loss.Counts[offset + 1]++;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or DurableStorageExperimentException)
            {
                var lossKind = loss is null ? 0 : SampledLossProtocol.Classify(exception, tracked.Identity);
                if (lossKind == 4 && exception is DurableStorageExperimentException { CoherenceProof: { } proof }
                    && (!IsClassifiableCoherenceSnapshot(proof.First, observations)
                        || !IsClassifiableCoherenceSnapshot(proof.Second, observations)))
                    lossKind = 0;
                if (lossKind != 0 && LinuxProcessIdentity.Matches(tracked.Identity))
                {
                    loss!.Counts[offset + lossKind]++;
                    loss = loss with { FirstLoss = loss.FirstLoss
                        ?? ((DurableStorageExperimentException)exception).DescriptorFailure!.Encode(tracked.Identity.Role) };
                    continue;
                }
                if (exception is DurableStorageExperimentException { DescriptorFailure: { } failure })
                {
                    firstDescriptorFailure ??= failure.Encode(tracked.Identity.Role);
                }
                errors.Add(exception is DurableStorageExperimentException storage
                    ? storage.Code
                    : $"DescriptorObservation{exception.GetType().Name}");
            }
        }
        if (_sampledLoss && !tracked.IntentionalTermination && !LinuxProcessIdentity.Matches(tracked.Identity))
            errors.Add("UnexpectedProcessIdentityLoss");
    }

    private bool IsClassifiableCoherenceSnapshot(PinnedDescriptorSnapshot snapshot,
        Dictionary<ApparentFileIdentity, ObservedIdentity> observations)
    {
        if (_attribution.RuntimeOnlyDescriptorProofs.Any(proof => proof.DescriptorTarget == snapshot.Target)
            || snapshot.Target.StartsWith("/memfd:", StringComparison.Ordinal))
            return false;
        var role = ClassifyPath(snapshot.Target);
        var writable = (snapshot.Flags & 3) != 0;
        if (_prevalidationScope is not null && role == "completed-context")
            return !writable && observations.TryGetValue(snapshot.Identity, out var prior) && prior.SeenInRoot;
        return role is not null || !writable && IsReadOnlyDependency(snapshot.Target);
    }

    private void ObservePath(
        string path,
        string rootRole,
        bool charged,
        bool isUnlinked,
        Dictionary<ApparentFileIdentity, ObservedIdentity> observations,
        List<string> errors)
    {
        if (Encoding.UTF8.GetByteCount(path) > _attribution.MaximumObservedPathUtf8Bytes)
        {
            errors.Add("ObservedPathLimitExceeded");
            return;
        }
        if (new FileInfo(path).LinkTarget is not null)
        {
            errors.Add("SymbolicLinkRejected");
            return;
        }
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var native = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            if (native.LinkCount != 1)
            {
                errors.Add(native.LinkCount == 0 ? "UnexpectedUnlinkedRootFile" : "HardLinkRejected");
                return;
            }
            AddObservation(
                observations,
                native,
                ClassifyPath(path) ?? rootRole,
                charged,
                isUnlinked,
                seenInRoot: true,
                seenInDescriptor: false,
                inRetainedHistory: IsRetainedHistoryPath(path));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DurableStorageExperimentException)
        {
            errors.Add(exception is DurableStorageExperimentException storage
                ? storage.Code
                : $"PathObservation{exception.GetType().Name}");
        }
    }

    private static void AddObservation(
        Dictionary<ApparentFileIdentity, ObservedIdentity> observations,
        DurableStorageNativeObservation native,
        string role,
        bool charged,
        bool isUnlinked,
        bool seenInRoot,
        bool seenInDescriptor,
        bool inRetainedHistory = false)
    {
        if (observations.TryGetValue(native.Identity, out var existing))
        {
            existing.Length = Math.Max(existing.Length, native.Length);
            existing.Charged |= charged;
            existing.IsUnlinked |= isUnlinked;
            existing.Role = PreferRole(existing.Role, role);
            existing.SeenInRoot |= seenInRoot;
            existing.SeenInDescriptor |= seenInDescriptor;
            existing.InRetainedHistory |= inRetainedHistory;
            return;
        }
        observations.Add(
            native.Identity,
            new ObservedIdentity(
                native.Length,
                role,
                charged,
                isUnlinked,
                seenInRoot,
                seenInDescriptor)
            {
                InRetainedHistory = inRetainedHistory,
            });
    }

    private ProcessMetricTotals ObserveProcessMetrics(
        IReadOnlyList<TrackedProcess> tracked,
        List<string> errors)
    {
        long diagnosticCpu = _retiredCpuTicks[(int)MonitoredProcessRole.Diagnostic];
        long targetCpu = _retiredCpuTicks[(int)MonitoredProcessRole.Target];
        long harnessCpu = _retiredCpuTicks[(int)MonitoredProcessRole.Harness];
        long harnessRss = 0;
        foreach (var item in tracked)
        {
            if (!LinuxProcessIdentity.Matches(item.Identity))
            {
                if (_sampledLoss && !item.IntentionalTermination)
                    errors.Add("UnexpectedProcessIdentityLoss");
                continue;
            }
            try
            {
                using var process = Process.GetProcessById(item.Identity.ProcessId);
                process.Refresh();
                item.LastCpuTicks = Math.Max(item.LastCpuTicks, process.TotalProcessorTime.Ticks - item.InitialCpuTicks);
                switch (item.Identity.Role)
                {
                    case MonitoredProcessRole.Diagnostic:
                        diagnosticCpu = checked(
                            diagnosticCpu
                            + item.LastCpuTicks);
                        _diagnosticPeakRss = Math.Max(_diagnosticPeakRss, process.WorkingSet64);
                        break;
                    case MonitoredProcessRole.Target:
                        targetCpu = checked(
                            targetCpu
                            + item.LastCpuTicks);
                        _targetPeakRss = Math.Max(_targetPeakRss, process.WorkingSet64);
                        break;
                    case MonitoredProcessRole.Harness:
                        harnessCpu = checked(
                            harnessCpu
                            + item.LastCpuTicks);
                        harnessRss = checked(harnessRss + process.WorkingSet64);
                        _harnessPeakRss = Math.Max(_harnessPeakRss, harnessRss);
                        break;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or ArgumentException
                or System.ComponentModel.Win32Exception)
            {
                errors.Add($"ProcessMetric{exception.GetType().Name}");
            }
        }
        return new ProcessMetricTotals(
            diagnosticCpu,
            _diagnosticPeakRss,
            targetCpu,
            _targetPeakRss,
            harnessCpu,
            _harnessPeakRss);
    }

    private static string? DetermineAlarm(
        long packageBytes,
        long recoveryBytes,
        long historyBytes,
        long workspaceBytes,
        ProcessMetricTotals metrics)
    {
        if (packageBytes > PackageThresholdBytes - recoveryBytes)
        {
            return "ObservedPackageRecoveryThresholdExceeded";
        }
        if (historyBytes > HistoryThresholdBytes)
        {
            return "ObservedHistoryThresholdExceeded";
        }
        if (workspaceBytes > WorkspaceThresholdBytes)
        {
            return "ObservedWorkspaceThresholdExceeded";
        }
        if (metrics.DiagnosticPeakRssBytes > DiagnosticRssThresholdBytes)
        {
            return "ObservedDiagnosticRssThresholdExceeded";
        }
        if (metrics.HarnessPeakRssBytes > HarnessRssThresholdBytes)
        {
            return "ObservedHarnessRssThresholdExceeded";
        }
        if (metrics.TargetPeakRssBytes > TargetRssThresholdBytes)
        {
            return "ObservedTargetRssThresholdExceeded";
        }
        return null;
    }

    private bool IsRetainedHistoryPath(string path)
        => _prevalidationScope?.CurrentHistoryRoot is { } root
            && Path.IsPathRooted(path) && MonitoredPathRules.IsContained(root, path);

    private string? ClassifyPath(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return null;
        }
        var full = Path.GetFullPath(path);
        if (_prevalidationScope?.CompletedRoots.Any(root =>
                MonitoredPathRules.IsContained(root, full)) == true)
        {
            return "completed-context";
        }
        var containingRoot = _attribution.Roots
            .Where(root => MonitoredPathRules.IsContained(root.Path, full))
            .OrderByDescending(static root => root.Path.Length)
            .FirstOrDefault();
        if (containingRoot is null)
        {
            return null;
        }
        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => _attribution.PackageDirectoryNames.Contains(segment, StringComparer.Ordinal)))
        {
            return "package";
        }
        if (segments.Any(segment => _attribution.RecoveryDirectoryNames.Contains(segment, StringComparer.Ordinal)))
        {
            return "recovery";
        }
        return containingRoot.Role;
    }

    private bool IsReadOnlyDependency(string path)
        => Path.IsPathRooted(path)
            && _attribution.ReadOnlyDependencyRoots.Any(root =>
                MonitoredPathRules.IsContained(root, path));

    private bool IsSimpleRuntimeOnlyTarget(string target)
        => target.StartsWith("pipe:[", StringComparison.Ordinal)
            || target.StartsWith("socket:[", StringComparison.Ordinal)
            || target.StartsWith("anon_inode:", StringComparison.Ordinal)
            || _attribution.RuntimeOnlyDescriptorTargets.Contains(target, StringComparer.Ordinal);

    private static string PreferRole(string left, string right)
    {
        static int Rank(string value) => value switch
        {
            "package" => 6,
            "recovery" => 5,
            "history" => 4,
            "workspace" => 3,
            "outputs" => 2,
            "evidence" => 1,
            _ => 0,
        };
        return Rank(right) > Rank(left) ? right : left;
    }

    private TrackedProcess RequireTracked(MonitoredProcessIdentity identity)
    {
        if (!_processes.TryGetValue(identity.ProcessId, out var tracked)
            || tracked.Identity != identity)
        {
            throw Error("UnknownProcessIdentity", "The exact process identity is not tracked by the monitor.");
        }
        return tracked;
    }

    private void MarkIncomplete(string code)
    {
        _incomplete = true;
        if (_prevalidationScope is not null) code = PrevalidationFailureCodes.Normalize(code);
        var terminal = _terminalAlarm ?? code;
        _terminalAlarm = terminal;
        _terminalIssue.TrySetResult(terminal);
    }

    private void MarkAlarm(string code)
    {
        if (_prevalidationScope is not null) code = PrevalidationFailureCodes.Normalize(code);
        var terminal = _terminalAlarm ?? code;
        _terminalAlarm = terminal;
        _terminalIssue.TrySetResult(terminal);
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);

    private static int ValidateEstablishedIdentities(
        MonitoredAttributionMap attribution,
        int? maximumEstablishedIdentities)
    {
        var maximum = maximumEstablishedIdentities
            ?? attribution.MaximumTrackedIdentitiesPerSweep;
        if (maximum is < 1 || maximum > attribution.MaximumTrackedIdentitiesPerSweep)
        {
            throw Error(
                "InvalidEstablishedIdentityGeometry",
                "The established identity geometry must be within the adopted per-sweep cap.");
        }
        return maximum;
    }

    private sealed class TrackedProcess(
        MonitoredProcessIdentity identity,
        long initialCpuTicks)
    {
        internal MonitoredProcessIdentity Identity { get; } = identity;
        internal long InitialCpuTicks { get; } = initialCpuTicks;
        internal long LastCpuTicks { get; set; }
        internal bool IntentionalTermination { get; set; }
    }

    private sealed class ObservedIdentity(
        long length,
        string role,
        bool charged,
        bool isUnlinked,
        bool seenInRoot,
        bool seenInDescriptor)
    {
        internal long Length { get; set; } = length;
        internal string Role { get; set; } = role;
        internal bool Charged { get; set; } = charged;
        internal bool IsUnlinked { get; set; } = isUnlinked;
        internal bool SeenInRoot { get; set; } = seenInRoot;
        internal bool SeenInDescriptor { get; set; } = seenInDescriptor;
        internal bool InRetainedHistory { get; set; }
    }

    private sealed record ProcessMetricTotals(
        long DiagnosticCpuTicks,
        long DiagnosticPeakRssBytes,
        long TargetCpuTicks,
        long TargetPeakRssBytes,
        long HarnessCpuTicks,
        long HarnessPeakRssBytes);
}
