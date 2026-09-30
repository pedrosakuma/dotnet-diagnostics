using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

internal enum DurableStorageFileRole
{
    Source,
    Output,
}

internal readonly record struct DurableStorageHandleInput(
    SafeFileHandle Handle,
    DurableStorageFileRole Role);

internal readonly record struct DurableStorageHandleInventoryLimits(
    long Capacity,
    int MaximumSuppliedHandles,
    int MaximumTrackedFiles,
    int MaximumOutstandingPermits);

internal sealed record DurableStorageHandleObservation(
    ApparentFileIdentity Identity,
    long Length,
    uint LinkCount,
    bool IsUnlinked,
    int SuppliedHandleCount,
    bool HasSource,
    bool HasOutput);

internal readonly record struct DurableStorageNativeObservation(
    ApparentFileIdentity Identity,
    long Length,
    uint LinkCount);

internal interface IDurableStorageHandleMetadataObserver
{
    DurableStorageNativeObservation Observe(SafeFileHandle handle);
}

internal sealed class DurableStorageHandleInventoryLease : IDisposable
{
    private readonly IReadOnlyList<DurableStorageHandleObservation> _observations;
    private readonly IReadOnlyList<SafeFileHandle> _retainedHandles;
    private readonly ApparentByteLedgerSnapshot _ledgerSnapshot;
    private int _disposed;

    private DurableStorageHandleInventoryLease(
        IReadOnlyList<DurableStorageHandleObservation> observations,
        IReadOnlyList<SafeFileHandle> retainedHandles,
        ApparentByteLedgerSnapshot ledgerSnapshot)
    {
        _observations = observations;
        _retainedHandles = retainedHandles;
        _ledgerSnapshot = ledgerSnapshot;
    }

    internal IReadOnlyList<DurableStorageHandleObservation> Observations
    {
        get
        {
            EnsureNotDisposed();
            return _observations;
        }
    }

    internal ApparentByteLedgerSnapshot LedgerSnapshot
    {
        get
        {
            EnsureNotDisposed();
            return _ledgerSnapshot;
        }
    }

    internal static DurableStorageHandleInventoryLease Create(
        IEnumerable<DurableStorageHandleInput> inputs,
        DurableStorageHandleInventoryLimits limits)
    {
        LinuxStatxHandleMetadataObserver.EnsureSupportedPlatform(OperatingSystem.IsLinux());
        return Create(inputs, limits, LinuxStatxHandleMetadataObserver.Instance);
    }

    internal static DurableStorageHandleInventoryLease Create(
        IEnumerable<DurableStorageHandleInput> inputs,
        DurableStorageHandleInventoryLimits limits,
        IDurableStorageHandleMetadataObserver observer)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(observer);
        if (limits.MaximumSuppliedHandles <= 0)
        {
            throw Error("InvalidHandleLimit", "The supplied-handle limit must be positive.");
        }

        var ledger = new ApparentByteReservationLedger(
            limits.Capacity,
            limits.MaximumTrackedFiles,
            limits.MaximumOutstandingPermits);
        var retainedHandles = new List<SafeFileHandle>(
            Math.Min(limits.MaximumSuppliedHandles, 16));
        var observations = new Dictionary<ApparentFileIdentity, ObservationBuilder>();
        var suppliedHandleCount = 0;

        try
        {
            foreach (var input in inputs)
            {
                if (suppliedHandleCount == limits.MaximumSuppliedHandles)
                {
                    throw Error(
                        "SuppliedHandleLimitExceeded",
                        "The caller-supplied handle limit was exceeded during enumeration.");
                }

                suppliedHandleCount++;
                // Reserve the tracking slot before acquiring a reference that needs cleanup.
                retainedHandles.EnsureCapacity(retainedHandles.Count + 1);
                AcquireReference(input.Handle);
                retainedHandles.Add(input.Handle);

                var observed = observer.Observe(input.Handle);
                if (observations.TryGetValue(observed.Identity, out var existing))
                {
                    if (existing.Length != observed.Length)
                    {
                        throw Error(
                            "InconsistentFileLength",
                            "Two retained handles for the same file identity reported different lengths.");
                    }

                    if (existing.LinkCount != observed.LinkCount)
                    {
                        throw Error(
                            "InconsistentLinkCount",
                            "Two retained handles for the same file identity reported different link counts.");
                    }

                    existing.Add(input.Role, observed.LinkCount);
                }
                else
                {
                    ledger.Register(observed.Identity, observed.Length);
                    observations.Add(
                        observed.Identity,
                        new ObservationBuilder(observed.Length, observed.LinkCount, input.Role));
                }
            }

            var published = new List<DurableStorageHandleObservation>(observations.Count);
            foreach (var pair in observations)
            {
                published.Add(pair.Value.Publish(pair.Key));
            }

            return new DurableStorageHandleInventoryLease(
                new ReadOnlyCollection<DurableStorageHandleObservation>(published),
                new ReadOnlyCollection<SafeFileHandle>(retainedHandles),
                ledger.GetSnapshot());
        }
        catch
        {
            ReleaseReferences(retainedHandles);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseReferences(_retainedHandles);
    }

    private static void AcquireReference(SafeFileHandle? handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            throw Error("InvalidHandle", "A valid, open caller-owned file handle is required.");
        }

