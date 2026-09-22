using System.Collections;
using Microsoft.Win32.SafeHandles;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

public sealed class DurableStorageHandleInventoryTests
{
    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void SameFileOpenedTwiceHasOneIdentityAndOneCharge()
    {
        using var artifacts = new FileArtifacts();
        var path = artifacts.Write("same.db", 11);
        using var first = File.OpenHandle(path);
        using var second = File.OpenHandle(path);

        using var lease = Create(
            [Input(first, DurableStorageFileRole.Source), Input(second, DurableStorageFileRole.Output)],
            capacity: 11,
            maximumHandles: 2,
            maximumFiles: 1);

        lease.Observations.Should().ContainSingle()
            .Which.Should().Match<DurableStorageHandleObservation>(
                item => item.Length == 11 && item.SuppliedHandleCount == 2 &&
                    item.HasSource && item.HasOutput);
        lease.LedgerSnapshot.Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.ChargedBytes == 11 && snapshot.TrackedFiles == 1);
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void IdenticalContentsInDistinctFilesHaveDistinctIdentities()
    {
        using var artifacts = new FileArtifacts();
        using var first = File.OpenHandle(artifacts.Write("first.db", 7));
        using var second = File.OpenHandle(artifacts.Write("second.db", 7));

        using var lease = Create(
            [Input(first), Input(second)],
            capacity: 14,
            maximumHandles: 2,
            maximumFiles: 2);

        lease.Observations.Should().HaveCount(2);
        lease.Observations.Select(item => item.Identity).Should().OnlyHaveUniqueItems();
        lease.LedgerSnapshot.ChargedBytes.Should().Be(14);
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void RenameDoesNotChangeHeldFileIdentity()
    {
        using var artifacts = new FileArtifacts();
        var original = artifacts.Write("before.db", 9);
        var renamed = artifacts.PathFor("after.db");
        using var handle = File.OpenHandle(original);
        using var before = Create([Input(handle)], 9, 1, 1);

        File.Move(original, renamed);
        using var after = Create([Input(handle)], 9, 1, 1);

        after.Observations.Single().Identity.Should().Be(before.Observations.Single().Identity);
        after.Observations.Single().Length.Should().Be(9);
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void AggregateCapacityIncludesSourceAndOutputAtExactBoundary()
    {
        using var artifacts = new FileArtifacts();
        using var source = File.OpenHandle(artifacts.Write("source.db", 8));
        using var output = File.OpenHandle(artifacts.Write("output.db", 12));
        var sourceDescriptor = CaptureDescriptorTarget(source);
        var outputDescriptor = CaptureDescriptorTarget(output);

        using var exact = Create(
            [Input(source, DurableStorageFileRole.Source), Input(output, DurableStorageFileRole.Output)],
            20,
            2,
            2);
        exact.LedgerSnapshot.ChargedBytes.Should().Be(20);

        Action over = () => Create([Input(source), Input(output)], 19, 2, 2);
        over.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("CapacityExceeded");
        RandomAccess.GetLength(source).Should().Be(8);
        RandomAccess.GetLength(output).Should().Be(12);
        exact.Dispose();
        source.Dispose();
        output.Dispose();
        RefersToCapturedFile(sourceDescriptor).Should().BeFalse();
        RefersToCapturedFile(outputDescriptor).Should().BeFalse();
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void HandleAndDistinctFileBoundsHaveExactBoundaries()
    {
        using var artifacts = new FileArtifacts();
        using var first = File.OpenHandle(artifacts.Write("first.db", 1));
        using var firstAlias = File.OpenHandle(artifacts.PathFor("first.db"));
        using var second = File.OpenHandle(artifacts.Write("second.db", 1));
        var descriptors = new[] { first, firstAlias, second }.Select(CaptureDescriptorTarget).ToArray();

        using var exact = Create([Input(first), Input(firstAlias)], 2, 2, 1);
        exact.Observations.Should().ContainSingle();

        Action tooManyHandles = () => Create([Input(first), Input(firstAlias), Input(second)], 3, 2, 2);
        tooManyHandles.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SuppliedHandleLimitExceeded");

        Action tooManyFiles = () => Create([Input(first), Input(second)], 2, 2, 1);
        tooManyFiles.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("TrackedFileLimitExceeded");
        exact.Dispose();
        first.Dispose();
        firstAlias.Dispose();
        second.Dispose();
        descriptors.Should().OnlyContain(descriptor => !RefersToCapturedFile(descriptor));
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void ClosedAndInvalidHandlesAreRejected()
    {
        using var artifacts = new FileArtifacts();
        var closed = File.OpenHandle(artifacts.Write("closed.db", 1));
        closed.Dispose();
        using var invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);

        foreach (var handle in new[] { closed, invalid })
        {
            Action create = () => Create([Input(handle)], 1, 1, 1);
            create.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("InvalidHandle");
        }
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void EnumerationFailureReleasesReferencesWithoutClosingCallerHandle()
    {
        using var artifacts = new FileArtifacts();
        using var enumerationHandle = File.OpenHandle(artifacts.Write("enumeration.db", 2));
        var enumerationDescriptor = CaptureDescriptorTarget(enumerationHandle);

        Action enumerationFailure = () => DurableStorageHandleInventoryLease.Create(
            new ThrowingInputs(Input(enumerationHandle)),
            Limits(10, 2, 2));
        enumerationFailure.Should().Throw<InvalidOperationException>()
            .WithMessage("enumeration failed");
        RandomAccess.GetLength(enumerationHandle).Should().Be(2);
        enumerationHandle.Dispose();
        RefersToCapturedFile(enumerationDescriptor).Should().BeFalse();
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void ObservationFailureReleasesAllReferencesWithoutClosingCallerHandles()
    {
        using var artifacts = new FileArtifacts();
        using var first = File.OpenHandle(artifacts.Write("first.db", 3));
        using var second = File.OpenHandle(artifacts.Write("second.db", 4));
        var firstDescriptor = CaptureDescriptorTarget(first);
        var secondDescriptor = CaptureDescriptorTarget(second);

        Action observationFailure = () => DurableStorageHandleInventoryLease.Create(
            [Input(first), Input(second)],
            Limits(10, 2, 2),
            new FailingObserver(failOnCall: 2));
        observationFailure.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SyntheticObservationFailure");

        RandomAccess.GetLength(first).Should().Be(3);
        RandomAccess.GetLength(second).Should().Be(4);
        first.Dispose();
        second.Dispose();
        RefersToCapturedFile(firstDescriptor).Should().BeFalse();
        RefersToCapturedFile(secondDescriptor).Should().BeFalse();
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void ConcurrentOwnerDisposalAfterReferenceAcquisitionDoesNotBreakObservation()
    {
        using var artifacts = new FileArtifacts();
        var stream = File.OpenRead(artifacts.Write("race.db", 5));
        var handle = stream.SafeFileHandle;
        var observer = new DisposingObserver(stream);

        using var lease = DurableStorageHandleInventoryLease.Create(
            [Input(handle)],
            Limits(5, 1, 1),
            observer);

        lease.Observations.Single().Length.Should().Be(5);
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void LeasePinsDescriptorUntilIdempotentDisposalAndCannotBeReused()
    {
        using var artifacts = new FileArtifacts();
        using var handle = File.OpenHandle(artifacts.Write("pin.db", 6));
        var descriptor = CaptureDescriptorTarget(handle);
        using var lease = Create([Input(handle)], 6, 1, 1);

        handle.Dispose();
        RefersToCapturedFile(descriptor).Should().BeTrue();

        lease.Dispose();
        lease.Dispose();
        RefersToCapturedFile(descriptor).Should().BeFalse();
        Action reuse = () => _ = lease.Observations;
        reuse.Should().Throw<ObjectDisposedException>();
        Action reuseSnapshot = () => _ = lease.LedgerSnapshot;
        reuseSnapshot.Should().Throw<ObjectDisposedException>();
    }

    [LinuxOnlyFact("The release probe compares unique file targets rather than reusable descriptor numbers.")]
    public void ReleaseProbeDistinguishesTheOriginalFileFromADifferentOpenFile()
    {
        using var artifacts = new FileArtifacts();
        using var first = File.OpenHandle(artifacts.Write("original.db", 1));
        using var other = File.OpenHandle(artifacts.Write("other.db", 1));
        var originalDescriptor = CaptureDescriptorTarget(first);
        var otherDescriptor = CaptureDescriptorTarget(other);

        RefersToCapturedFile(originalDescriptor).Should().BeTrue();
        RefersToCapturedFile((otherDescriptor.Path, originalDescriptor.Target)).Should().BeFalse();
    }

    [LinuxOnlyFact("The frozen handle-inventory experiment currently exercises Linux statx.")]
    public void HeldUnlinkedFileRetainsObservedIdentityAndLength()
    {
        using var artifacts = new FileArtifacts();
        var path = artifacts.Write("unlinked.db", 13);
        using var handle = File.OpenHandle(path);
        using var before = Create([Input(handle)], 13, 1, 1);

        File.Delete(path);
        using var after = Create([Input(handle)], 13, 1, 1);

        after.Observations.Single().Should().Match<DurableStorageHandleObservation>(
            item => item.Identity == before.Observations.Single().Identity &&
                item.Length == 13 && item.LinkCount == 0 && item.IsUnlinked);
        after.LedgerSnapshot.ChargedBytes.Should().Be(13);
    }

    [Fact]
    public void UnsupportedPlatformAndMissingNativeFieldsAreTypedFailures()
    {
        Action unsupported = () => LinuxStatxHandleMetadataObserver.EnsureSupportedPlatform(isLinux: false);
        unsupported.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("UnsupportedPlatform");

        var buffer = new byte[LinuxStatxHandleMetadataObserver.StatxBufferSize];
        Action missing = () => LinuxStatxHandleMetadataObserver.Parse(buffer);
        missing.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("RequiredMetadataUnavailable");
    }

    [Fact]
    public void DefaultFactoryChecksPlatformEvenForAnEmptyInventory()
    {
        if (OperatingSystem.IsLinux())
        {
            using var empty = Create([], 0, 1, 1);
            empty.LedgerSnapshot.ChargedBytes.Should().Be(0);
        }
        else
        {
            Action create = () => Create([], 0, 1, 1);
            create.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("UnsupportedPlatform");
        }
    }

    [Fact]
    public void InconsistentLengthsForOneIdentityFailBeforePublishingInventory()
    {
        using var first = new SafeFileHandle(new IntPtr(40), ownsHandle: false);
        using var second = new SafeFileHandle(new IntPtr(41), ownsHandle: false);
        var observer = new SequenceObserver(
            new DurableStorageNativeObservation(new ApparentFileIdentity("same"), 4, 1),
            new DurableStorageNativeObservation(new ApparentFileIdentity("same"), 5, 1));

        Action create = () => DurableStorageHandleInventoryLease.Create(
            [Input(first), Input(second)],
            Limits(10, 2, 1),
            observer);

        create.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InconsistentFileLength");
    }

    private static DurableStorageHandleInventoryLease Create(
        IEnumerable<DurableStorageHandleInput> inputs,
        long capacity,
        int maximumHandles,
        int maximumFiles) =>
        DurableStorageHandleInventoryLease.Create(
            inputs,
            Limits(capacity, maximumHandles, maximumFiles));

    private static DurableStorageHandleInventoryLimits Limits(
        long capacity,
        int maximumHandles,
        int maximumFiles) =>
        new(capacity, maximumHandles, maximumFiles, MaximumOutstandingPermits: 1);

    private static DurableStorageHandleInput Input(
        SafeFileHandle handle,
        DurableStorageFileRole role = DurableStorageFileRole.Source) =>
        new(handle, role);

    private static (string Path, string Target) CaptureDescriptorTarget(SafeFileHandle handle)
    {
        var path = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt32()}";
        return (path, new FileInfo(path).LinkTarget
            ?? throw new InvalidOperationException("The open test descriptor must have a procfs target."));
    }

    // Test files have unique private paths and are not reopened by other test classes.
    // A recycled descriptor pointing elsewhere is not evidence of a leaked reference.
    private static bool RefersToCapturedFile((string Path, string Target) descriptor) =>
        string.Equals(new FileInfo(descriptor.Path).LinkTarget, descriptor.Target, StringComparison.Ordinal);

    private sealed class FileArtifacts : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            $"handle-inventory-artifacts-{Guid.NewGuid():N}");

        internal FileArtifacts() => Directory.CreateDirectory(_directory);

        internal string PathFor(string name) => System.IO.Path.Combine(_directory, name);

        internal string Write(string name, int length)
        {
            var path = PathFor(name);
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, length).ToArray());
            return path;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class ThrowingInputs(DurableStorageHandleInput first) : IEnumerable<DurableStorageHandleInput>
    {
        public IEnumerator<DurableStorageHandleInput> GetEnumerator()
        {
            yield return first;
            throw new InvalidOperationException("enumeration failed");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FailingObserver(int failOnCall) : IDurableStorageHandleMetadataObserver
    {
        private int _calls;

        public DurableStorageNativeObservation Observe(SafeFileHandle handle)
        {
            if (++_calls == failOnCall)
            {
                throw new DurableStorageExperimentException(
                    "SyntheticObservationFailure",
                    "Synthetic observer failure.");
            }

            return LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
        }
    }

    private sealed class DisposingObserver(FileStream owner) : IDurableStorageHandleMetadataObserver
    {
        public DurableStorageNativeObservation Observe(SafeFileHandle handle)
        {
            owner.Dispose();
            return LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
        }
    }

    private sealed class SequenceObserver(params DurableStorageNativeObservation[] observations)
        : IDurableStorageHandleMetadataObserver
    {
        private int _index;

        public DurableStorageNativeObservation Observe(SafeFileHandle handle) => observations[_index++];
    }
}
