using System.Globalization;
using DotnetDiagnostics.Core.Capabilities;

namespace DotnetDiagnostics.Core.Preflight;

/// <summary>
/// Diagnoses whether the diagnostics host (sidecar) is configured to attach to and collect from
/// .NET targets. Target-optional: pass a <c>processId</c> to also validate target-specific
/// readiness (diagnostic-socket UID match), or omit it for a host-only environment check.
/// </summary>
public interface IPreflightInspector
{
    /// <summary>
    /// Runs all preflight checks. Cheap (a handful of <c>/proc</c> reads on Linux, OS checks
    /// elsewhere) and safe to call repeatedly; never throws.
    /// </summary>
    /// <param name="processId">Target pid to scope the socket-UID check to, or null for host-only.</param>
    PreflightReport Inspect(int? processId);
}

/// <summary>
/// Default <see cref="IPreflightInspector"/>. Gathers the real host probes
/// (<see cref="PtraceProbe"/>, <see cref="PerfHostProbe"/>) and the self/target UIDs, then defers
/// the verdict assembly to the pure <see cref="PreflightChecks.Build"/>.
/// </summary>
public sealed class PreflightInspector : IPreflightInspector
{
    /// <inheritdoc />
    public PreflightReport Inspect(int? processId)
    {
        var ptrace = PtraceProbe.Detect();
        var perf = PerfHostProbe.Detect();

        int? selfUid = null;
        int? targetUid = null;
        if (OperatingSystem.IsLinux())
        {
            selfUid = TryReadUid("/proc/self/status");
            if (processId is int pid)
            {
                targetUid = TryReadUid($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/status");
            }
        }

        return PreflightChecks.Build(
            processId,
            isLinux: OperatingSystem.IsLinux(),
            isWindows: OperatingSystem.IsWindows(),
            isMacOs: OperatingSystem.IsMacOS(),
            ptrace,
            perf,
            selfUid,
            targetUid);
    }

    /// <summary>
    /// Reads the real UID (first column of the <c>Uid:</c> line) from a <c>/proc/*/status</c>
    /// file. This is the same column <see cref="DotnetDiagnostics.Core.Threads"/>'s native thread
    /// inspector uses for its attach decision, so the preflight verdict matches the tool it predicts
    /// for. Best-effort: returns null when the file is unreadable or the target has exited.
    /// </summary>
    private static int? TryReadUid(string statusPath)
    {
        try
        {
            foreach (var line in File.ReadLines(statusPath))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                var tokens = line["Uid:".Length..]
                    .Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                {
                    return null;
                }

                if (int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
                {
                    return uid;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }
}
