using System.Collections.ObjectModel;

namespace DotnetDiagnostics.TestSupport.OperatingEnvelope;

public enum OperatingEnvelopePopulation
{
    IdleCounters,
    CpuAndCounters,
    AllocationAndCounters,
    ActivityAndCounters,
    QueueAndThread,
    MixedCollectors,
    TwoTargetsSharedDisk,
}

public enum OperatingEnvelopeStorageMode
{
    Ephemeral,
    Durable,
}

public enum OperatingEnvelopeTrialOutcome
{
    Completed,
    Incomplete,
    Failed,
    Stopped,
}

public enum OperatingEnvelopeStopOutcome
{
    None,
    CellDeadlineExceeded,
    RunDeadlineExceeded,
    OutputBudgetExceeded,
    ResourceLimitExceeded,
    Cancelled,
    TargetExited,
    CollectionFailed,
    PersistenceFailed,
    CleanupFailed,
}

public sealed record OperatingEnvelopeConfiguration
{
    public static OperatingEnvelopeConfiguration Default { get; } = new();

    public TimeSpan WarmupDuration { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CellDeadline { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan RunDeadline { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan RequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaximumRequestsPerCell { get; init; } = 128;
    public long MaximumOutputBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public OperatingEnvelopeConfiguration Validate()
    {
        if (WarmupDuration < TimeSpan.Zero || WarmupDuration > TimeSpan.FromSeconds(10) ||
            WindowDuration < TimeSpan.FromSeconds(6) || WindowDuration > TimeSpan.FromSeconds(30) ||
            CellDeadline < WindowDuration + WarmupDuration || CellDeadline > TimeSpan.FromMinutes(3) ||
            RunDeadline < CellDeadline || RunDeadline > TimeSpan.FromHours(4) ||
            RequestInterval < TimeSpan.FromMilliseconds(50) || RequestInterval > TimeSpan.FromSeconds(5) ||
            MaximumRequestsPerCell is < 1 or > 512 || MaximumOutputBytes is < 1024 * 1024 or > 32L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(OperatingEnvelopeConfiguration),
                "Operating-envelope bounds are outside the finite protocol limits.");
        }

        var plannedRequests = OperatingEnvelopeProtocol.MaximumTargetsPerCell *
            (long)Math.Ceiling(WindowDuration.TotalMilliseconds / RequestInterval.TotalMilliseconds);
        if (plannedRequests > MaximumRequestsPerCell)
        {
            throw new ArgumentException("The request schedule exceeds MaximumRequestsPerCell.");
        }

        return this;
    }
}

public sealed record OperatingEnvelopePopulationDefinition(
    OperatingEnvelopePopulation Population,
    string Name,
    int TargetCount,
    IReadOnlyList<string> MeasuredRequestPaths,
    string CapturePlan);

public sealed record OperatingEnvelopeTrialPlan(
    string TrialId,
    string PairId,
    int PairNumber,
    int OrderInPair,
    OperatingEnvelopePopulation Population,
    OperatingEnvelopeStorageMode StorageMode,
    int TargetCount,
    IReadOnlyList<string> MeasuredRequestPaths,
    string CapturePlan,
    string StoreDirectoryName);

public sealed record OperatingEnvelopeSchedule(
    string ProtocolVersion,
    OperatingEnvelopeConfiguration Configuration,
    IReadOnlyList<OperatingEnvelopeTrialPlan> Trials);

public static class OperatingEnvelopeProtocol
{
    public const string Version = "real-operating-envelope/1";
    public const int PairsPerPopulation = 3;
    public const int MaximumTargetsPerCell = 2;

    private static readonly ReadOnlyCollection<OperatingEnvelopePopulationDefinition> PopulationDefinitions =
        new(
        [
            new(OperatingEnvelopePopulation.IdleCounters, "idle-counters", 1, Paths(),
                "System.Runtime counters only; no measured-window requests"),
            new(OperatingEnvelopePopulation.CpuAndCounters, "cpu-counters", 1,
                Paths("/cpu-burn?ms=100"), "EventPipe CPU sample plus System.Runtime counters"),
            new(OperatingEnvelopePopulation.AllocationAndCounters, "allocation-counters", 1,
                Paths("/render?count=64"), "EventPipe allocation sample plus System.Runtime counters"),
            new(OperatingEnvelopePopulation.ActivityAndCounters, "activity-counters", 1,
                Paths("/activity?delayMs=10"), "ActivitySource capture plus System.Runtime counters"),
            new(OperatingEnvelopePopulation.QueueAndThread, "queue-thread", 1,
                Paths("/threadpool/queue?globalItems=4&localItems=4&blockMs=100"),
                "ThreadPool EventPipe capture while the existing queue route is exercised"),
            new(OperatingEnvelopePopulation.MixedCollectors, "mixed-collectors", 1,
                Paths("/cpu-burn?ms=100", "/render?count=64", "/activity?delayMs=10&collectGc=true", "/parse"),
                "Concurrent counters, GC, exceptions, ThreadPool, and process resources in one durable sweep capture"),
            new(OperatingEnvelopePopulation.TwoTargetsSharedDisk, "two-targets-shared-disk", 2,
                Paths("/cpu-burn?ms=100"),
                "One independent counter capture per target under the same fresh store root"),
        ]);

    public static IReadOnlyList<OperatingEnvelopePopulationDefinition> Populations => PopulationDefinitions;

    public static OperatingEnvelopeSchedule CreateSchedule(OperatingEnvelopeConfiguration? configuration = null)
    {
        var config = (configuration ?? OperatingEnvelopeConfiguration.Default).Validate();
        var trials = new List<OperatingEnvelopeTrialPlan>(
            PopulationDefinitions.Count * PairsPerPopulation * 2);

        foreach (var definition in PopulationDefinitions)
        {
            for (var pairNumber = 1; pairNumber <= PairsPerPopulation; pairNumber++)
            {
                var pairId = $"{definition.Name}-pair-{pairNumber:D2}";
                var firstMode = pairNumber % 2 == 0
                    ? OperatingEnvelopeStorageMode.Durable
                    : OperatingEnvelopeStorageMode.Ephemeral;
                var secondMode = firstMode == OperatingEnvelopeStorageMode.Ephemeral
                    ? OperatingEnvelopeStorageMode.Durable
                    : OperatingEnvelopeStorageMode.Ephemeral;

                AddTrial(firstMode, orderInPair: 1);
                AddTrial(secondMode, orderInPair: 2);

                void AddTrial(OperatingEnvelopeStorageMode mode, int orderInPair)
                {
                    var trialId = $"{pairId}-{mode.ToString().ToLowerInvariant()}";
                    trials.Add(new(
                        trialId,
                        pairId,
                        pairNumber,
                        orderInPair,
                        definition.Population,
                        mode,
                        definition.TargetCount,
                        definition.MeasuredRequestPaths,
                        definition.CapturePlan,
                        $"stores/{trialId}"));
                }
            }
        }

        return new(Version, config, trials.AsReadOnly());
    }

    public static IReadOnlyList<OperatingEnvelopeTrialPlan> GetPair(
        OperatingEnvelopeSchedule schedule,
        string pairId)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        return schedule.Trials.Where(trial => trial.PairId == pairId)
            .OrderBy(trial => trial.OrderInPair)
            .ToArray();
    }

    private static IReadOnlyList<string> Paths(params string[] values) => Array.AsReadOnly(values);
}
