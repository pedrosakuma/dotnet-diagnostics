using System.Diagnostics;

namespace DotnetDiagnostics.TestSupport.OperatingEnvelope;

public enum OperatingEnvelopeRequestOutcome
{
    Admitted,
    Rejected,
    Unknown,
}

public sealed record OperatingEnvelopeRequestProgressSnapshot(
    OperatingEnvelopeRequestAccounting Accounting,
    IReadOnlyList<string> Notes);

public sealed class OperatingEnvelopeRequestProgress(int planned)
{
    private readonly object _gate = new();
    private readonly RequestSlotState[] _slots = CreateSlots(planned);
    private readonly List<double> _latencies = [];
    private readonly List<string> _notes = [];

    public OperatingEnvelopeRequestProgressSnapshot Snapshot(double measurementWindowMilliseconds)
    {
        lock (_gate)
        {
            var offered = 0;
            var admitted = 0;
            var rejected = 0;
            var unknown = 0;
            foreach (var slot in _slots)
            {
                switch (slot)
                {
                    case RequestSlotState.Offered:
                        offered++;
                        unknown++;
                        break;
                    case RequestSlotState.Admitted:
                        offered++;
                        admitted++;
                        break;
                    case RequestSlotState.Rejected:
                        offered++;
                        rejected++;
                        break;
                    case RequestSlotState.Unknown:
                        offered++;
                        unknown++;
                        break;
                }
            }

            var accounting = new OperatingEnvelopeRequestAccounting(
                planned,
                offered,
                admitted,
                rejected,
                unknown,
                planned - offered,
                measurementWindowMilliseconds,
                _latencies.ToArray()).Validate();
            return new(accounting, _notes.ToArray());
        }
    }

    public void MarkOffered(int slot)
    {
        lock (_gate)
        {
            if (_slots[slot] != RequestSlotState.NotOffered)
            {
                throw new InvalidOperationException($"Request slot {slot} was already offered or completed.");
            }

            _slots[slot] = RequestSlotState.Offered;
        }
    }

    public void Complete(int slot, OperatingEnvelopeRequestOutcome outcome, double latencyMilliseconds)
    {
        if (!double.IsFinite(latencyMilliseconds) || latencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        }

        lock (_gate)
        {
            if (_slots[slot] != RequestSlotState.Offered)
            {
                throw new InvalidOperationException($"Request slot {slot} was not in flight.");
            }

            _slots[slot] = outcome switch
            {
                OperatingEnvelopeRequestOutcome.Admitted => RequestSlotState.Admitted,
                OperatingEnvelopeRequestOutcome.Rejected => RequestSlotState.Rejected,
                OperatingEnvelopeRequestOutcome.Unknown => RequestSlotState.Unknown,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
            _latencies.Add(latencyMilliseconds);
        }
    }

    public void AddNote(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        lock (_gate)
        {
            _notes.Add(note);
        }
    }

    private static RequestSlotState[] CreateSlots(int planned)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planned);
        return new RequestSlotState[planned];
    }

    private enum RequestSlotState : byte
    {
        NotOffered,
        Offered,
        Admitted,
        Rejected,
        Unknown,
    }
}

public static class OperatingEnvelopeDeadlineGuard
{
    public static void CheckRun(
        long runDeadlineTimestamp,
        long nowTimestamp,
        CancellationTokenSource runDeadline)
    {
        ArgumentNullException.ThrowIfNull(runDeadline);
        if (nowTimestamp >= runDeadlineTimestamp)
        {
            runDeadline.Cancel();
        }

        runDeadline.Token.ThrowIfCancellationRequested();
    }

    public static void CheckTrial(
        long runDeadlineTimestamp,
        long cellDeadlineTimestamp,
        long nowTimestamp,
        CancellationTokenSource runDeadline,
        CancellationTokenSource cellDeadline)
    {
        ArgumentNullException.ThrowIfNull(runDeadline);
        ArgumentNullException.ThrowIfNull(cellDeadline);
        if (nowTimestamp >= runDeadlineTimestamp)
        {
            runDeadline.Cancel();
        }
        else if (nowTimestamp >= cellDeadlineTimestamp)
        {
            cellDeadline.Cancel();
        }

        runDeadline.Token.ThrowIfCancellationRequested();
        cellDeadline.Token.ThrowIfCancellationRequested();
    }
}

public static class OperatingEnvelopeQueryPageRunner
{
    public static void ReadAll<TPage>(
        Func<TPage> readPage,
        Func<TPage, bool> hasMore,
        Action<TPage> visitPage,
        Action checkDeadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readPage);
        ArgumentNullException.ThrowIfNull(hasMore);
        ArgumentNullException.ThrowIfNull(visitPage);
        ArgumentNullException.ThrowIfNull(checkDeadline);

        while (true)
        {
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            var page = readPage();
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            visitPage(page);
            if (!hasMore(page))
            {
                return;
            }
        }
    }
}

public sealed record OperatingEnvelopeTaskSettlementResult(
    bool Settled,
    IReadOnlyList<string> Faults);

