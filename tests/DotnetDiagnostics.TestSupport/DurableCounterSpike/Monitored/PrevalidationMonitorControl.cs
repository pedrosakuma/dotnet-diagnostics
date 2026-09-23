using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record PrevalidationMonitorRequest(string Operation, MonitoredProcessIdentity? Process = null,
    string? Boundary = null, bool Active = false);

internal sealed record PrevalidationMonitorReply(bool Incomplete, string? Alarm, int Records, long Bytes,
    int MaximumIdentities, long MaximumBytes, MonitoredSweepSummary? Summary);

internal static class PrevalidationMonitorControl
{
    private static readonly JsonSerializerOptions WireJson = new(PrevalidationProtocol.Json) { WriteIndented = false };
    internal const int MaximumFrames = 256;
    internal const int MaximumFrameBytes = 2_048;
    internal const int MaximumPeriodicRecords = 120_000 / 100 + 1;
    internal const int MaximumBoundaryRecords = 64;
    internal const int FullWindowRecords = MaximumPeriodicRecords + MaximumBoundaryRecords;
    internal const int ReservedHandshakeBytes = 256 + 64 * 128;
    internal const int ReservedControlBytes = 2 * MaximumFrames * MaximumFrameBytes + ReservedHandshakeBytes;
    internal const int ReservedSummaryBytes = 2_048 * 1_024;
    internal const int FullWindowOutputBytes = FullWindowRecords * 1_024
        + ReservedControlBytes + 64 * (8_192 + 1);

    internal static string Encode<T>(T frame)
    {
        var line = JsonSerializer.Serialize(frame, WireJson);
        PrevalidationProtocol.Require(Encoding.UTF8.GetByteCount(line) + 1 <= MaximumFrameBytes,
            "PrevalidationControlFrameWidth");
        return line;
    }

    internal static async Task<string> ReadLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) == 1)
        {
            if (buffer[0] == '\n')
            {
                var text = line.ToString();
                PrevalidationProtocol.Require(Encoding.UTF8.GetByteCount(text) + 1 <= MaximumFrameBytes,
                    "PrevalidationControlFrameWidth");
                return text;
            }
            PrevalidationProtocol.Require(line.Length < MaximumFrameBytes - 1, "PrevalidationControlFrameWidth");
            line.Append(buffer[0]);
        }
        throw PrevalidationProtocol.Error("PrevalidationControlClosed", "The authoritative monitor channel closed.");
    }

    internal static async Task<PrevalidationMonitorReply> DispatchAsync(PrevalidationMonitorRequest request,
        MonitoredStorageMonitor monitor, string ownershipPath, CancellationToken cancellationToken,
        PrevalidationOwnedProcessLedger? ownership = null)
    {
        MonitoredSweepSummary? summary = null;
        switch (request.Operation)
        {
            case "register" when request.Process is { Role: not MonitoredProcessRole.Harness } identity:
                // One writer owns both journal and registry; acknowledgement follows both.
                if (ownership is null) PrevalidationOwnership.Append(ownershipPath, identity);
                else ownership.Register(ownershipPath, identity);
                monitor.AddProcess(identity);
                break;
            case "stage" when request.Boundary is not null:
                monitor.SetStage(request.Boundary, request.Active);
                break;
            case "boundary" when request.Boundary is not null:
                var observation = await monitor.ObserveBoundaryAsync(request.Boundary, request.Active,
                    cancellationToken).ConfigureAwait(false);
                if (request.Boundary == "geometry-539-rooted-32-descriptor-only")
                {
                    var path = Path.Combine(Path.GetDirectoryName(ownershipPath)!, "geometry-descriptor-proof.json");
                    PrevalidationProtocol.EnsureImmutable(path);
                    var proof = PrevalidationProtocol.Read<PrevalidationDescriptorProof>(path, 65_536);
                    var owner = PrevalidationOwnership.Read(ownershipPath)
                        .Single(identity => identity.Role == MonitoredProcessRole.Harness);
                    PrevalidationGeometry.ValidateObservation(proof, observation, owner);
                }
                summary = observation.Summary;
                break;
            case "terminating" when request.Process is not null:
                await monitor.PrepareTerminationAsync(request.Process, cancellationToken).ConfigureAwait(false);
                break;
            case "terminated" when request.Process is not null:
                await monitor.ConfirmTerminatedAsync(request.Process, cancellationToken).ConfigureAwait(false);
                break;
            case "kill" when request.Process is not null:
                await monitor.KillOwnedAsync(request.Process, cancellationToken).ConfigureAwait(false);
                break;
            case "status":
                break;
            default:
                throw PrevalidationProtocol.Error("PrevalidationControlOperation", "Unknown monitor control request.");
        }
        return Snapshot(monitor, summary);
    }

    internal static PrevalidationMonitorReply Snapshot(MonitoredStorageMonitor monitor,
        MonitoredSweepSummary? summary = null)
        => new(monitor.IsIncomplete, monitor.TerminalAlarm, monitor.SummaryRecords, monitor.SummaryBytes,
            monitor.MaximumCurrentContextIdentities, monitor.MaximumObservedSweepBytes, summary);
}

