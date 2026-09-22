using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

internal enum CounterMetadataState
{
    Missing,
    Valid,
    Nonpositive,
    Nonfinite,
}

internal enum CoverageGapState
{
    Unknown,
    NoGap,
    Gap,
}

internal enum CounterResetState
{
    Unknown,
}

internal sealed record DurableCounterSourceClock(string Domain, string Origin);

internal sealed record DurableCounterObservation(
    CounterValue Counter,
    long? SourceTimeTicks,
    DurableCounterSourceClock Clock,
    int? RequestedEncodedBytes = null);

internal sealed record DurableCounterRecord(
    long Sequence,
    string Provider,
    string Name,
    string DisplayName,
    string? Unit,
    double Value,
    CounterKind Kind,
    double? IntervalSec,
    CounterMetadataState IntervalState,
    long? DisplayScaleTicks,
    CounterMetadataState DisplayScaleState,
    long? SourceTimeTicks,
    string ClockDomain,
    string ClockOrigin,
    CoverageGapState CoverageGap,
    CounterResetState ResetState,
    int EncodedBytes);

internal sealed record DurableCounterPipelineLimits(
    int RecordEncodedBytes = 4_096,
    int ProviderUtf8Bytes = 128,
    int NameUtf8Bytes = 256,
    int DisplayNameUtf8Bytes = 512,
    int UnitUtf8Bytes = 64,
    int DistinctKeys = 128,
    long OwnedBufferBytes = 2_097_152,
    int OwnedRecords = 321,
    int QueueRecords = 256,
    int InCopyRecords = 1,
    int BatchRecords = 64,
    long BatchOwnedBytes = 262_144,
    TimeSpan? BatchMaxAge = null,
    int PageRows = 100,
    int ResultBytes = 1_048_576,
    int ActiveCaptures = 1);

internal enum DurableCounterOfferStatus
{
    Accepted,
    Invalid,
    TooManyKeys,
    RecordTooLarge,
    OwnedBudgetFull,
    QueueFull,
    Closed,
    WriterFailed,
}

internal sealed record DurableCounterOfferResult(
    long Sequence,
    DurableCounterOfferStatus Status,
    string? Reason = null);

internal enum DurableCounterCommitOutcome
{
    Committed,
    Failed,
    Unknown,
}

internal sealed record DurableCounterCommitResult(
    DurableCounterCommitOutcome Outcome,
    string? Error = null);

// Encoded memory is owned by the pipeline and is valid only until CommitAsync returns.
internal readonly record struct DurableCounterSinkRecord(
    DurableCounterRecord Record,
    ReadOnlyMemory<byte> Encoded);

internal interface IDurableCounterSink
{
    ValueTask<DurableCounterCommitResult> CommitAsync(
        IReadOnlyList<DurableCounterSinkRecord> records,
        CancellationToken cancellationToken);

    ValueTask FinalizeAsync(CancellationToken cancellationToken);
}

internal interface IDurableCounterBatchAgeWaiter
{
    ValueTask WaitAsync(TimeSpan age, CancellationToken cancellationToken);
}

internal sealed class ImmediateCounterBatchAgeWaiter : IDurableCounterBatchAgeWaiter
{
    internal static ImmediateCounterBatchAgeWaiter Instance { get; } = new();

