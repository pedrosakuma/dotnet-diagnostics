using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DotnetDiagnostics.Core.Requests;

namespace DotnetDiagnostics.Cli;

internal sealed record InvestigationEvidenceCapability(
    int Version,
    IReadOnlyList<string> Kinds,
    int MaxItems,
    int MaxItemBytes,
    int MaxTotalBytes);

internal sealed record InvestigationEvidenceTarget(
    int ProcessId,
    DateTimeOffset? ProcessStartedAt,
    string IdentityStatus);

internal sealed record InvestigationEvidenceClock(
    string Kind,
    string InstanceId,
    string Comparison);

internal sealed record InvestigationEvidenceWindow(
    string Kind,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    InvestigationEvidenceClock Clock,
    string Completion);

internal sealed record InvestigationEvidenceConnection(
    string InstanceId,
    string StoreId);

internal sealed record InvestigationEvidenceProjection(
    string Owner,
    string Mode);

internal sealed record InvestigationEvidenceDrilldown(
    string Owner,
    string ConnectionId,
    string StoreId,
    string? Handle,
    string Status,
    string? UnavailableReason,
    DateTimeOffset? ExpiresNoLaterThan,
    IReadOnlyList<string> InvalidatedBy);

internal sealed record InvestigationEvidenceRetention(
    InvestigationEvidenceConnection Connection,
    InvestigationEvidenceProjection Projection,
    InvestigationEvidenceDrilldown Drilldown);

internal sealed record InvestigationEvidenceBackend(
    string Kind,
    string Source);

internal sealed record InvestigationEvidenceQuality(
    string Status,
    string Source,
    IReadOnlyList<string> Limitations);

internal sealed record InvestigationEvidenceSafety(
    string Risk,
    string Authorization);

internal sealed record InvestigationEvidenceDescriptor(
    int Version,
    string EvidenceId,
    string Kind,
    string CollectorKind,
    InvestigationEvidenceTarget Target,
    InvestigationEvidenceWindow Window,
    InvestigationEvidenceRetention Retention,
    InvestigationEvidenceBackend Backend,
    InvestigationEvidenceQuality Quality,
    InvestigationEvidenceSafety Safety,
    bool MayContainSensitiveData,
    IReadOnlyList<string> Limitations,
    string Availability,
    string? UnavailableReason);

internal sealed class CliInvestigationEvidence
{
    internal const int Version = 1;
    internal const int MaxItems = 7;
    internal const int MaxItemBytes = 1024 * 1024;
    internal const int MaxTotalBytes = 4 * 1024 * 1024;

    internal static readonly IReadOnlyList<string> Kinds =
        ["cpu", "allocation", "off_cpu", "contention", "threadpool", "gc", "requests"];

    internal InvestigationEvidenceCapability Capability { get; } =
        new(Version, Kinds, MaxItems, MaxItemBytes, MaxTotalBytes);

    internal string ConnectionId { get; } = "cli-" + Guid.NewGuid().ToString("N");
    internal string StoreId { get; } = "store-" + Guid.NewGuid().ToString("N");

    internal static DateTimeOffset? ReadProcessStart(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    internal InvestigationEvidenceDescriptor? Create(
        string collectorKind,
        int processId,
        DateTimeOffset? processStartedAtBefore,
        DateTimeOffset? processStartedAtAfter,
        DateTimeOffset? windowStart,
        DateTimeOffset? windowEnd,
        string windowKind,
        string completion,
        string backend,
        string qualitySource,
        IReadOnlyList<string> qualityLimitations,
        string? handle,
        DateTimeOffset? handleExpiresAt)
    {
        var normalizedKind = NormalizeKind(collectorKind);
        if (normalizedKind is null)
        {
            return null;
        }

        var identityStatus = processStartedAtBefore is null || processStartedAtAfter is null
            ? "unknown"
            : processStartedAtBefore == processStartedAtAfter ? "verified" : "changed";
        var processStartedAt = processStartedAtBefore == processStartedAtAfter
            ? processStartedAtBefore
            : processStartedAtAfter ?? processStartedAtBefore;
        var qualityStatus = qualitySource == "not-reported" ? "unknown" : "reported";
        var hasHandle = !string.IsNullOrWhiteSpace(handle);
        var invalidatedBy = hasHandle
            ? new[] { "expired", "capacity-evicted", "owner-replaced", "invalidated", "connection-closed", "process-exited" }
            : Array.Empty<string>();
        var limitations = new[]
        {
            "Independent captures use reported wall-clock comparison only; temporal relation never establishes causality.",
            "Handle expiry is an upper bound. Capacity eviction, replacement, invalidation, process exit, or connection close can end drilldown earlier.",
            "The extension owns only its bounded response copy; CLI handles remain borrowed from this connection and store.",
        };

        return new(
            Version,
            Guid.NewGuid().ToString("N"),
            normalizedKind,
            collectorKind,
            new(processId, processStartedAt, identityStatus),
            new(windowKind, windowStart, windowEnd,
                new("diagnostic-host-utc", ConnectionId, "reported-only"), completion),
            new(
                new(ConnectionId, StoreId),
                new("extension-context", "response-copy"),
                new(
                    hasHandle ? "cli-connection" : "none",
                    ConnectionId,
                    StoreId,
                    handle,
                    hasHandle ? "available" : "unavailable",
                    hasHandle ? null : "not-retained",
                    handleExpiresAt,
                    invalidatedBy)),
            new(backend, collectorKind),
            new(qualityStatus, qualitySource, qualityLimitations),
            new(collectorKind == "off_cpu" ? "high" : "moderate",
                collectorKind == "off_cpu" ? "explicit-acknowledgement" : "standard-preflight"),
            normalizedKind != "gc",
            limitations,
            identityStatus == "changed" ? "unavailable" : "available",
            identityStatus == "changed" ? "target-changed" : null);
    }

    internal InvestigationEvidenceDescriptor CreateRequestActivities(
        RequestActivityCapture capture,
        DateTimeOffset? processStartedAtAfter,
        string handle,
        DateTimeOffset expiresAt)
    {
        var limitations = capture.Notes.Concat(
        [
            $"Completion={capture.Quality.Completion}; eventsLost={capture.Quality.EventsLost?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; "
                + $"parseErrors={capture.Quality.ParseErrors}; droppedRequests={capture.Quality.DroppedRequests}; "
                + $"droppedActivities={capture.Quality.DroppedActivities}.",
        ]).ToArray();
        var descriptor = Create(
            "request-activities",
            capture.ProcessId,
            capture.ProcessStartedAt,
            processStartedAtAfter,
            capture.ObservationStart ?? capture.RequestedStart,
            capture.ObservationEnd,
            capture.ObservationStart is null ? "requested" : "observed",
            NormalizeCompletion(capture.Quality.Completion),
            "eventpipe",
            "request-activities",
            limitations,
            handle,
            expiresAt);
        return descriptor ?? throw new InvalidOperationException("Request activities must be eligible investigation evidence.");
    }

    internal static string NormalizeCompletion(string? completion) => completion switch
    {
        "normal" => "normal",
        "early" => "early",
        "cancelled" => "cancelled",
        "failed" => "failed",
        _ => "unknown",
    };

    private static string? NormalizeKind(string collectorKind) => collectorKind switch
    {
        "cpu" => "cpu",
        "allocation" => "allocation",
        "off_cpu" => "off_cpu",
        "contention" => "contention",
        "threadpool" => "threadpool",
        "gc" => "gc",
        "requests" or "request-activities" => "requests",
        _ => null,
    };
}