internal sealed class PrevalidationMonitorClient(CancellationToken cancellationToken)
{
    private int _requests;
    internal PrevalidationMonitorReply State { get; private set; } = new(false, null, 0, 0, 0, 0, null);

    internal PrevalidationMonitorReply Send(PrevalidationMonitorRequest request)
    {
        PrevalidationProtocol.Require(++_requests <= PrevalidationMonitorControl.MaximumFrames,
            "PrevalidationControlRecordLimit");
        Console.Out.WriteLine(PrevalidationMonitorControl.Encode(request));
        Console.Out.Flush();
        var line = PrevalidationMonitorControl.ReadLineAsync(Console.In, cancellationToken).GetAwaiter().GetResult();
        State = JsonSerializer.Deserialize<PrevalidationMonitorReply>(line, PrevalidationProtocol.Json)
            ?? throw PrevalidationProtocol.Error("PrevalidationControlReply", "Missing authoritative monitor reply.");
        PrevalidationProtocol.Require(!State.Incomplete && State.Alarm is null,
            State.Alarm ?? "PrevalidationMonitoringIncomplete");
        return State;
    }
}

// The scored runner keeps its local monitor. Prevalidation delegates observation,
// not execution, to the single coordinator-owned monitor for the entire entry.
internal sealed class MonitoredExecutionMonitor : IAsyncDisposable
{
    private readonly MonitoredStorageMonitor? _local;
    private readonly PrevalidationMonitorClient? _remote;
    private readonly int _initialRecords;
    private readonly long _initialBytes;
    private readonly TaskCompletionSource<string> _unusedTerminal = new();

    internal MonitoredExecutionMonitor(MonitoredExecutionContext context, string path, BoundedOutputBudget budget)
    {
        _remote = context.Prevalidation?.Monitor;
        if (_remote is null)
        {
            PrevalidationProtocol.Require(context.Prevalidation is null, "PrevalidationMonitorOwnerMissing");
            _local = new(context.Attribution, context.Encoding, path, budget,
                context.ComponentEvidence.DerivedMaximumSimultaneousIdentitiesEnforced);
        }
        else
        {
            var initial = _remote.State;
            _initialRecords = initial.Records;
            _initialBytes = initial.Bytes;
        }
    }

    internal bool IsIncomplete => _local?.IsIncomplete ?? _remote!.State.Incomplete;
    internal string? TerminalAlarm => _local is not null ? _local.TerminalAlarm : _remote!.State.Alarm;
    internal int SummaryRecords => _local?.SummaryRecords ?? _remote!.State.Records - _initialRecords;
    internal long SummaryBytes => _local?.SummaryBytes ?? _remote!.State.Bytes - _initialBytes;
    internal int MaximumCurrentContextIdentities => _local?.MaximumCurrentContextIdentities
        ?? _remote!.State.MaximumIdentities;
    internal long MaximumObservedSweepBytes => _local?.MaximumObservedSweepBytes ?? _remote!.State.MaximumBytes;
    internal Task<string> TerminalIssue => _local?.TerminalIssue ?? _unusedTerminal.Task;

    internal void AddProcess(MonitoredProcessIdentity identity)
    {
        if (_local is not null) _local.AddProcess(identity);
        else _remote!.Send(new("register", identity));
    }

    internal void MarkIntentionalTermination(MonitoredProcessIdentity identity)
    {
        if (_local is not null) _local.MarkIntentionalTermination(identity);
        else _remote!.Send(new("terminating", identity));
    }

    internal void ConfirmTerminatedAndRemove(MonitoredProcessIdentity identity)
    {
        if (_local is not null) _local.ConfirmTerminatedAndRemove(identity);
        else _remote!.Send(new("terminated", identity));
    }

    internal void SetStage(string boundary, bool activeStorageStage)
    {
        if (_local is not null) _local.SetStage(boundary, activeStorageStage);
        else _remote!.Send(new("stage", Boundary: boundary, Active: activeStorageStage));
    }

    internal Task StartAsync() => _local?.StartAsync() ?? Task.CompletedTask;
    internal Task StopAsync() => _local?.StopAsync() ?? Task.CompletedTask;

    internal async Task KillOwnedAsync(System.Diagnostics.Process process, MonitoredProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        if (_local is not null)
        {
            await _local.StopAsync().ConfigureAwait(false);
            _local.MarkIntentionalTermination(identity);
            OwnedProcessTerminator.KillExact(process, identity);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _local.ConfirmTerminatedAndRemove(identity);
        }
        else
        {
            _remote!.Send(new("kill", identity));
        }
    }

    internal Task<MonitoredSweepResult> ObserveBoundaryAsync(string boundary, bool activeStorageStage,
        CancellationToken cancellationToken)
    {
        if (_local is not null) return _local.ObserveBoundaryAsync(boundary, activeStorageStage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var reply = _remote!.Send(new("boundary", Boundary: boundary, Active: activeStorageStage));
        return Task.FromResult(new MonitoredSweepResult(reply.Summary
            ?? throw PrevalidationProtocol.Error("PrevalidationBoundaryMissing", "Missing boundary reply."), [], []));
    }

    public ValueTask DisposeAsync() => _local?.DisposeAsync() ?? ValueTask.CompletedTask;
}
