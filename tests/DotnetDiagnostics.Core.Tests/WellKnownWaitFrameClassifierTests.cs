using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class WellKnownWaitFrameClassifierTests
{
    [Theory]
    [InlineData("System.Threading.LowLevelLifoSemaphore.WaitForSignal(System.Int32,System.Int32)", "ThreadPool worker idle wait")]
    [InlineData("System.Threading.LowLevelLifoSemaphore.WaitNative(System.Int32,System.Int32)", "ThreadPool worker idle wait")]
    [InlineData("System.Threading.WaitHandle.WaitOneNoCheck()", "WaitHandle.Wait")]
    [InlineData("System.Threading.Monitor.Wait(System.Object)", "Monitor.Wait")]
    [InlineData("System.Threading.Tasks.Task.Wait()", "Task.Wait (blocking)")]
    [InlineData("System.Threading.SemaphoreSlim.Wait(System.Threading.CancellationToken)", "Semaphore.Wait")]
    [InlineData("System.Threading.PortableThreadPool+WorkerThread.WaitForWork()", "ThreadPool worker idle wait")]
    public void Classify_KnownWaitFrames_ReturnsReason(string frame, string expectedReason)
    {
        var match = WellKnownWaitFrameClassifier.Classify(frame);

        match.Should().NotBeNull();
        match!.Reason.Should().Be(expectedReason);
    }

    [Fact]
    public void Classify_BusyFrame_ReturnsNull()
    {
        WellKnownWaitFrameClassifier.Classify("MyApp.Worker.BurnCpu(System.Int32)").Should().BeNull();
    }

    [Theory]
    [InlineData("System.Threading.SemaphoreSlim.WaitAsync(System.Threading.CancellationToken)")]
    [InlineData("System.Threading.Tasks.Task.WaitAsync(System.TimeSpan)")]
    [InlineData("System.IO.MemoryStream.Read(System.Byte[],System.Int32,System.Int32)")]
    [InlineData("MyApp.SparkProcessor.Run()")]
    public void Classify_AsyncOrBroadSubstringFrames_ReturnsNull(string frame)
    {
        WellKnownWaitFrameClassifier.Classify(frame).Should().BeNull();
    }
}
