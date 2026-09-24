using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record PrevalidationDescriptorProofEntry(string Identity, string Target, long Length);
internal sealed record PrevalidationDescriptorProof(string Schema, MonitoredProcessIdentity Owner,
    string Attribution, IReadOnlyList<PrevalidationDescriptorProofEntry> Fixtures);

internal static class PrevalidationGeometry
{
    private static readonly HashSet<string> DescriptorTargets = Enumerable.Range(0, 32)
        .Select(static index => FormattableString.Invariant($"/memfd:dc5-pv-geometry-{index:D2} (deleted)"))
        .ToHashSet(StringComparer.Ordinal);

    internal static bool IsDeclaredDescriptorTarget(string target) => DescriptorTargets.Contains(target);

    internal static FileStream CreateDescriptorFixture(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 31);
        var name = FormattableString.Invariant($"dc5-pv-geometry-{index:D2}");
        var fd = MemfdCreate(System.Text.Encoding.UTF8.GetBytes(name + "\0"), 1);
        PrevalidationProtocol.Require(fd >= 0, "PrevalidationDescriptorFixtureCreate");
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(handle, FileAccess.ReadWrite);
            stream.SetLength(512);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    internal static async Task<PrevalidationCoverage> RunAsync(PrevalidationValidated validated,
        PrevalidationProbe probe, MonitoredExecutionContext context, CancellationToken cancellationToken)
    {
        var root = PrevalidationLayout.ContextRoot(validated.Manifest, probe);
        await using var monitor = new MonitoredExecutionMonitor(context, string.Empty,
            new BoundedOutputBudget(PrevalidationMonitorControl.ReservedSummaryBytes));
        return await RunCoreAsync(root, context.Prevalidation!.CurrentHistoryRoot!, probe,
            monitor.ObserveBoundaryAsync, () => monitor.LossTotals, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<PrevalidationCoverage> RunForComponentAsync(string root, string historyRoot,
        MonitoredStorageMonitor monitor, CancellationToken cancellationToken, Action<int>? fixtureCreated = null)
        => RunCoreAsync(root, historyRoot, PrevalidationProtocol.Plan()[7],
            async (boundary, active, token) =>
            {
                var reply = await PrevalidationMonitorControl.DispatchAsync(
                    new("boundary", Boundary: boundary, Active: active), monitor,
                    Path.Combine(root, "ownership.jsonl"), token).ConfigureAwait(false);
                return new(reply.Summary
                    ?? throw PrevalidationProtocol.Error("PrevalidationBoundaryMissing", "Missing boundary reply."),
                    [], []);
            }, () => monitor.LossTotals, cancellationToken, fixtureCreated);

    private static async Task<PrevalidationCoverage> RunCoreAsync(string root, string historyRoot,
        PrevalidationProbe probe, Func<string, bool, CancellationToken, Task<MonitoredSweepResult>> observe,
        Func<SampledLossMeasurement?> losses, CancellationToken cancellationToken,
        Action<int>? fixtureCreated = null)
    {
        var streams = new List<FileStream>(32);
        using var self = Process.GetCurrentProcess();
        var identity = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        var proofs = new Dictionary<ApparentFileIdentity, PrevalidationDescriptorFixture>();
        try
        {
            var proofPath = Path.Combine(root, "geometry-descriptor-proof.json");
            await using var proofSlot = new FileStream(proofPath,
                FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var resultSlot = new FileStream(Path.Combine(root, "harness-result.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using (File.Open(Path.Combine(root, "coverage.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
            }
            using (File.Open(Path.Combine(root, "cleanup.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
            }
            var before = await observe("geometry-before-padding", true, cancellationToken)
                .ConfigureAwait(false);
            PrevalidationExecutor.RequireSweep(before);
            var current = before.Summary.CurrentContextRootedIdentities
                ?? throw PrevalidationProtocol.Error("PrevalidationContextCountsMissing", "Missing two-level counts.");
            PrevalidationProtocol.Require(current <= 539, "PrevalidationGeometryAlreadyExceeded");
            var padding = Path.Combine(root, "rooted-geometry-fixtures");
            Directory.CreateDirectory(padding);
            MonitoredFile.MakePrivateDirectory(padding);
            for (var index = current; index < 539; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(padding, $"geometry-{index:D3}.bin");
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    output.Write(new byte[512]);
                }
                MonitoredFile.MakeReadOnly(path);
            }
            // Drain any traversal predating the new paths before filling the descriptor-only
            // allowance. A linked file missed by that traversal is correctly descriptor-only.
            var rooted = await observe("geometry-rooted-fixtures-complete", true, cancellationToken)
                .ConfigureAwait(false);
            PrevalidationExecutor.RequireSweep(rooted);
            PrevalidationProtocol.Require(rooted.Summary.CurrentContextRootedIdentities == 539
                && rooted.Summary.DescriptorOnlyIdentityCount == 0, "PrevalidationGeometryRootedPopulation");
            for (var index = 0; index < 32; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stream = CreateDescriptorFixture(index);
                streams.Add(stream);
                var native = LinuxStatxHandleMetadataObserver.Instance.Observe(stream.SafeFileHandle);
                proofs.Add(native.Identity, new(identity,
                    FormattableString.Invariant($"/memfd:dc5-pv-geometry-{index:D2} (deleted)"), 512));
                fixtureCreated?.Invoke(index);
            }
            await JsonSerializer.SerializeAsync(proofSlot,
                new PrevalidationDescriptorProof("durable-prevalidation-descriptor-fixtures/1", identity,
                    "charged-owned-anonymous-inventory-fixtures-not-native-exemptions",
                    proofs.Select(static pair => new PrevalidationDescriptorProofEntry(
                        pair.Key.Value, pair.Value.Target, pair.Value.Length)).ToArray()),
                PrevalidationProtocol.Json, cancellationToken).ConfigureAwait(false);
            proofSlot.Flush(flushToDisk: true);
            await proofSlot.DisposeAsync().ConfigureAwait(false);
            MonitoredFile.MakeReadOnly(proofPath);
            var measured = await observe("geometry-539-rooted-32-descriptor-only",
                true, cancellationToken).ConfigureAwait(false);
            PrevalidationExecutor.RequireSweep(measured);
            PrevalidationProtocol.Require(measured.Summary.CurrentContextRootedIdentities == 539
                && measured.Summary.CurrentContextIdentities == 571
                && measured.Summary.DescriptorOnlyIdentityCount == 32, "PrevalidationGeometryPopulation");
            var widest = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
            {
                CurrentContextIdentities = int.MaxValue, CurrentContextRootedIdentities = int.MaxValue,
                RetainedHistoryBytes = long.MaxValue,
                FirstErrorCode = new string('e', 64),
            };
            if (measured.Summary.SampledLoss is not null) widest = SampledLossProtocol.WorstCaseSummary();
            if (measured.Summary.SampledLoss?.ObservedUnlinked is not null)
                widest = ObservedUnlinkedProtocol.WorstCaseSummary();
            if (measured.Summary.SampledLoss?.RootSampling is not null)
                widest = UnifiedActiveProtocol.WorstCaseSummary();
            PrevalidationProtocol.Require(MonitoredSweepSummaryEncoding.EncodeLine(widest).Length <= 1_024,
                "PrevalidationSummaryWidth");
            var fixtureFiles = CountFixtureFiles(historyRoot);
            PrevalidationProtocol.Require(fixtureFiles == 256, "PrevalidationGeometryHistoryPopulation");
            var coverage = new PrevalidationCoverage(PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id,
                "coverage-observed", null, true, 64, fixtureFiles,
                measured.Summary.CurrentContextIdentities,
                measured.Summary.ObservedSweepBytes, null, null, "synthetic-inventory-and-encoding-only",
                ["geometry-539-rooted-32-descriptor-only"])
            {
                MonitoringComplete = losses()?.HasLoss != true,
                SampledLoss = losses(),
                SampledAdmissible = measured.Summary.SampledLoss is not null,
            };
            await JsonSerializer.SerializeAsync(resultSlot, coverage, PrevalidationProtocol.Json, cancellationToken)
                .ConfigureAwait(false);
            await resultSlot.FlushAsync(cancellationToken).ConfigureAwait(false);
            return coverage;
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static int CountFixtureFiles(string historyRoot)
    {
        var root = MonitoredPathRules.ResolveAbsoluteDirectory(historyRoot, mustExist: true);
        var slots = Directory.EnumerateFileSystemEntries(root).Take(65).ToArray();
        PrevalidationProtocol.Require(slots.Length <= 64, "PrevalidationFixtureSlotLimit");
        var count = 0;
        foreach (var slot in slots)
        {
            // The traversal bound includes directories, not just files. Bound each slot
            // separately so 64 directories do not consume the 256-file population.
            var files = MonitoredPathRules.EnumerateFilesRejectingLinks(slot, 4, 4_096);
            PrevalidationProtocol.Require(files.Count == 4
                && Enumerable.Range(0, 4).All(index => files.Contains(
                    Path.Combine(slot, $"fixture-{index}.bin"), StringComparer.Ordinal))
                && files.All(static path => new FileInfo(path).Length == 512),
                "PrevalidationFixtureFilePopulation");
            foreach (var file in files) PrevalidationProtocol.EnsureImmutable(file);
            count += files.Count;
        }
        return count;
    }

    internal static void ValidateObservation(PrevalidationDescriptorProof proof, MonitoredSweepResult observation,
        MonitoredProcessIdentity owner)
    {
        PrevalidationProtocol.Require(proof.Schema == "durable-prevalidation-descriptor-fixtures/1"
            && proof.Owner == owner && proof.Fixtures.Count == 32
            && proof.Fixtures.Select(static item => item.Identity).Distinct(StringComparer.Ordinal).Count() == 32
            && proof.Fixtures.Select(static item => item.Target).ToHashSet(StringComparer.Ordinal)
                .SetEquals(DescriptorTargets)
            && proof.Fixtures.All(item => item.Length == 512 && observation.IdentityEvidence.Any(observed =>
                observed.Identity == item.Identity && observed.Length == 512 && observed.Charged
                && observed.IsUnlinked && observed.SeenInDescriptor && !observed.SeenInRoot)),
            "PrevalidationGeometryProofMismatch");
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
    private static extern int MemfdCreate(byte[] name, uint flags);
}
