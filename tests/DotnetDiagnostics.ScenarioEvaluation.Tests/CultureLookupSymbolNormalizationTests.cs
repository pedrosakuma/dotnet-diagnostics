using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

/// <summary>
/// Covers #860: the culture-lookup scenario's `globalization-hash-leaf` invariant must attribute
/// samples correctly no matter which equivalent ICU/NLS/managed globalization hashing leaf spelling
/// the runtime happens to emit, or how the runtime splits self-time across those equivalent spellings.
/// </summary>
public sealed class CultureLookupSymbolNormalizationTests
{
    private static readonly ScenarioManifest Manifest =
        ScenarioManifestLoader.LoadAll().Single(manifest => manifest.Id == "culture-lookup");

    private static readonly ScenarioEvidence BaseEvidence =
        ScenarioJson.ReadEvidence(ScenarioManifestLoader.ScenarioPath("Fixtures", "culture-lookup.windows.evidence.json"));

    [Theory]
    [InlineData("System.Globalization.CompareInfo.IcuGetHashCodeOfString(value class System.ReadOnlySpan`1<wchar>,value class System.Globalization.CompareOptions)")]
    [InlineData("System.Globalization.CompareInfo.NlsGetHashCodeOfString(value class System.ReadOnlySpan`1<wchar>,value class System.Globalization.CompareOptions)")]
    [InlineData("System.Globalization.CompareInfo.GetHashCodeOfString(value class System.ReadOnlySpan`1<wchar>,value class System.Globalization.CompareOptions)")]
    public void GlobalizationHashLeafInvariant_PassesForEachObservedSymbolVariantAlone(string symbol)
    {
        var evidence = WithSignalBuckets(
            [
                new ObservedSignalBucket(symbol, 55, "%"),
                new ObservedSignalBucket("System.Threading.Monitor.Wait(class System.Object,int32)", 17, "%"),
            ]);

        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        report.Evidence.Should().Contain(result => result.Id == "globalization-hash-leaf" && result.Passed);
    }

    [Fact]
    public void GlobalizationHashLeafInvariant_AggregatesAcrossSplitEquivalentVariants()
    {
        // Reproduces the #860 flake: the runtime distributes globalization-hash self-time across
        // multiple equivalent leaf spellings (e.g. one for the ICU fast path, one for a slow-path
        // re-entry), each individually below the 20% threshold, but the causal signal is real once
        // the equivalent frames are attributed as one bucket.
        var evidence = WithSignalBuckets(
            [
                new ObservedSignalBucket(
                    "System.Globalization.CompareInfo.IcuGetHashCodeOfString(value class System.ReadOnlySpan`1<wchar>,value class System.Globalization.CompareOptions)",
                    12,
                    "%"),
                new ObservedSignalBucket(
                    "System.Globalization.CompareInfo.NlsGetHashCodeOfString(value class System.ReadOnlySpan`1<wchar>,value class System.Globalization.CompareOptions)",
                    11,
                    "%"),
                new ObservedSignalBucket("System.Threading.Monitor.Wait(class System.Object,int32)", 17, "%"),
                new ObservedSignalBucket("Interop+Kernel32.GetQueuedCompletionStatus(int,unsigned int32&,unsigned int&,int&,int32)", 13.4, "%"),
            ]);

        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        var result = report.Evidence.Single(item => item.Id == "globalization-hash-leaf");
        result.Passed.Should().BeTrue(result.Detail);
        result.Detail.Should().Contain("23");
    }

    [Fact]
    public void GlobalizationHashLeafInvariant_FailsWhenGlobalizationHashingIsAbsent()
    {
        var evidence = WithSignalBuckets(
            [
                new ObservedSignalBucket("System.Threading.Monitor.Wait(class System.Object,int32)", 40, "%"),
                new ObservedSignalBucket("Interop+Kernel32.GetQueuedCompletionStatus(int,unsigned int32&,unsigned int&,int&,int32)", 30, "%"),
            ]);

        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        var result = report.Evidence.Single(item => item.Id == "globalization-hash-leaf");
        result.Passed.Should().BeFalse();
        result.Detail.Should().Contain("no buckets matched");
    }

    [Fact]
    public void GlobalizationHashLeafInvariant_FailsWhenOnlyInclusiveThreadPoolDispatchFramesArePresent()
    {
        // The forbidden "threadpool-runtime-overhead" hypothesis: dispatch/idle-wait frames must
        // never satisfy the invariant that exists specifically to require an exclusive hashing leaf.
        var evidence = WithSignalBuckets(
            [
                new ObservedSignalBucket("System.Threading.PortableThreadPool+IOCompletionPoller.Poll()", 45, "%"),
                new ObservedSignalBucket("System.Threading.WaitHandle.WaitOneNoCheck(int32,bool,class System.Object,value class WaitHandleWaitSourceMap)", 35, "%"),
                new ObservedSignalBucket("System.Threading.LowLevelLifoSemaphore.WaitForSignal()", 20, "%"),
            ]);

        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        var result = report.Evidence.Single(item => item.Id == "globalization-hash-leaf");
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void GlobalizationHashLeafInvariant_FailureDetailShowsUnmatchedFramesAndMagnitudes()
    {
        var evidence = WithSignalBuckets(
            [
                new ObservedSignalBucket("System.Threading.Monitor.Wait(class System.Object,int32)", 44.5, "%"),
                new ObservedSignalBucket("Interop+Kernel32.GetQueuedCompletionStatus(int,unsigned int32&,unsigned int&,int&,int32)", 22.1, "%"),
            ]);

        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        var result = report.Evidence.Single(item => item.Id == "globalization-hash-leaf");
        result.Detail.Should().Contain("Monitor.Wait");
        result.Detail.Should().Contain("44.5");
        result.Detail.Should().Contain("GetQueuedCompletionStatus");
        result.Detail.Should().Contain("22.1");
    }

    private static ScenarioEvidence WithSignalBuckets(IReadOnlyList<ObservedSignalBucket> buckets)
        => BaseEvidence with
        {
            Signals = [new ObservedSignal("cpu.self-time.concentration", 0.5, buckets, "query_snapshot")],
        };
}
