using System;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Lease metadata for an investigation handle.
/// </summary>
/// <param name="IdleTtl">Per-handle idle TTL requested at attach time.</param>
/// <param name="AttachDeadline">Deadline for the Attaching → Active transition.</param>
/// <param name="LastSuccessfulUseAt">Timestamp of the last successful proxied tool call, if any.</param>
/// <param name="IdleExpiresAt">Current idle-expiry deadline. Refreshed only after successful proxied calls.</param>
/// <param name="AbsoluteExpiresAt">Hard wall-clock cap for the handle lifetime.</param>
public sealed record InvestigationLease(
    TimeSpan IdleTtl,
    DateTimeOffset AttachDeadline,
    DateTimeOffset? LastSuccessfulUseAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt)
{
    /// <summary>
    /// Backward-compatible effective expiry used by summaries and projections.
    /// Because <see cref="IdleExpiresAt"/> is clamped to <see cref="AbsoluteExpiresAt"/>,
    /// this is the deadline the reaper uses once a handle becomes Active.
    /// </summary>
    public DateTimeOffset EffectiveExpiresAt => IdleExpiresAt <= AbsoluteExpiresAt
        ? IdleExpiresAt
        : AbsoluteExpiresAt;
}
