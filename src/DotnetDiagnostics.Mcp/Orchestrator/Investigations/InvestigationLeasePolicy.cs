using System;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Shared lease math for every investigation transport.
/// </summary>
internal static class InvestigationLeasePolicy
{
    public static InvestigationLease Create(
        DateTimeOffset attachedAt,
        TimeSpan attachTimeout,
        TimeSpan idleTtl,
        TimeSpan absoluteTtl)
    {
        var absoluteExpiresAt = attachedAt + absoluteTtl;
        return new InvestigationLease(
            IdleTtl: idleTtl,
            AttachDeadline: attachedAt + attachTimeout,
            LastSuccessfulUseAt: null,
            IdleExpiresAt: ClampIdleExpiry(attachedAt, idleTtl, absoluteExpiresAt),
            AbsoluteExpiresAt: absoluteExpiresAt);
    }

    public static InvestigationLease FromLegacyExpiry(DateTimeOffset attachedAt, DateTimeOffset expiresAt)
    {
        var idleTtl = expiresAt > attachedAt
            ? expiresAt - attachedAt
            : TimeSpan.Zero;
        var absoluteExpiresAt = expiresAt > attachedAt.AddHours(8)
            ? expiresAt
            : attachedAt.AddHours(8);

        return new InvestigationLease(
            IdleTtl: idleTtl,
            AttachDeadline: expiresAt,
            LastSuccessfulUseAt: null,
            IdleExpiresAt: expiresAt,
            AbsoluteExpiresAt: absoluteExpiresAt);
    }

    public static InvestigationLease RecordSuccessfulUse(InvestigationLease lease, DateTimeOffset successfulUseAt)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return lease with
        {
            LastSuccessfulUseAt = successfulUseAt,
            IdleExpiresAt = ClampIdleExpiry(successfulUseAt, lease.IdleTtl, lease.AbsoluteExpiresAt),
        };
    }

    public static DateTimeOffset GetExpirationDeadline(InvestigationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return handle.State == InvestigationState.Attaching
            ? handle.AttachDeadline
            : handle.ExpiresAt;
    }

    public static bool IsExpired(InvestigationHandle handle, DateTimeOffset now)
        => GetExpirationDeadline(handle) <= now;

    public static string BuildExpirationReason(InvestigationHandle handle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.State == InvestigationState.Attaching)
        {
            return $"Attach deadline expired at {handle.AttachDeadline:O} (reaper observed at {now:O}).";
        }

        if (handle.AbsoluteExpiresAt <= now)
        {
            return $"Absolute lease expired at {handle.AbsoluteExpiresAt:O} (reaper observed at {now:O}).";
        }

        return $"Idle lease expired at {handle.IdleExpiresAt:O} (last successful use {handle.LastSuccessfulUseAt?.ToString("O") ?? "never"}; reaper observed at {now:O}).";
    }

    private static DateTimeOffset ClampIdleExpiry(DateTimeOffset baseTime, TimeSpan idleTtl, DateTimeOffset absoluteExpiresAt)
    {
        var idleExpiresAt = baseTime + idleTtl;
        return idleExpiresAt <= absoluteExpiresAt
            ? idleExpiresAt
            : absoluteExpiresAt;
    }
}
