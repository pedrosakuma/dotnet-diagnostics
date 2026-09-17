using System.Net.Sockets;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcDumpFlushTerminationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TruncatedStream_RequiresObservedEventOrExpiredBudget(
        bool observed,
        bool timedOut,
        bool expected)
    {
        var exception = new FormatException("Read past end of stream.");

        GcDumpHeapSnapshotCollector.IsExpectedFlushTermination(exception, observed, timedOut)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(SocketError.ConnectionAborted, true, false, true)]
    [InlineData(SocketError.OperationAborted, false, true, true)]
    [InlineData(SocketError.ConnectionAborted, false, false, false)]
    [InlineData(SocketError.OperationAborted, false, false, false)]
    [InlineData(SocketError.ConnectionReset, true, true, false)]
    [InlineData(SocketError.AccessDenied, true, true, false)]
    public void SocketFailure_RequiresVerifiedShapeAndShutdownBoundary(
        SocketError error,
        bool observed,
        bool timedOut,
        bool expected)
    {
        var exception = new IOException("stream failed", new SocketException((int)error));

        GcDumpHeapSnapshotCollector.IsExpectedFlushTermination(exception, observed, timedOut)
            .Should().Be(expected);
    }

    [Fact]
    public void UnrelatedFailures_AreNotSuppressedAfterShutdown()
    {
        Exception[] failures =
        [
            new IOException("artifact write failed"),
            new FormatException("invalid event payload"),
            new InvalidOperationException("unexpected processing failure"),
            new OperationCanceledException(),
            new ObjectDisposedException("pipe"),
        ];

        foreach (var failure in failures)
        {
            GcDumpHeapSnapshotCollector.IsExpectedFlushTermination(failure, true, true)
                .Should().BeFalse();
        }
    }
}
