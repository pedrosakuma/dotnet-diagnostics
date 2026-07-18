using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventPipeThreadPoolCollectorTests
{
    [Theory]
    [InlineData(50, "ThreadPoolWorkerThreadStart")]
    [InlineData(55, "ThreadPoolWorkerThreadAdjustmentAdjustment")]
    [InlineData(59, "ThreadPoolMinMaxThreadsChanged")]
    [InlineData(60, "ThreadPoolWorkingThreadCount")]
    public void GetCanonicalEventName_PrefersRuntimeEventId_WhenWindowsReportsTaskGuid(
        int eventId,
        string expected)
    {
        var name = EventPipeThreadPoolCollector.GetCanonicalEventName(
            eventId,
            "Task(8a9a44ab-f681-4271-8810-830dab9f5621)");

        name.Should().Be(expected);
    }

    [Theory]
    [InlineData("0", "Warmup")]
    [InlineData("6", "Starvation")]
    [InlineData("8", "CooperativeBlocking")]
    [InlineData("Starvation", "Starvation")]
    [InlineData("99", "99")]
    public void NormalizeAdjustmentReason_MapsNumericRuntimeEnumValues(
        string reason,
        string expected)
    {
        EventPipeThreadPoolCollector.NormalizeAdjustmentReason(reason)
            .Should().Be(expected);
    }
}
