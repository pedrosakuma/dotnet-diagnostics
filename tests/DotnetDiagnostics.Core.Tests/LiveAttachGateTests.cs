using DotnetDiagnostics.Core.Threads;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class LiveAttachGateTests
{
    [Fact]
    public void Acquire_SamePid_BlocksUntilLeaseDisposed()
    {
        var first = LiveAttachGate.Acquire(900001, CancellationToken.None);
        Assert.Throws<TimeoutException>(() => LiveAttachGate.Acquire(900001, CancellationToken.None, TimeSpan.FromMilliseconds(20)));
        first.Dispose();
        first.Dispose();
        using var second = LiveAttachGate.Acquire(900001, CancellationToken.None, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Acquire_DifferentPids_DoNotBlockEachOther()
    {
        using var a = LiveAttachGate.Acquire(900002, CancellationToken.None);
        using var b = LiveAttachGate.Acquire(900003, CancellationToken.None, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Acquire_CancelledWhileWaiting_ThrowsAndLeavesGateUsable()
    {
        var holder = LiveAttachGate.Acquire(900004, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Assert.ThrowsAny<OperationCanceledException>(() => LiveAttachGate.Acquire(900004, cts.Token));
        holder.Dispose();
        using var next = LiveAttachGate.Acquire(900004, CancellationToken.None, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Lease_DisposedOnExceptionPath_ReleasesGate()
    {
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lease = LiveAttachGate.Acquire(900005, CancellationToken.None);
            throw new InvalidOperationException("simulated cancellation after attach");
        }));
        using var next = LiveAttachGate.Acquire(900005, CancellationToken.None, TimeSpan.FromMilliseconds(200));
    }
}