public sealed record OperatingEnvelopePostTerminationSettlementResult(
    bool TargetsSettled,
    IReadOnlyList<string> TargetFaults,
    bool OwnedTasksSettled,
    IReadOnlyList<string> OwnedTaskFaults)
{
    public bool CanFinalize =>
        TargetsSettled && TargetFaults.Count == 0 &&
        OwnedTasksSettled && OwnedTaskFaults.Count == 0;
}

public sealed class OperatingEnvelopeTaskRegistry
{
    private readonly List<Task> _measured = [];
    private readonly List<Task> _owned = [];

    public IReadOnlyList<Task> OwnedTasks => _owned;

    public void AddMeasured(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _measured.Add(task);
        _owned.Add(task);
    }

    public void AddCleanupOnly(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _owned.Add(task);
    }

    public Task WaitForMeasuredAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_measured).WaitAsync(cancellationToken);
}

public static class OperatingEnvelopeTaskSettlement
{
    public static async Task<OperatingEnvelopeTaskSettlementResult> SettleAsync(
        IReadOnlyList<Task> tasks,
        long absoluteDeadlineTimestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var all = Task.WhenAll(tasks);
        while (!all.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTicks = absoluteDeadlineTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                ObserveLateFaults(all);
                return new(false, []);
            }

            var delay = Task.Delay(
                TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency),
                cancellationToken);
            if (await Task.WhenAny(all, delay).ConfigureAwait(false) != all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObserveLateFaults(all);
                return new(false, []);
            }
        }

        var faults = tasks.Where(task => task.IsFaulted)
            .SelectMany(task => task.Exception!.Flatten().InnerExceptions)
            .Select(exception => $"{exception.GetType().Name}: {exception.Message}")
            .ToArray();
        return new(true, faults);
    }

    public static async Task<OperatingEnvelopePostTerminationSettlementResult> SettleAfterTargetsAsync(
        IReadOnlyList<Task> targetTerminationTasks,
        Func<IReadOnlyList<Task>> getOwnedTasks,
        long absoluteDeadlineTimestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetTerminationTasks);
        ArgumentNullException.ThrowIfNull(getOwnedTasks);

        var targets = await SettleAsync(
            targetTerminationTasks, absoluteDeadlineTimestamp, cancellationToken).ConfigureAwait(false);
        if (!targets.Settled || targets.Faults.Count > 0)
        {
            return new(targets.Settled, targets.Faults, false, []);
        }

        var ownedTasks = getOwnedTasks();
        ArgumentNullException.ThrowIfNull(ownedTasks);
        var work = await SettleAsync(
            ownedTasks, absoluteDeadlineTimestamp, cancellationToken).ConfigureAwait(false);
        return new(true, targets.Faults, work.Settled, work.Faults);
    }

    private static void ObserveLateFaults(Task task)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

public sealed record OperatingEnvelopeRequestAccounting(
    int Planned,
    int Offered,
    int Admitted,
    int Rejected,
    int Unknown,
    int NotOffered,
    double MeasurementWindowMilliseconds,
    IReadOnlyList<double> LatencyMilliseconds)
{
    public double? ThroughputPerSecond => MeasurementWindowMilliseconds > 0
        ? Admitted * 1000d / MeasurementWindowMilliseconds
        : null;

    public double? LatencyMinimumMilliseconds => LatencyMilliseconds.Count == 0
        ? null
        : LatencyMilliseconds.Min();

    public double? LatencyMedianMilliseconds => Percentile(0.5);
    public double? LatencyP95Milliseconds => Percentile(0.95);
    public double? LatencyMaximumMilliseconds => LatencyMilliseconds.Count == 0
        ? null
        : LatencyMilliseconds.Max();

    public OperatingEnvelopeRequestAccounting Validate()
    {
        if (Planned < 0 || Offered < 0 || Admitted < 0 || Rejected < 0 || Unknown < 0 ||
            NotOffered < 0 || MeasurementWindowMilliseconds < 0 ||
            Offered != Admitted + Rejected + Unknown ||
            Planned != Offered + NotOffered ||
            LatencyMilliseconds.Count > Planned ||
            LatencyMilliseconds.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new ArgumentException("Request accounting populations are inconsistent or out of bounds.");
        }

        return this;
    }

    private double? Percentile(double percentile)
    {
        if (LatencyMilliseconds.Count == 0)
        {
            return null;
        }

        var ordered = LatencyMilliseconds.Order().ToArray();
        var index = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[index];
    }
}

