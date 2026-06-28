namespace DotnetDiagnostics.Core.Preflight;

/// <summary>
/// Outcome of a single preflight check. Mirrors the severity ladder used by the rest of the
/// diagnostics surface, but framed for <i>environment readiness</i> rather than workload health.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PreflightStatus>))]
public enum PreflightStatus
{
    /// <summary>The capability is available; nothing to fix.</summary>
    Ok,

    /// <summary>An <b>optional</b> capability is unavailable. Core diagnostics still work; some
    /// tools (off-CPU sampling, native-alloc sampling) will fail until remediated.</summary>
    Degraded,

    /// <summary>A <b>hard</b> blocker. The diagnostic IPC socket is unreachable (UID mismatch) or
    /// the ClrMD-backed attach tools cannot run. Surfaces a non-zero CLI exit code.</summary>
    Blocked,

    /// <summary>The check does not apply to this host / target (for example a Linux-only check on
    /// Windows, or a target-specific check with no <c>processId</c>). Excluded from the overall
    /// verdict.</summary>
    NotApplicable,
}

/// <summary>
/// One remediation-first preflight finding. Unlike the boolean
/// <see cref="Capabilities.DiagnosticCapabilities"/> matrix, every non-OK check carries a
/// copy-pasteable <see cref="Remediation"/> string (docker flag / k8s securityContext / sysctl).
/// </summary>
/// <param name="Id">Stable machine identifier (for example <c>socket-uid</c>, <c>clrmd-attach</c>).</param>
/// <param name="Title">Short human-readable label.</param>
/// <param name="Status">Severity of the finding.</param>
/// <param name="Reason">Why the check landed on this status. Self-contained and English.</param>
/// <param name="Remediation">Copy-pasteable fix when <see cref="Status"/> is Blocked/Degraded; null when Ok/NotApplicable.</param>
/// <param name="AffectedTools">Tools that will fail (or degrade) while this check is not OK.</param>
public sealed record PreflightCheck(
    string Id,
    string Title,
    PreflightStatus Status,
    string Reason,
    string? Remediation = null,
    IReadOnlyList<string>? AffectedTools = null);

/// <summary>
/// Result of <see cref="IPreflightInspector.Inspect"/>. Target-optional: with a <c>processId</c> it
/// also evaluates target-specific readiness (socket UID match); without one it diagnoses only the
/// sidecar host environment.
/// </summary>
/// <param name="ProcessId">Target pid the report was scoped to, or null for host-only diagnosis.</param>
/// <param name="Os">Operating system family the diagnostics host is running on.</param>
/// <param name="Overall">Worst non-<see cref="PreflightStatus.NotApplicable"/> status across <see cref="Checks"/>.</param>
/// <param name="Checks">Ordered findings (most severe surfaced first by the caller).</param>
public sealed record PreflightReport(
    int? ProcessId,
    string Os,
    PreflightStatus Overall,
    IReadOnlyList<PreflightCheck> Checks)
{
    /// <summary>True when at least one check is <see cref="PreflightStatus.Blocked"/>. Drives the
    /// non-zero CLI <c>doctor</c> exit code for CI gating.</summary>
    public bool HasBlocker => Overall == PreflightStatus.Blocked;
}
