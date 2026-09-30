using System.Collections;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Fact]
    public async Task RootObservationLeafLossRemainsHardUnderRevisionSix()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "root");
        Directory.CreateDirectory(root);
        var leaf = Path.Combine(root, "charged.bin");
        File.WriteAllBytes(leaf, [1, 2, 3]);
        var attribution = Attribution(root, root, root, root) with
        {
            Roots = [new("workspace", root, true)],
        };
        await using var monitor = new MonitoredStorageMonitor(attribution, EncodingContract(),
            Path.Combine(_workspace, "root-proof.jsonl"), observedUnlinked: true);
        monitor.BeforeRootPathObservationForComponent = path =>
        {
            path.Should().Be(leaf);
            File.Delete(path);
        };
        var result = monitor.Sweep();
        result.Errors.Should().ContainSingle().Which.Should().Be("PathObservationFileNotFoundException");
        result.Summary.Complete.Should().BeFalse();
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        result.Summary.SampledLoss!.Lost.Should().Be(0);
        result.Summary.SampledLoss.ObservedUnlinked!.Identities.Should().Be(0);
        Action require = () => PrevalidationExecutor.RequireSweep(result);
        require.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PathObservationFileNotFoundException");
        _output.WriteLine("Enumerated charged leaf deleted before open: hard root error, no descriptor loss exemption.");
    }

    [Fact]
    public void RootObservationAnchoredDirectorySurvivesAncestorRename()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "staging");
        Directory.CreateDirectory(root);
        var leaf = Path.Combine(root, "charged.bin");
        File.WriteAllBytes(leaf, [1, 2, 3]);
        using var anchor = OpenRootAnchor(root);
        using var before = File.OpenHandle(leaf);
        var identity = LinuxStatxHandleMetadataObserver.Instance.Observe(before);
        var enumerated = MonitoredPathRules.EnumerateFilesRejectingLinks(root, 8, 4_096).Single();
        Directory.Move(root, Path.Combine(_workspace, "published"));
        Action stale = () => { using var handle = File.OpenHandle(enumerated); };
        stale.Should().Throw<DirectoryNotFoundException>();
        using var relative = OpenRootRelative(anchor, "charged.bin");
        var observed = LinuxStatxHandleMetadataObserver.Instance.Observe(relative);
        observed.Identity.Should().Be(identity.Identity);
        observed.Length.Should().Be(3);
        observed.LinkCount.Should().Be(1);
        _output.WriteLine($"Ancestor rename: anchored openat preserves {observed.Identity}, length=3, links=1.");
    }

    [Fact]
    public void RootObservationAnchoredDirectoryDoesNotPreventLeafUnlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        var leaf = Path.Combine(_workspace, "charged.bin");
        File.WriteAllBytes(leaf, [1, 2, 3]);
        using var anchor = OpenRootAnchor(_workspace);
        using var pinned = File.OpenHandle(leaf);
        MonitoredPathRules.EnumerateFilesRejectingLinks(_workspace, 8, 4_096).Should().Contain(leaf);
        File.Delete(leaf);
        var fd = RootOpenAt(anchor.DangerousGetHandle().ToInt32(), "charged.bin", RootOpenFlags);
        var error = Marshal.GetLastPInvokeError();
        if (fd >= 0) new SafeFileHandle((IntPtr)fd, ownsHandle: true).Dispose();
        fd.Should().Be(-1);
        error.Should().Be(2);
        var observed = LinuxStatxHandleMetadataObserver.Instance.Observe(pinned);
        observed.LinkCount.Should().Be(0);
        observed.Length.Should().Be(3);
        _output.WriteLine("Leaf unlink: anchored openat=ENOENT; already-pinned file remains length=3, links=0.");
    }

    [Fact]
    public void RootObservationAtomicLeafReplacementDoesNotPreserveEnumeratedIdentity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var leaf = Path.Combine(_workspace, "charged.bin");
        var temporary = Path.Combine(_workspace, "replacement.bin");
        File.WriteAllBytes(leaf, [1, 2, 3]);
        using var anchor = OpenRootAnchor(_workspace);
        using var pinned = File.OpenHandle(leaf);
        var original = LinuxStatxHandleMetadataObserver.Instance.Observe(pinned);
        MonitoredPathRules.EnumerateFilesRejectingLinks(_workspace, 8, 4_096).Should().Contain(leaf);
        File.WriteAllBytes(temporary, [4]);
        File.Move(temporary, leaf, overwrite: true);
        using var replaced = OpenRootRelative(anchor, "charged.bin");
        var observed = LinuxStatxHandleMetadataObserver.Instance.Observe(replaced);
        observed.Identity.Should().NotBe(original.Identity);
        observed.Length.Should().Be(1);
        LinuxStatxHandleMetadataObserver.Instance.Observe(pinned).LinkCount.Should().Be(0);
        _output.WriteLine("Atomic leaf replacement: openat succeeds with a DIFFERENT identity; name is not identity proof.");
    }

    [Fact]
    public async Task RootObservationActualBIndexCommitDeletesAnEnumeratedJournal()
    {
        if (!OperatingSystem.IsLinux()) return;
        var database = Path.Combine(_workspace, "index.db");
        await using var index = DurableAppendFirstQueryIndex.CreateWritable(database, new());
        using var anchor = OpenRootAnchor(_workspace);
        string? enumeratedJournal = null;
        var record = new DurableCounterRecord(
            1, "Synthetic.Provider", "counter-0", "Synthetic counter 0", "items",
            42, CounterKind.Mean, 1, CounterMetadataState.Valid, TimeSpan.TicksPerSecond,
            CounterMetadataState.Valid, 10, "fixture-relative-100ns", "component-test",
            CoverageGapState.Unknown, CounterResetState.Unknown, EncodedBytes: 512);
        index.InsertBatch(new RootObservedBatch(record, () =>
        {
            enumeratedJournal = MonitoredPathRules.EnumerateFilesRejectingLinks(_workspace, 8, 4_096)
                .Single(path => path.EndsWith("-journal", StringComparison.Ordinal));
            using var handle = File.OpenHandle(enumeratedJournal);
            var native = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            native.LinkCount.Should().Be(1);
            native.Length.Should().BeGreaterThan(0);
            _output.WriteLine($"Controlled actual B transaction journal before commit: bytes={native.Length}, links=1.");
        }));
        enumeratedJournal.Should().NotBeNull();
        index.CountRows().Should().Be(1);
        Action stale = () => { using var handle = File.OpenHandle(enumeratedJournal!); };
        stale.Should().Throw<FileNotFoundException>();
        var fd = RootOpenAt(anchor.DangerousGetHandle().ToInt32(), Path.GetFileName(enumeratedJournal!), RootOpenFlags);
        var error = Marshal.GetLastPInvokeError();
        if (fd >= 0) new SafeFileHandle((IntPtr)fd, ownsHandle: true).Dispose();
        fd.Should().Be(-1);
        error.Should().Be(2);
        _output.WriteLine("Actual B InsertBatch committed one record; retained journal name now fails both absolute open and openat. Not historical filename identification.");
    }

    private const int RootOpenFlags = 0x80000 | 0x20000;

    private static SafeFileHandle OpenRootAnchor(string path)
    {
        var fd = RootOpenAt(-100, path, RootOpenFlags | 0x10000);
        fd.Should().BeGreaterThanOrEqualTo(0);
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle OpenRootRelative(SafeFileHandle anchor, string name)
    {
        var fd = RootOpenAt(anchor.DangerousGetHandle().ToInt32(), name, RootOpenFlags);
        fd.Should().BeGreaterThanOrEqualTo(0);
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int RootOpenAt(int directory, string path, int flags);

    private sealed class RootObservedBatch(DurableCounterRecord record, Action afterInsert)
        : IReadOnlyList<DurableCounterRecord>
    {
        public int Count => 1;
        public DurableCounterRecord this[int index] => index == 0 ? record : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<DurableCounterRecord> GetEnumerator()
        {
            yield return record;
            afterInsert();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