    public ValueTask WaitAsync(TimeSpan age, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class SystemCounterBatchAgeWaiter : IDurableCounterBatchAgeWaiter
{
    internal static SystemCounterBatchAgeWaiter Instance { get; } = new();

    public ValueTask WaitAsync(TimeSpan age, CancellationToken cancellationToken)
        => new(Task.Delay(age, cancellationToken));
}

internal sealed record DurableCounterAccounting(
    long Offered,
    long Rejected,
    long Admitted,
    long InCopy,
    long Queued,
    long ActiveBatch,
    long Committed,
    long FailedAfterAdmission,
    long AbandonedKnown,
    long UnknownCommitOutcome,
    long OwnedRecords,
    long OwnedBytes,
    long PeakOwnedRecords,
    long PeakOwnedBytes,
    bool AdmissionCancelled,
    IReadOnlyDictionary<string, long> Rejections)
{
    internal bool IsCleanQuiescent
        => InCopy == 0 && Queued == 0 && ActiveBatch == 0 && OwnedRecords == 0 && OwnedBytes == 0;

    internal bool HasKnownTerminalConservation
        => UnknownCommitOutcome == 0
            && Offered == Rejected + Admitted
            && Admitted == InCopy + Queued + ActiveBatch + Committed + FailedAfterAdmission + AbandonedKnown;
}

internal enum DurableCounterPipelineState
{
    Accepting,
    Draining,
    Completed,
    Cancelled,
    Failed,
    UnknownCommitOutcome,
}

internal sealed record DurableCounterDrainResult(
    bool CompletedWithinTimeout,
    DurableCounterPipelineState State,
    string? Error);

internal sealed class DurableCounterGlobalBudget
{
    private readonly object _gate = new();
    private readonly DurableCounterPipelineLimits _limits;
    private int _captures;
    private long _ownedRecords;
    private long _ownedBytes;

    internal DurableCounterGlobalBudget(DurableCounterPipelineLimits limits) => _limits = limits;

    internal IDisposable AcquireCapture()
    {
        lock (_gate)
        {
            if (_captures >= _limits.ActiveCaptures)
            {
                throw new DurableCounterPipelineException("ActiveCaptureLimit", "The active capture limit is full.");
            }

            _captures++;
            return new ReleaseAction(() =>
            {
                lock (_gate)
                {
                    _captures--;
                }
            });
        }
    }

    internal bool TryReserveRecord(int capacity)
    {
        lock (_gate)
        {
            if (_ownedRecords >= _limits.OwnedRecords || _ownedBytes + capacity > _limits.OwnedBufferBytes)
            {
                return false;
            }

            _ownedRecords++;
            _ownedBytes += capacity;
            return true;
        }
    }

    internal void ReleaseRecord(int capacity)
    {
        lock (_gate)
        {
            _ownedRecords--;
            _ownedBytes -= capacity;
        }
    }

    internal (long Records, long Bytes) Snapshot()
    {
        lock (_gate)
        {
            return (_ownedRecords, _ownedBytes);
        }
    }
}

internal sealed class DurableCounterPipelineException : Exception
{
    internal DurableCounterPipelineException(string code, string message)
        : base(message) => Code = code;

    internal string Code { get; }
}

internal sealed class DurableCounterPipeline : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly DurableCounterPipelineLimits _limits;
    private readonly DurableCounterGlobalBudget _globalBudget;
    private readonly IDurableCounterSink _sink;
    private readonly IDurableCounterBatchAgeWaiter _batchAgeWaiter;
    private readonly Action? _batchAvailabilityWaitRegistered;
    private readonly Action<DurableCounterPipeline>? _batchAgeWaitWon;
    private readonly Task _writerStartGate;
    private readonly IDisposable _captureLease;
    private readonly Queue<OwnedRecord> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Dictionary<CounterKey, long?> _previousSourceTicks = new();
    private readonly Dictionary<string, long> _rejections = new(StringComparer.Ordinal);
    private readonly Task _writer;

    private long _nextSequence;
    private long _offered;
    private long _rejected;
    private long _admitted;
    private long _inCopy;
    private long _queued;
    private long _activeBatch;
    private long _committed;
    private long _failedAfterAdmission;
    private long _abandonedKnown;
    private long _unknownCommitOutcome;
    private long _ownedRecords;
    private long _ownedBytes;
    private long _peakOwnedRecords;
    private long _peakOwnedBytes;
    private bool _admissionClosed;
    private bool _admissionCancelled;
    private bool _disposed;
    private DurableCounterPipelineState _state = DurableCounterPipelineState.Accepting;
    private string? _terminalError;
    private Task? _finalizeTask;

    internal DurableCounterPipeline(
        DurableCounterPipelineLimits limits,
        DurableCounterGlobalBudget globalBudget,
        IDurableCounterSink sink,
        IDurableCounterBatchAgeWaiter? batchAgeWaiter = null,
        Task? writerStartGate = null,
        Action? batchAvailabilityWaitRegistered = null,
        Action<DurableCounterPipeline>? batchAgeWaitWon = null)
    {
        _limits = limits;
        _globalBudget = globalBudget;
        _sink = sink;
        _batchAgeWaiter = batchAgeWaiter ?? SystemCounterBatchAgeWaiter.Instance;
        _writerStartGate = writerStartGate ?? Task.CompletedTask;
        _batchAvailabilityWaitRegistered = batchAvailabilityWaitRegistered;
        _batchAgeWaitWon = batchAgeWaitWon;
        _captureLease = globalBudget.AcquireCapture();
        _writer = RunWriterAsync();
    }

    internal Task Completion => _writer;

    internal DurableCounterPipelineState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal string? TerminalError
    {
        get
        {
            lock (_gate)
            {
                return _terminalError;
            }
        }
    }

    internal DurableCounterOfferResult TryWrite(DurableCounterObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        long sequence;
        CoverageGapState gap;

        lock (_gate)
        {
            sequence = checked(++_nextSequence);
            _offered++;
            if (_state == DurableCounterPipelineState.Failed
                || _state == DurableCounterPipelineState.UnknownCommitOutcome)
            {
                return Reject(sequence, DurableCounterOfferStatus.WriterFailed, "WriterFailed");
            }
            if (_admissionClosed)
            {
                return Reject(sequence, DurableCounterOfferStatus.Closed, "AdmissionClosed");
            }

            if (!TryValidateAndComputeGap(observation, out gap, out var invalidStatus, out var reason))
            {
                return Reject(sequence, invalidStatus, reason);
            }

            if (observation.RequestedEncodedBytes > _limits.RecordEncodedBytes)
            {
                return Reject(sequence, DurableCounterOfferStatus.RecordTooLarge, "RecordEncodedBytes");
            }
            if (_inCopy >= _limits.InCopyRecords || !TryReserveOwnedRecord())
            {
                return Reject(sequence, DurableCounterOfferStatus.OwnedBudgetFull, "OwnedBudgetFull");
            }

            _inCopy++;
            ObserveOwnedPeak();
        }

        OwnedRecord? owned = null;
        try
        {
            owned = OwnAndEncode(sequence, observation, gap);
        }
        catch (DurableCounterPipelineException exception)
        {
            lock (_gate)
            {
                _inCopy--;
                ReleaseOwnedRecord();
                return Reject(sequence, DurableCounterOfferStatus.RecordTooLarge, exception.Code);
            }
        }
        catch
        {
            lock (_gate)
            {
                _inCopy--;
                ReleaseOwnedRecord();
            }
            throw;
        }

        lock (_gate)
        {
            _inCopy--;
            if (_admissionClosed)
            {
                owned.Dispose();
                ReleaseOwnedRecord();
                return Reject(sequence, DurableCounterOfferStatus.Closed, "AdmissionClosedDuringCopy");
            }
            if (_queue.Count >= _limits.QueueRecords)
            {
                owned.Dispose();
                ReleaseOwnedRecord();
                return Reject(sequence, DurableCounterOfferStatus.QueueFull, "QueueFull");
            }

            _queue.Enqueue(owned);
            _queued++;
            _admitted++;
            _available.Release();
            return new DurableCounterOfferResult(sequence, DurableCounterOfferStatus.Accepted);
        }
    }

    internal void StopAdmission()
        => CloseAdmission(cancelled: false);

    internal void CancelAdmission()
        => CloseAdmission(cancelled: true);

    private void CloseAdmission(bool cancelled)
    {
        lock (_gate)
        {
            if (_admissionClosed)
            {
                return;
            }

            _admissionClosed = true;
            _admissionCancelled = cancelled;
            if (_state == DurableCounterPipelineState.Accepting)
            {
                _state = DurableCounterPipelineState.Draining;
            }
            _available.Release();
        }
    }

    internal async Task<DurableCounterDrainResult> DrainAsync(TimeSpan timeout)
    {
        StopAdmission();
        var completed = await WaitWithTimeoutAsync(_writer, timeout).ConfigureAwait(false);
        return new DurableCounterDrainResult(completed, State, TerminalError);
    }

    internal async Task<DurableCounterDrainResult> CancelAndDrainAsync(TimeSpan timeout)
    {
        CancelAdmission();
        var completed = await WaitWithTimeoutAsync(_writer, timeout).ConfigureAwait(false);
        return new DurableCounterDrainResult(completed, State, TerminalError);
    }

    internal async Task<bool> FinalizeAsync(TimeSpan timeout)
    {
        if (!_writer.IsCompletedSuccessfully
            || State is not (DurableCounterPipelineState.Completed or DurableCounterPipelineState.Cancelled))
        {
            return false;
        }

        Task finalize;
        lock (_gate)
        {
            _finalizeTask ??= _sink.FinalizeAsync(CancellationToken.None).AsTask();
            finalize = _finalizeTask;
        }
        if (!await WaitWithTimeoutAsync(finalize, timeout).ConfigureAwait(false))
        {
            return false;
        }
        await finalize.ConfigureAwait(false);
        return true;
    }

    internal DurableCounterAccounting GetAccounting()
    {
        lock (_gate)
        {
            return new DurableCounterAccounting(
                _offered,
                _rejected,
                _admitted,
                _inCopy,
                _queued,
                _activeBatch,
                _committed,
                _failedAfterAdmission,
                _abandonedKnown,
                _unknownCommitOutcome,
                _ownedRecords,
                _ownedBytes,
                _peakOwnedRecords,
                _peakOwnedBytes,
                _admissionCancelled,
                new Dictionary<string, long>(_rejections, StringComparer.Ordinal));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        StopAdmission();
        await _writer.ConfigureAwait(false);
        Task? finalize;
        lock (_gate)
        {
            finalize = _finalizeTask;
        }
        if (finalize is not null)
        {
            await finalize.ConfigureAwait(false);
        }
        _available.Dispose();
        _captureLease.Dispose();
        _disposed = true;
    }

    private async Task RunWriterAsync()
    {
        try
        {
            await _writerStartGate.ConfigureAwait(false);
            while (true)
            {
                lock (_gate)
                {
                    if (_admissionClosed && _queue.Count == 0)
                    {
                        SetCleanTerminalState();
                        return;
                    }
                }

                await _available.WaitAsync().ConfigureAwait(false);
                List<OwnedRecord>? batch;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        if (_admissionClosed)
                        {
                            SetCleanTerminalState();
                            return;
                        }
                        continue;
                    }

                    batch = DequeueBatch();
                }

                if (batch.Count < _limits.BatchRecords && !_admissionClosed)
                {
                    var ageTask = _batchAgeWaiter.WaitAsync(
                        _limits.BatchMaxAge ?? TimeSpan.FromMilliseconds(100),
                        CancellationToken.None).AsTask();
                    while (batch.Count < _limits.BatchRecords)
                    {
                        lock (_gate)
                        {
                            FillBatch(batch);
                            if (batch.Count >= _limits.BatchRecords || _admissionClosed)
                            {
                                break;
                            }
                        }

                        if (ageTask.IsCompleted)
                        {
                            break;
                        }

                        using var availableCancellation = new CancellationTokenSource();
                        var availableTask = _available.WaitAsync(availableCancellation.Token);
                        _batchAvailabilityWaitRegistered?.Invoke();
                        if (await Task.WhenAny(ageTask, availableTask).ConfigureAwait(false) == ageTask)
                        {
                            _batchAgeWaitWon?.Invoke(this);
                            availableCancellation.Cancel();
                            var consumedAvailablePermit = false;
                            try
                            {
                                await availableTask.ConfigureAwait(false);
                                consumedAvailablePermit = true;
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            if (consumedAvailablePermit)
                            {
                                lock (_gate)
                                {
                                    FillBatch(batch);
                                }
                            }
                            break;
                        }
                    }
                }

                var records = batch.Select(static item => item.AsSinkRecord()).ToArray();
                DurableCounterCommitResult result;
                try
                {
                    result = await _sink.CommitAsync(records, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    result = new DurableCounterCommitResult(DurableCounterCommitOutcome.Failed, exception.Message);
                }

                lock (_gate)
                {
                    _activeBatch -= batch.Count;
                    switch (result.Outcome)
                    {
                        case DurableCounterCommitOutcome.Committed:
                            _committed += batch.Count;
                            break;
                        case DurableCounterCommitOutcome.Failed:
                            _failedAfterAdmission += batch.Count;
                            FailQueuedRecords();
                            _state = DurableCounterPipelineState.Failed;
                            _terminalError = result.Error ?? "Sink commit failed.";
                            _admissionClosed = true;
                            break;
                        case DurableCounterCommitOutcome.Unknown:
                            _unknownCommitOutcome += batch.Count;
                            FailQueuedRecords();
                            _state = DurableCounterPipelineState.UnknownCommitOutcome;
                            _terminalError = result.Error ?? "Sink commit outcome is unknown.";
                            _admissionClosed = true;
                            break;
                    }

                    foreach (var item in batch)
                    {
                        item.Dispose();
                        ReleaseOwnedRecord();
                    }

                    if (result.Outcome != DurableCounterCommitOutcome.Committed)
                    {
                        return;
                    }
                    if (_admissionClosed && _queue.Count == 0)
                    {
                        SetCleanTerminalState();
                        return;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                FailQueuedRecords();
                _state = DurableCounterPipelineState.Failed;
                _terminalError = exception.Message;
                _admissionClosed = true;
            }
        }
    }

    private List<OwnedRecord> DequeueBatch()
    {
        var batch = new List<OwnedRecord>(_limits.BatchRecords);
        FillBatch(batch);
        return batch;
    }

    private void FillBatch(List<OwnedRecord> batch)
    {
        while (_queue.Count > 0
            && batch.Count < _limits.BatchRecords
            && ((long)batch.Count + 1) * _limits.RecordEncodedBytes <= _limits.BatchOwnedBytes)
        {
            batch.Add(_queue.Dequeue());
            _queued--;
            _activeBatch++;
        }
    }

    private void FailQueuedRecords()
    {
        while (_queue.Count > 0)
        {
            var queued = _queue.Dequeue();
            queued.Dispose();
            ReleaseOwnedRecord();
            _queued--;
            _failedAfterAdmission++;
        }
    }

    private DurableCounterOfferResult Reject(
        long sequence,
        DurableCounterOfferStatus status,
        string reason)
    {
        _rejected++;
        _rejections.TryGetValue(reason, out var current);
        _rejections[reason] = current + 1;
        return new DurableCounterOfferResult(sequence, status, reason);
    }

    private bool TryValidateAndComputeGap(
        DurableCounterObservation observation,
        out CoverageGapState gap,
        out DurableCounterOfferStatus status,
        out string reason)
    {
        gap = CoverageGapState.Unknown;
        status = DurableCounterOfferStatus.Invalid;
        reason = string.Empty;
        var counter = observation.Counter;

        if (!IsBoundedUtf8(counter.Provider, _limits.ProviderUtf8Bytes)
            || !IsBoundedUtf8(counter.Name, _limits.NameUtf8Bytes)
            || !IsBoundedUtf8(counter.DisplayName, _limits.DisplayNameUtf8Bytes)
            || (counter.Unit is not null && !IsBoundedUtf8(counter.Unit, _limits.UnitUtf8Bytes)))
        {
            reason = "StringUtf8Limit";
            return false;
        }
        if (string.IsNullOrWhiteSpace(observation.Clock.Domain)
            || string.IsNullOrWhiteSpace(observation.Clock.Origin)
            || !IsBoundedUtf8(observation.Clock.Domain, _limits.RecordEncodedBytes)
            || !IsBoundedUtf8(observation.Clock.Origin, _limits.RecordEncodedBytes))
        {
            reason = "InvalidClockMetadata";
            return false;
        }

        var key = new CounterKey(counter.Provider, counter.Name);
        if (!_previousSourceTicks.ContainsKey(key) && _previousSourceTicks.Count >= _limits.DistinctKeys)
        {
            status = DurableCounterOfferStatus.TooManyKeys;
            reason = "DistinctKeyLimit";
            return false;
        }

        var intervalState = ClassifyInterval(counter.IntervalSec);
        if (_previousSourceTicks.TryGetValue(key, out var previous)
            && previous.HasValue
            && observation.SourceTimeTicks.HasValue
            && intervalState == CounterMetadataState.Valid)
        {
            var delta = (double)observation.SourceTimeTicks.Value - previous.Value;
            if (delta < 0)
            {
                gap = CoverageGapState.Unknown;
            }
            else
            {
                var intervalTicks = counter.IntervalSec!.Value * TimeSpan.TicksPerSecond;
                gap = delta > 3d * intervalTicks ? CoverageGapState.Gap : CoverageGapState.NoGap;
            }
        }
        _previousSourceTicks[key] = observation.SourceTimeTicks;

        if (!Enum.IsDefined(counter.Kind))
        {
            reason = "UnknownCounterKind";
            return false;
        }
        if (!double.IsFinite(counter.Value))
        {
            reason = "NonfiniteValue";
            return false;
        }

        return true;
    }

    private OwnedRecord OwnAndEncode(
        long sequence,
        DurableCounterObservation observation,
        CoverageGapState gap)
    {
        var source = observation.Counter;
        var intervalState = ClassifyInterval(source.IntervalSec);
        var scaleState = ClassifyScale(source.DisplayRateTimeScale);
        var record = new DurableCounterRecord(
            sequence,
            Copy(source.Provider),
            Copy(source.Name),
            Copy(source.DisplayName),
            source.Unit is null ? null : Copy(source.Unit),
            source.Value,
            source.Kind,
            intervalState == CounterMetadataState.Nonfinite ? null : source.IntervalSec,
            intervalState,
            source.DisplayRateTimeScale?.Ticks,
            scaleState,
            observation.SourceTimeTicks,
            Copy(observation.Clock.Domain),
            Copy(observation.Clock.Origin),
            gap,
            CounterResetState.Unknown,
            EncodedBytes: 0);

        var buffer = new byte[_limits.RecordEncodedBytes];
        var writerBuffer = new FixedBufferWriter(buffer);
        try
        {
            using (var writer = new Utf8JsonWriter(writerBuffer))
            {
                JsonSerializer.Serialize(writer, record);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new DurableCounterPipelineException("RecordEncodedBytes", "Bounded encoding exceeded its buffer.");
        }

        var encodedBytes = writerBuffer.WrittenCount;
        var requested = observation.RequestedEncodedBytes;
        if (requested.HasValue)
        {
            if (requested.Value < encodedBytes || requested.Value > buffer.Length)
            {
                throw new DurableCounterPipelineException("RecordEncodedBytes", "Requested encoded size is invalid.");
            }
            buffer.AsSpan(encodedBytes, requested.Value - encodedBytes).Fill((byte)' ');
            encodedBytes = requested.Value;
        }

        return new OwnedRecord(record with { EncodedBytes = encodedBytes }, buffer);
    }

    private void ObserveOwnedPeak()
    {
        _peakOwnedRecords = Math.Max(_peakOwnedRecords, _ownedRecords);
        _peakOwnedBytes = Math.Max(_peakOwnedBytes, _ownedBytes);
    }

    private void SetCleanTerminalState()
        => _state = _admissionCancelled
            ? DurableCounterPipelineState.Cancelled
            : DurableCounterPipelineState.Completed;

    private bool TryReserveOwnedRecord()
    {
        if (_ownedRecords >= _limits.OwnedRecords
            || _ownedBytes + _limits.RecordEncodedBytes > _limits.OwnedBufferBytes
            || !_globalBudget.TryReserveRecord(_limits.RecordEncodedBytes))
        {
            return false;
        }
        _ownedRecords++;
        _ownedBytes += _limits.RecordEncodedBytes;
        return true;
    }

    private void ReleaseOwnedRecord()
    {
        _ownedRecords--;
        _ownedBytes -= _limits.RecordEncodedBytes;
        _globalBudget.ReleaseRecord(_limits.RecordEncodedBytes);
    }

    private static CounterMetadataState ClassifyInterval(double? interval)
        => interval switch
        {
            null => CounterMetadataState.Missing,
            { } value when !double.IsFinite(value) => CounterMetadataState.Nonfinite,
            <= 0 => CounterMetadataState.Nonpositive,
            _ => CounterMetadataState.Valid,
        };

    private static CounterMetadataState ClassifyScale(TimeSpan? scale)
        => scale switch
        {
            null => CounterMetadataState.Missing,
            { } value when value <= TimeSpan.Zero => CounterMetadataState.Nonpositive,
            _ => CounterMetadataState.Valid,
        };

    private static bool IsBoundedUtf8(string value, int maximumBytes)
        => Encoding.UTF8.GetByteCount(value) <= maximumBytes;

    private static string Copy(string value) => new(value.AsSpan());

    private static async Task<bool> WaitWithTimeoutAsync(Task task, TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        return await Task.WhenAny(task, timeoutTask).ConfigureAwait(false) == task;
    }

    private sealed record CounterKey(string Provider, string Name);

    private sealed class OwnedRecord : IDisposable
    {
        private byte[]? _buffer;

        internal OwnedRecord(DurableCounterRecord record, byte[] buffer)
        {
            Record = record;
            _buffer = buffer;
        }

        internal DurableCounterRecord Record { get; }

        internal DurableCounterSinkRecord AsSinkRecord()
            => new(
                Record,
                (_buffer ?? throw new ObjectDisposedException(nameof(OwnedRecord)))
                    .AsMemory(0, Record.EncodedBytes));

        public void Dispose() => _buffer = null;
    }

    private sealed class FixedBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer;

        internal FixedBufferWriter(byte[] buffer) => _buffer = buffer;

        internal int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            if (count < 0 || WrittenCount + count > _buffer.Length)
            {
                throw new InvalidOperationException("The fixed encoding buffer was exceeded.");
            }
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsSpan(WrittenCount);
        }

        private void EnsureAvailable(int sizeHint)
        {
            if (sizeHint < 0 || sizeHint > _buffer.Length - WrittenCount)
            {
                throw new InvalidOperationException("The fixed encoding buffer was exceeded.");
            }
        }
    }
}

internal sealed record DurableCounterSummaryRow(
    string Provider,
    string Name,
    CounterKind Kind,
    string? Unit,
    int RetainedCount,
    double First,
    double Last,
    double Min,
    double Max,
    long? FirstSourceTimeTicks,
    long? LastSourceTimeTicks,
    int GapCount,
    int UnknownCoverageCount);

internal sealed record DurableCounterSeriesPage(
    IReadOnlyList<DurableCounterRecord> Rows,
    long? NextAfterSequence,
    bool LimitExceeded);

internal sealed record DurableCounterQualityReport(
    DurableCounterAccounting Accounting,
    bool UnknownCommitOutcome,
    string ResetState,
    string SourceCoverageMeaning);

internal sealed class DurableCounterQuery
{
    private readonly IReadOnlyList<DurableCounterRecord> _records;
    private readonly DurableCounterPipelineLimits _limits;

    internal DurableCounterQuery(
        IReadOnlyList<DurableCounterRecord> records,
        DurableCounterPipelineLimits limits)
    {
        _records = records;
        _limits = limits;
    }

    internal IReadOnlyList<DurableCounterSummaryRow> Summary()
    {
        var result = _records
            .GroupBy(static record => (record.Provider, record.Name))
            .Select(static group =>
            {
                var rows = group.OrderBy(static record => record.Sequence).ToArray();
                return new DurableCounterSummaryRow(
                    group.Key.Provider,
                    group.Key.Name,
                    rows[0].Kind,
                    rows[0].Unit,
                    rows.Length,
                    rows[0].Value,
                    rows[^1].Value,
                    rows.Min(static record => record.Value),
                    rows.Max(static record => record.Value),
                    rows.FirstOrDefault(static record => record.SourceTimeTicks.HasValue)?.SourceTimeTicks,
                    rows.LastOrDefault(static record => record.SourceTimeTicks.HasValue)?.SourceTimeTicks,
                    rows.Count(static record => record.CoverageGap == CoverageGapState.Gap),
                    rows.Count(static record => record.CoverageGap == CoverageGapState.Unknown));
            })
            .OrderBy(static row => row.Provider, StringComparer.Ordinal)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .Take(_limits.DistinctKeys)
            .ToArray();
        EnsureResultBound(result);
        return result;
    }

    internal DurableCounterSeriesPage Series(
        string provider,
        string name,
        long? afterSequence,
        int pageSize)
    {
        if (afterSequence < 0)
        {
            throw new DurableCounterPipelineException("InvalidCursor", "The sequence cursor cannot be negative.");
        }
        if (pageSize is < 1 or > 100 || pageSize > _limits.PageRows)
        {
            throw new DurableCounterPipelineException("InvalidPageSize", "The requested page size is outside the limit.");
        }

        var matching = _records
            .Where(record => string.Equals(record.Provider, provider, StringComparison.Ordinal)
                && string.Equals(record.Name, name, StringComparison.Ordinal)
                && (!afterSequence.HasValue || record.Sequence > afterSequence.Value))
            .OrderBy(static record => record.Sequence)
            .Take(pageSize + 1)
            .ToArray();
        var hasMore = matching.Length > pageSize;
        var rows = matching.Take(pageSize).ToArray();
        var page = new DurableCounterSeriesPage(
            rows,
            hasMore && rows.Length > 0 ? rows[^1].Sequence : null,
            LimitExceeded: false);
        EnsureResultBound(page);
        return page;
    }

    internal DurableCounterQualityReport Quality(DurableCounterAccounting accounting)
    {
        var result = new DurableCounterQualityReport(
            accounting,
            accounting.UnknownCommitOutcome > 0,
            "unknown",
            "coverageGap is observed source timing only; unknown is not inferred loss");
        EnsureResultBound(result);
        return result;
    }

    private void EnsureResultBound<T>(T result)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > _limits.ResultBytes)
        {
            throw new DurableCounterPipelineException("ResultLimitExceeded", "The typed query result exceeded its byte limit.");
        }
    }
}

internal sealed class RecordingCounterSink : IDurableCounterSink
{
    private readonly object _gate = new();
    private readonly Func<int, ValueTask<DurableCounterCommitResult>> _commitBehavior;
    private readonly Func<ValueTask> _finalizeBehavior;
    private readonly List<DurableCounterRecord> _committed = new();
    private readonly List<int> _encodedLengths = new();
    private int _commitCalls;

