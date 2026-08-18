using DotnetDiagnostics.Core.Internal;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventPipeSessionShutdownTests
{
    [Fact]
    public async Task StopThenDrainAsync_StopDelayDoesNotConsumeDrainBudget()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingErrors = new List<Exception>();
        var drainBudget = TimeSpan.FromSeconds(1);

        var shutdown = EventPipeSessionShutdown.StopThenDrainAsync(
            async () =>
            {
                stopEntered.SetResult();
                await releaseStop.Task;
            },
            processingCompleted.Task,
            processingErrors.Add,
            drainBudget);

        await stopEntered.Task;
        await Task.Delay(drainBudget + TimeSpan.FromMilliseconds(100));
        releaseStop.SetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        processingCompleted.SetResult();

        await shutdown;
        processingErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task StopThenDrainAsync_ProcessingThatExceedsDrainBudgetStillFails()
    {
        var processingCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainBudget = TimeSpan.FromMilliseconds(50);

        var act = () => EventPipeSessionShutdown.StopThenDrainAsync(
            () => Task.CompletedTask,
            processingCompleted.Task,
            _ => { },
            drainBudget);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*did not drain within 0.1 seconds after session stop completed*");

        processingCompleted.SetResult();
    }
}