        var addedReference = false;
        try
        {
            handle.DangerousAddRef(ref addedReference);
        }
        catch (ObjectDisposedException)
        {
            throw Error("InvalidHandle", "The caller-owned file handle was already disposed.");
        }

        if (!addedReference)
        {
            throw Error("InvalidHandle", "The caller-owned file handle could not be retained.");
        }
    }

    private static void ReleaseReferences(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].DangerousRelease();
        }
    }

    private void EnsureNotDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(DurableStorageHandleInventoryLease));
        }
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);

    private sealed class ObservationBuilder
    {
        internal ObservationBuilder(long length, uint linkCount, DurableStorageFileRole role)
        {
            Length = length;
            LinkCount = linkCount;
            AddRole(role);
        }

        internal long Length { get; }
        internal uint LinkCount { get; private set; }
        internal int SuppliedHandleCount { get; private set; } = 1;
        internal bool HasSource { get; private set; }
        internal bool HasOutput { get; private set; }

        internal void Add(DurableStorageFileRole role, uint linkCount)
        {
            SuppliedHandleCount++;
            LinkCount = linkCount;
            AddRole(role);
        }

        internal DurableStorageHandleObservation Publish(ApparentFileIdentity identity) =>
            new(identity, Length, LinkCount, LinkCount == 0, SuppliedHandleCount, HasSource, HasOutput);

        private void AddRole(DurableStorageFileRole role)
        {
            if (role == DurableStorageFileRole.Source)
            {
                HasSource = true;
            }
            else if (role == DurableStorageFileRole.Output)
            {
                HasOutput = true;
            }
            else
            {
                throw Error("InvalidFileRole", "The supplied file role is not recognized.");
            }
        }
    }
}

internal sealed class LinuxStatxHandleMetadataObserver : IDurableStorageHandleMetadataObserver
{
    // Linux UAPI include/uapi/linux/stat.h fixes struct statx at 0x100 bytes and
    // labels these offsets. Only the requested original fields are decoded.
    internal const int StatxBufferSize = 0x100;
    internal static LinuxStatxHandleMetadataObserver Instance { get; } = new();

    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const uint StatxNlink = 0x00000004;
    private const uint StatxIno = 0x00000100;
    private const uint StatxSize = 0x00000200;
    private const uint RequiredMask = StatxType | StatxNlink | StatxIno | StatxSize;
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFileType = 0x8000;

    private LinuxStatxHandleMetadataObserver()
    {
    }

    public DurableStorageNativeObservation Observe(SafeFileHandle handle)
    {
        EnsureSupportedPlatform(OperatingSystem.IsLinux());

        var buffer = new byte[StatxBufferSize];
        int result;
        try
        {
            result = NativeMethods.Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath,
                RequiredMask,
                buffer);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new DurableStorageExperimentException(
                "UnsupportedNativeFacility",
                $"The Linux libc statx entry point is unavailable: {exception.Message}");
        }
        catch (DllNotFoundException exception)
        {
            throw new DurableStorageExperimentException(
                "UnsupportedNativeFacility",
                $"The Linux C library is unavailable: {exception.Message}");
        }

        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new DurableStorageExperimentException(
                "NativeMetadataObservationFailed",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Linux statx failed for the retained handle with errno {error}."));
        }

        return Parse(buffer);
    }

    internal static void EnsureSupportedPlatform(bool isLinux)
    {
        if (!isLinux)
        {
            throw new DurableStorageExperimentException(
                "UnsupportedPlatform",
                "Handle metadata inventory is currently implemented only for Linux.");
        }
    }

    internal static DurableStorageNativeObservation Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length != StatxBufferSize)
        {
            throw new DurableStorageExperimentException(
                "InvalidNativeMetadata",
                "The Linux statx result did not have the required 256-byte UAPI layout.");
        }

        var mask = ReadUInt32(buffer[0x00..]);
        if ((mask & RequiredMask) != RequiredMask)
        {
            throw new DurableStorageExperimentException(
                "RequiredMetadataUnavailable",
                "Linux statx did not return file type, link count, inode, and size.");
        }

        var mode = ReadUInt16(buffer[0x1C..]);
        if ((mode & FileTypeMask) != RegularFileType)
        {
            throw new DurableStorageExperimentException(
                "UnsupportedFileKind",
                "Only regular files can be included in the experimental storage inventory.");
        }

        var inode = ReadUInt64(buffer[0x20..]);
        var size = ReadUInt64(buffer[0x28..]);
        if (size > long.MaxValue)
        {
            throw new DurableStorageExperimentException(
                "FileLengthOutOfRange",
                "The observed file length cannot be represented by the accepted ledger.");
        }

        var linkCount = ReadUInt32(buffer[0x10..]);
        var deviceMajor = ReadUInt32(buffer[0x88..]);
        var deviceMinor = ReadUInt32(buffer[0x8C..]);
        var identity = new ApparentFileIdentity(string.Create(
            CultureInfo.InvariantCulture,
            $"linux:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}"));

        return new(identity, (long)size, linkCount);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes) =>
        BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes) =>
        BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt64BigEndian(bytes);

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "statx", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Statx(
            int directoryFileDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            [Out] byte[] buffer);
    }
}