    internal RecordingCounterSink(
        Func<int, ValueTask<DurableCounterCommitResult>>? commitBehavior = null,
        Func<ValueTask>? finalizeBehavior = null)
    {
        _commitBehavior = commitBehavior
            ?? (_ => ValueTask.FromResult(new DurableCounterCommitResult(DurableCounterCommitOutcome.Committed)));
        _finalizeBehavior = finalizeBehavior ?? (() => ValueTask.CompletedTask);
    }

    internal IReadOnlyList<DurableCounterRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _committed.ToArray();
            }
        }
    }

    internal IReadOnlyList<int> EncodedLengths
    {
        get
        {
            lock (_gate)
            {
                return _encodedLengths.ToArray();
            }
        }
    }

    public async ValueTask<DurableCounterCommitResult> CommitAsync(
        IReadOnlyList<DurableCounterSinkRecord> records,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _commitCalls);
        var result = await _commitBehavior(call).ConfigureAwait(false);
        if (result.Outcome == DurableCounterCommitOutcome.Committed)
        {
            lock (_gate)
            {
                _committed.AddRange(records.Select(static record => record.Record));
                _encodedLengths.AddRange(records.Select(static record => record.Encoded.Length));
            }
        }
        return result;
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken) => _finalizeBehavior();
}