public sealed record OperatingEnvelopeCaptureAccounting(
    long? Offered,
    long? Accepted,
    long? Persisted,
    long? RecordRejected,
    long? QueueRejected,
    long? StorageRejected,
    long? Pending,
    long? Unknown,
    long? SourceRejected,
    bool UnknownTail,
    string AccountingScope)
{
    public long? Rejected =>
        RecordRejected is { } records && QueueRejected is { } queued && StorageRejected is { } storage
            ? records + queued + storage
            : null;

    public static OperatingEnvelopeCaptureAccounting Ephemeral(string scope) =>
        new(null, null, 0, null, null, null, null, null, null, true, scope);

    public static OperatingEnvelopeCaptureAccounting FromDurableQuality(
        long offered,
        long accepted,
        long persisted,
        long recordRejected,
        long queueRejected,
        long storageRejected,
        long pending,
        long? sourceRejected,
        bool unknownTail,
        string scope)
    {
        var accounted = persisted + recordRejected + queueRejected + storageRejected + pending;
        long? unknown = !unknownTail && offered >= accounted ? offered - accounted : null;
        return new(offered, accepted, persisted, recordRejected, queueRejected, storageRejected,
            pending, unknown, sourceRejected, unknownTail, scope);
    }
}

public sealed record OperatingEnvelopeTargetResources(
    int ProcessId,
    double? CpuMilliseconds,
    long? PeakWorkingSetBytes,
    IReadOnlyList<string> Notes);

public sealed record OperatingEnvelopeTargetRequests(
    int ProcessId,
    OperatingEnvelopeRequestAccounting Accounting);

public sealed record OperatingEnvelopeDiagnosticResources(
    double? CpuMilliseconds,
    long? PeakWorkingSetBytes,
    long? AllocatedBytes,
    long? Gen0Collections,
    long? Gen1Collections,
    long? Gen2Collections,
    IReadOnlyList<string> Notes);

public sealed record OperatingEnvelopeArtifactMeasurement(
    string Kind,
    bool CollectionSucceeded,
    string? CaptureId,
    string? CaptureState,
    OperatingEnvelopeCaptureAccounting Accounting,
    long? RecordCount,
    long? StoreBytes,
    IReadOnlyList<OperatingEnvelopeArtifactHash> StoreFiles,
    double CollectorElapsedMilliseconds,
    double? StoreEncodeAndSealMilliseconds,
    double? QueryOpenMilliseconds,
    double? QueryMilliseconds,
    long? QueriedRecords,
    IReadOnlyList<string> LossAndCapNotes,
    string? Error,
    IReadOnlyDictionary<string, double?>? TargetGcCounters = null,
    double? CollectorDrainMilliseconds = null,
    double? QueryCloseMilliseconds = null,
    double? EvidenceHashMilliseconds = null);

public sealed record OperatingEnvelopeTrialResult(
    OperatingEnvelopeTrialPlan Plan,
    string ConfigurationHash,
    OperatingEnvelopeTrialOutcome Outcome,
    OperatingEnvelopeStopOutcome StopOutcome,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    double WarmupMilliseconds,
    OperatingEnvelopeRequestAccounting WarmupRequests,
    OperatingEnvelopeRequestAccounting Requests,
    IReadOnlyList<OperatingEnvelopeTargetRequests> TargetRequests,
    IReadOnlyList<OperatingEnvelopeTargetResources> Targets,
    OperatingEnvelopeDiagnosticResources DiagnosticProcess,
    IReadOnlyList<OperatingEnvelopeArtifactMeasurement> Artifacts,
    double CleanupMilliseconds,
    bool CleanupSucceeded,
    IReadOnlyList<string> CleanupErrors,
    IReadOnlyList<string> Notes,
    string? Error);

public sealed record OperatingEnvelopePairResult(
    string PairId,
    OperatingEnvelopePopulation Population,
    bool IsValid,
    IReadOnlyList<string> InvalidReasons,
    IReadOnlyList<string> TrialIds);

public static class OperatingEnvelopePairValidation
{
    public static OperatingEnvelopePairResult Validate(
        string pairId,
        OperatingEnvelopePopulation population,
        IReadOnlyList<OperatingEnvelopeTrialResult> trials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        ArgumentNullException.ThrowIfNull(trials);
        var reasons = new List<string>();
        var pair = trials.Where(trial => trial.Plan.PairId == pairId)
            .OrderBy(trial => trial.Plan.OrderInPair)
            .ToArray();

        if (pair.Length != 2)
        {
            reasons.Add($"Expected two outcomes for the pair; found {pair.Length}.");
        }

        if (pair.Any(trial => trial.Plan.Population != population))
        {
            reasons.Add("Pair members do not use the same workload population.");
        }

        if (pair.Select(trial => trial.Plan.StorageMode).Distinct().Count() != 2)
        {
            reasons.Add("Pair does not contain exactly one ephemeral and one durable trial.");
        }

        if (pair.Any(trial => trial.Outcome != OperatingEnvelopeTrialOutcome.Completed ||
                              trial.StopOutcome != OperatingEnvelopeStopOutcome.None ||
                              trial.Artifacts.Any(artifact => !artifact.CollectionSucceeded)))
        {
            reasons.Add("At least one trial did not produce a completed collection outcome.");
        }

        if (pair.Select(trial => trial.ConfigurationHash).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            reasons.Add("Pair members were run with different configuration hashes.");
        }

        return new(pairId, population, reasons.Count == 0, reasons.AsReadOnly(),
            pair.Select(trial => trial.Plan.TrialId).ToArray());
    }
}
