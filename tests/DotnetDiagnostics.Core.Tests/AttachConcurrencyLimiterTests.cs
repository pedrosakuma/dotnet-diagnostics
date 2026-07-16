using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for the per-pid attach concurrency gate (#452, D2). Two simultaneous live
/// attaches against the same pid must serialize: one runs while the other gets a structured
/// retriable "busy" envelope instead of failing hard. Dump-based work (null pid) is never gated.
/// </summary>
public sealed class AttachConcurrencyLimiterTests
{
    [Fact]
    public async Task GuardAttach_SecondConcurrentAttachSamePid_ReturnsBusy()
    {
        var limiter = new AttachConcurrencyLimiter(maxPerProcess: 1, acquireTimeout: TimeSpan.Zero);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = AttachGuard.GuardAttachAsync<string>("collect_thread_snapshot", 1234, async () =>
        {
            entered.SetResult();
            await release.Task;
            return DiagnosticResult.Ok("ok", "first attach");
        }, CancellationToken.None, limiter);

        await entered.Task;

        var second = await AttachGuard.GuardAttachAsync<string>("collect_thread_snapshot", 1234,
            () => Task.FromResult(DiagnosticResult.Ok("ok", "second attach")), CancellationToken.None, limiter);

        second.IsError.Should().BeTrue();
        second.Error!.Kind.Should().Be("Busy");
        var hint = second.Hints.Should().ContainSingle(h => h.NextTool == "collect_thread_snapshot").Which;
        hint.SuggestedArguments.Should().BeNull(
            "AttachGuard cannot assume that a tool accepts processId when the caller did not supply retry arguments");

        release.SetResult();
        (await first).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task GuardAttach_DifferentPids_DoNotBlock()
    {
        var limiter = new AttachConcurrencyLimiter(maxPerProcess: 1, acquireTimeout: TimeSpan.Zero);

        var a = await AttachGuard.GuardAttachAsync<string>("inspect_live_heap", 1,
            () => Task.FromResult(DiagnosticResult.Ok("a", "pid 1")), CancellationToken.None, limiter);
        var b = await AttachGuard.GuardAttachAsync<string>("inspect_live_heap", 2,
            () => Task.FromResult(DiagnosticResult.Ok("b", "pid 2")), CancellationToken.None, limiter);

        a.IsError.Should().BeFalse();
        b.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task GuardAttach_PermitReleasedAfterBody_AllowsNextAttach()
    {
        var limiter = new AttachConcurrencyLimiter(maxPerProcess: 1, acquireTimeout: TimeSpan.Zero);

        var first = await AttachGuard.GuardAttachAsync<string>("collect_process_dump", 99,
            () => Task.FromResult(DiagnosticResult.Ok("x", "first")), CancellationToken.None, limiter);
        var second = await AttachGuard.GuardAttachAsync<string>("collect_process_dump", 99,
            () => Task.FromResult(DiagnosticResult.Ok("y", "second")), CancellationToken.None, limiter);

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task GuardAttach_NoPid_IsNeverGated()
    {
        var limiter = new AttachConcurrencyLimiter(maxPerProcess: 1, acquireTimeout: TimeSpan.Zero);
        var release = new TaskCompletionSource();

        var first = AttachGuard.GuardAttachAsync<string>("inspect_dump", null, async () =>
        {
            await release.Task;
            return DiagnosticResult.Ok("ok", "dump");
        }, CancellationToken.None, limiter);

        var second = await AttachGuard.GuardAttachAsync<string>("inspect_dump", null,
            () => Task.FromResult(DiagnosticResult.Ok("ok", "dump2")), CancellationToken.None, limiter);

        second.IsError.Should().BeFalse();
        release.SetResult();
        (await first).IsError.Should().BeFalse();
    }

    [Fact]
    public void ClassifyAttachFailure_UsesCanonicalDumpAndOffCpuHints()
    {
        var result = AttachGuard.ClassifyAttachFailure<string>(
            "collect_thread_snapshot",
            4242,
            new Win32Exception(1));

        result.Error!.Kind.Should().Be("PermissionDenied");

        var dump = result.Hints.Should().ContainSingle(h => h.NextTool == "collect_process_dump").Which;
        dump.Reason.Should().Contain("inspect_heap(source=\"dump\")");
        dump.Reason.Should().NotContain("inspect_dump");

        var offCpu = result.Hints.Should().ContainSingle(h => h.NextTool == "collect_sample").Which;
        offCpu.SuggestedArguments!["kind"].Should().Be("off_cpu");
        offCpu.SuggestedArguments["processId"].Should().Be(4242);
    }
}