internal sealed class ReleaseAction : IDisposable
{
    private Action? _release;

    internal ReleaseAction(Action release) => _release = release;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal static class DurableCounterFixture
{
    internal const string Revision = "dc3-revision-3";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<DurableCounterObservation> GenerateQ1()
        => Enumerable.Range(1, 1_024).Select(Generate).ToArray();

    internal static DurableCounterObservation Generate(int sequence)
    {
        var key = (sequence - 1) % 8;
        return Observation(
            key,
            key % 2 == 0 ? CounterKind.Mean : CounterKind.Sum,
            100 - (sequence % 50),
            checked((sequence - 1L) * TimeSpan.TicksPerMillisecond * 10),
            intervalSec: 1,
            displayScaleTicks: TimeSpan.TicksPerSecond,
            requestedEncodedBytes: 512);
    }

    internal static IReadOnlyList<DurableCounterObservation> LoadQ2(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(repositoryRoot, "docs/design/durable-capture-comparison-protocol.json")));
        var rows = document.RootElement.GetProperty("fixture").GetProperty("q2").GetProperty("records");
        var result = new List<DurableCounterObservation>(16);
        foreach (var row in rows.EnumerateArray())
        {
            var ordinal = row.GetProperty("ordinal").GetInt32();
            var key = row.GetProperty("key").GetInt32();
            var kind = Enum.Parse<CounterKind>(row.GetProperty("kind").GetString()!, ignoreCase: false);
            var value = row.TryGetProperty("sourceNonfiniteValue", out _) ? double.NaN : row.GetProperty("value").GetDouble();
            long? sourceTimeTicks = row.GetProperty("sourceTimeMs").ValueKind == JsonValueKind.Null
                ? null
                : checked(row.GetProperty("sourceTimeMs").GetInt64() * TimeSpan.TicksPerMillisecond);
            double? interval = row.GetProperty("intervalSeconds").ValueKind == JsonValueKind.Null
                ? null
                : row.GetProperty("intervalSeconds").GetDouble();
            long? displayTicks = row.GetProperty("displayScaleTicks").ValueKind == JsonValueKind.Null
                ? null
                : row.GetProperty("displayScaleTicks").GetInt64();
            var encodedBytes = row.TryGetProperty("encodedBytes", out var encoded) ? encoded.GetInt32() : (int?)null;
            var nameBytes = row.TryGetProperty("nameUtf8Bytes", out var nameLength) ? nameLength.GetInt32() : (int?)null;

            result.Add(Observation(
                key,
                kind,
                value,
                sourceTimeTicks,
                interval,
                displayTicks,
                encodedBytes,
                nameBytes,
                ordinal));
        }
        return result;
    }

    internal static string CanonicalInputHash(IReadOnlyList<DurableCounterObservation> observations)
        => HashCanonical(observations.Select(static (observation, index) => new
        {
            Ordinal = index + 1,
            observation.Counter.Provider,
            observation.Counter.Name,
            observation.Counter.DisplayName,
            observation.Counter.Unit,
            Value = double.IsFinite(observation.Counter.Value)
                ? observation.Counter.Value.ToString("R", CultureInfo.InvariantCulture)
                : double.IsNaN(observation.Counter.Value)
                    ? "NaN"
                    : observation.Counter.Value > 0 ? "Infinity" : "-Infinity",
            Kind = observation.Counter.Kind.ToString(),
            observation.Counter.IntervalSec,
            DisplayScaleTicks = observation.Counter.DisplayRateTimeScale?.Ticks,
            observation.SourceTimeTicks,
            observation.Clock.Domain,
            observation.Clock.Origin,
            observation.RequestedEncodedBytes,
        }));

    internal static string CanonicalOracleHash(IReadOnlyList<DurableCounterExpected> expected)
        => HashCanonical(expected);

    private static DurableCounterObservation Observation(
        int key,
        CounterKind kind,
        double value,
        long? sourceTimeTicks,
        double? intervalSec,
        long? displayScaleTicks,
        int? requestedEncodedBytes,
        int? nameUtf8Bytes = null,
        int? ordinal = null)
    {
        var name = nameUtf8Bytes.HasValue
            ? new string('n', nameUtf8Bytes.Value)
            : $"counter-{key}";
        var counter = new CounterValue(
            "Synthetic.Provider",
            name,
            $"Synthetic counter {key}",
            value,
            kind,
            "items")
        {
            IntervalSec = intervalSec,
            DisplayRateTimeScale = displayScaleTicks.HasValue ? TimeSpan.FromTicks(displayScaleTicks.Value) : null,
        };
        return new DurableCounterObservation(
            counter,
            sourceTimeTicks,
            new DurableCounterSourceClock(
                "fixture-relative-100ns",
                ordinal.HasValue ? "dc3-rev3-q2" : "dc3-rev3-generated"),
            requestedEncodedBytes);
    }

    private static string HashCanonical<T>(IEnumerable<T> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            hash.AppendData(bytes);
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal sealed record DurableCounterExpected(
    long Sequence,
    DurableCounterOfferStatus Status,
    string? Reason,
    string? Provider,
    string? Name,
    double? Value,
    CounterKind? Kind,
    long? SourceTimeTicks,
    CoverageGapState? CoverageGap,
    CounterMetadataState? IntervalState,
    CounterMetadataState? DisplayScaleState,
    CounterResetState? ResetState);

internal static class DurableCounterOracle
{
    internal static IReadOnlyList<DurableCounterExpected> Q1()
        => Enumerable.Range(1, 1_024)
            .Select(sequence =>
            {
                var key = (sequence - 1) % 8;
                return new DurableCounterExpected(
                    sequence,
                    DurableCounterOfferStatus.Accepted,
                    null,
                    "Synthetic.Provider",
                    $"counter-{key}",
                    100 - (sequence % 50),
                    key % 2 == 0 ? CounterKind.Mean : CounterKind.Sum,
                    checked((sequence - 1L) * TimeSpan.TicksPerMillisecond * 10),
                    sequence <= 8 ? CoverageGapState.Unknown : CoverageGapState.NoGap,
                    CounterMetadataState.Valid,
                    CounterMetadataState.Valid,
                    CounterResetState.Unknown);
            })
            .ToArray();

    internal static IReadOnlyList<DurableCounterExpected> Q2()
        =>
        [
            Accepted(1, 0, CounterKind.Mean, 100, 0, CoverageGapState.Unknown, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(2, 1, CounterKind.Sum, 10, 0, CoverageGapState.Unknown, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(3, 1, CounterKind.Sum, 5, 1_000, CoverageGapState.NoGap, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(4, 0, CounterKind.Mean, 100, 1_000, CoverageGapState.Unknown, CounterMetadataState.Missing, CounterMetadataState.Valid),
            Accepted(5, 0, CounterKind.Mean, 100, 2_000, CoverageGapState.Unknown, CounterMetadataState.Nonpositive, CounterMetadataState.Valid),
            Accepted(6, 0, CounterKind.Mean, 100, 3_000, CoverageGapState.Unknown, CounterMetadataState.Nonpositive, CounterMetadataState.Valid),
            Accepted(7, 0, CounterKind.Mean, 100, 4_000, CoverageGapState.NoGap, CounterMetadataState.Valid, CounterMetadataState.Missing),
            Accepted(8, 0, CounterKind.Mean, 100, 5_000, CoverageGapState.NoGap, CounterMetadataState.Valid, CounterMetadataState.Nonpositive),
            Accepted(9, 1, CounterKind.Sum, 5, 5_000, CoverageGapState.Gap, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(10, 1, CounterKind.Sum, 5, 4_000, CoverageGapState.Unknown, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(11, 1, CounterKind.Sum, 5, 4_000, CoverageGapState.NoGap, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(12, 1, CounterKind.Sum, 5, null, CoverageGapState.Unknown, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Accepted(13, 0, CounterKind.Mean, 100, 6_000, CoverageGapState.NoGap, CounterMetadataState.Valid, CounterMetadataState.Valid),
            Rejected(14, DurableCounterOfferStatus.RecordTooLarge, "RecordEncodedBytes"),
            Rejected(15, DurableCounterOfferStatus.Invalid, "StringUtf8Limit"),
            Rejected(16, DurableCounterOfferStatus.Invalid, "NonfiniteValue"),
        ];

    private static DurableCounterExpected Accepted(
        long sequence,
        int key,
        CounterKind kind,
        double value,
        long? sourceTimeMilliseconds,
        CoverageGapState gap,
        CounterMetadataState interval,
        CounterMetadataState scale)
        => new(
            sequence,
            DurableCounterOfferStatus.Accepted,
            null,
            "Synthetic.Provider",
            $"counter-{key}",
            value,
            kind,
            sourceTimeMilliseconds * TimeSpan.TicksPerMillisecond,
            gap,
            interval,
            scale,
            CounterResetState.Unknown);

    private static DurableCounterExpected Rejected(
        long sequence,
        DurableCounterOfferStatus status,
        string reason)
        => new(sequence, status, reason, null, null, null, null, null, null, null, null, null);
}
