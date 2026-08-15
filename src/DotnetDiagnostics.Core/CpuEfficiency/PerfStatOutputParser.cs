using System.Globalization;

namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Parses <c>perf stat -x, -e &lt;events&gt; -p &lt;pid&gt; -- sleep N</c> CSV output (field separator
/// <c>,</c>, one line per requested event) into a metric-name → value map.
/// </summary>
/// <remarks>
/// <para>
/// <c>perf stat</c>'s default human-readable summary (right-aligned columns, "# n.nn insn per cycle"
/// annotations) is fragile to parse across perf versions/locales. <c>-x,</c> switches to a stable,
/// machine-readable CSV form documented by <c>man perf-stat</c>:
/// <c>counter-value,unit,event-name,run-time-ns,percentage-of-measurement-time[,metric-value,metric-unit]</c>.
/// We only depend on the first three fields (value, unit, event name), which keeps this parser
/// resilient to perf adding/removing trailing metric columns across versions.
/// </para>
/// <para>
/// Graceful degradation (issue #828): when the host's PMU is not exposed to the guest (common on
/// cloud VMs / CI runners, including virtualized GitHub Actions Linux runners), <c>perf stat</c>
/// does not fail the whole invocation — it reports <c>&lt;not supported&gt;</c> (event doesn't exist
/// on this CPU) or <c>&lt;not counted&gt;</c> (event exists but couldn't be scheduled/read) in the
/// value field for the affected event(s). Both are surfaced as a null value plus a
/// note-style entry (see <see cref="PerfStatParseResult.UnavailableEvents"/>), never as a thrown exception.
/// </para>
/// </remarks>
internal static class PerfStatOutputParser
{
    private const string NotSupportedToken = "<not supported>";
    private const string NotCountedToken = "<not counted>";

    public static PerfStatParseResult Parse(string csvOutput)
    {
        ArgumentNullException.ThrowIfNull(csvOutput);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        var unavailable = new List<string>();

        foreach (var rawLine in csvOutput.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 3)
            {
                // Not an event line (e.g. blank separator, "seconds time elapsed" summary — perf
                // only emits that trailer without -x on some versions; skip anything we don't
                // recognize rather than throwing on an unexpected format).
                continue;
            }

            var rawValue = fields[0].Trim();
            var eventName = fields[2].Trim();
            if (eventName.Length == 0)
            {
                continue;
            }

            if (string.Equals(rawValue, NotSupportedToken, StringComparison.Ordinal))
            {
                unavailable.Add($"{eventName}: not supported by this CPU/host (no vPMU exposed to the guest, or the kernel doesn't expose this generic event alias).");
                continue;
            }

            if (string.Equals(rawValue, NotCountedToken, StringComparison.Ordinal))
            {
                unavailable.Add($"{eventName}: not counted (too many simultaneous PMU events requested for the available hardware counter slots, or the event could not be scheduled during the window).");
                continue;
            }

            // perf -x, values are plain integers (no thousands separators — those only appear in
            // the human-readable default output), but tolerate a defensive strip just in case.
            var cleaned = rawValue.Replace(",", string.Empty, StringComparison.Ordinal);
            if (long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                values[eventName] = parsed;
            }
            else
            {
                unavailable.Add($"{eventName}: could not parse perf stat value '{rawValue}'.");
            }
        }

        return new PerfStatParseResult(values, unavailable);
    }
}

/// <summary>Result of parsing one <c>perf stat -x,</c> invocation.</summary>
/// <param name="Values">Successfully parsed event name → counter value.</param>
/// <param name="UnavailableEvents">Human-readable notes for events that were requested but came back
/// <c>&lt;not supported&gt;</c>, <c>&lt;not counted&gt;</c>, or otherwise unparsable.</param>
internal sealed record PerfStatParseResult(
    IReadOnlyDictionary<string, long> Values,
    IReadOnlyList<string> UnavailableEvents);
