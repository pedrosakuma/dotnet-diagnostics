using System.Globalization;

namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Parses textual <c>perf script</c> output of a sched event capture into pairs of
/// (off-CPU span, blocking stack). Each sched_switch event is both an OUT for the
/// previous task and an IN for the next; we maintain a per-TID pending-out map and
/// close it on the next IN matching the same TID. The blocking stack belongs to the
/// task going OUT, exactly what off-CPU profiling wants.
/// </summary>
/// <remarks>
/// Supports both <c>perf script</c> trace formats seen in the wild:
/// <list type="bullet">
///   <item>Long form: <c>prev_comm=X prev_pid=N prev_prio=P prev_state=S ==&gt; next_comm=Y next_pid=M next_prio=Q</c>.</item>
///   <item>Compact form (default on recent perf): <c>X:N [P] S ==&gt; Y:M [Q]</c>.</item>
/// </list>
/// Trailing state flags like <c>S+</c> (preempted while sleeping) collapse to their first letter.
/// Events whose TID is not in <c>targetTids</c> are still consumed (they may close a pending out for
/// a non-target task) but never emit spans.
/// </remarks>
internal static class PerfSchedScriptParser
{
    internal sealed record SchedEvent(double TimestampSeconds, string PrevComm, int PrevTid, string PrevState,
        string NextComm, int NextTid, List<OffCpuFrame> Stack);
    internal sealed record LastSeenSwitchOut(int Tid, string Comm, string PrevState, IReadOnlyList<OffCpuFrame> Stack, double TimestampSeconds);

    /// <summary>
    /// Records every sched_switch event observed for any TID in <paramref name="targetTids"/>
    /// and returns the closed off-CPU spans plus the total switch count that was attributed
    /// to the target (used for capture-density sanity checks).
    /// </summary>
    /// <param name="output">Raw <c>perf script</c> stdout.</param>
    /// <param name="targetTids">Kernel TIDs belonging to the target process; OUT/IN events outside this set are ignored.</param>
    /// <param name="flushPending">
    /// When <c>true</c>, any pending OUT for a target TID that never paired with an IN is emitted
    /// as a <see cref="OffCpuSpan.IsCensored"/> span with duration = (max observed timestamp − OUT timestamp).
    /// This captures long blockers (locks held nearly the whole window, I/O that outlasts the capture)
    /// which would otherwise vanish from the report. Default <c>false</c> preserves the strict
    /// closed-pair semantics for unit tests.
    /// </param>
    /// <param name="addressResolver">
    /// Optional callback that maps a frame's raw program-counter address (the leading hex token
    /// in each <c>perf script</c> stack line) to its canonical
    /// <see cref="DotnetDiagnostics.Core.Memory.MethodIdentity"/> handoff payload. Resolution
    /// is by address — not by symbol string — so overloaded methods that share a rendered
    /// <c>Type.Method</c> name still get their own correct identity. Frames whose address
    /// falls outside any JIT'd range keep <c>Identity = null</c> (native, kernel, unresolved JIT).
    /// </param>
    public static (IReadOnlyList<OffCpuSpan> Spans, long SchedSwitches) Parse(
        string output,
        HashSet<int> targetTids,
        bool flushPending = false,
        Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? addressResolver = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(targetTids);

        var spans = new List<OffCpuSpan>();
        using var reader = new StringReader(output);
        var switches = ParseAsync(
            reader,
            targetTids,
            span => spans.Add(span),
            flushPending,
            addressResolver,
            CancellationToken.None).GetAwaiter().GetResult();
        return (spans, switches);
    }

    public static async Task<long> ParseAsync(
        TextReader reader,
        HashSet<int> targetTids,
        Action<OffCpuSpan> onSpan,
        bool flushPending = false,
        Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? addressResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetTids);
        ArgumentNullException.ThrowIfNull(onSpan);

        var pending = new Dictionary<int, (double Ts, string State, List<OffCpuFrame> Stack, string Comm)>();
        long switches = 0;
        double maxTs = double.MinValue;
        string? pendingHeader = null;

        while (true)
        {
            var raw = pendingHeader ?? await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            pendingHeader = null;
            if (raw is null)
            {
                break;
            }

            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var ev = TryParseHeader(line);
            if (ev is null)
            {
                continue;
            }

            while (true)
            {
                var rawFrameLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (rawFrameLine is null)
                {
                    break;
                }

                var frameLine = rawFrameLine.TrimEnd('\r');
                if (frameLine.Length == 0)
                {
                    break;
                }

                if (frameLine.Contains(" sched:sched_switch:", StringComparison.Ordinal) || !char.IsWhiteSpace(frameLine[0]))
                {
                    pendingHeader = frameLine;
                    break;
                }

                var frame = ParseFrame(frameLine, addressResolver);
                if (frame is not null)
                {
                    ev.Stack.Add(frame);
                }
            }

            var prevIsTarget = targetTids.Contains(ev.PrevTid);
            var nextIsTarget = targetTids.Contains(ev.NextTid);
            if (ev.TimestampSeconds > maxTs)
            {
                maxTs = ev.TimestampSeconds;
            }

            if (nextIsTarget && pending.Remove(ev.NextTid, out var pendingOut))
            {
                var durMicros = (long)Math.Max(0, Math.Round((ev.TimestampSeconds - pendingOut.Ts) * 1_000_000.0));
                onSpan(new OffCpuSpan(
                    Tid: ev.NextTid,
                    Comm: pendingOut.Comm,
                    DurationMicros: durMicros,
                    PrevState: pendingOut.State,
                    BlockingStack: pendingOut.Stack));
            }

            if (prevIsTarget)
            {
                switches++;
                pending[ev.PrevTid] = (ev.TimestampSeconds, NormalizeState(ev.PrevState), ev.Stack, ev.PrevComm);
            }
        }

        if (flushPending && maxTs > double.MinValue)
        {
            foreach (var kv in pending)
            {
                if (!targetTids.Contains(kv.Key))
                {
                    continue;
                }

                var pendingOut = kv.Value;
                var durMicros = (long)Math.Max(0, Math.Round((maxTs - pendingOut.Ts) * 1_000_000.0));
                if (durMicros <= 0)
                {
                    continue;
                }

                onSpan(new OffCpuSpan(
                    Tid: kv.Key,
                    Comm: pendingOut.Comm,
                    DurationMicros: durMicros,
                    PrevState: pendingOut.State,
                    BlockingStack: pendingOut.Stack,
                    IsCensored: true));
            }
        }

        return switches;
    }

    /// <summary>
    /// Returns the most recent sched_switch OUT event observed per target TID. This powers the
    /// no-ptrace thread-snapshot fallback, which replays the "last seen" blocking stack for each
    /// thread over a short perf window.
    /// </summary>
    public static IReadOnlyDictionary<int, LastSeenSwitchOut> ParseLastSeenSwitchOut(
        string output,
        HashSet<int> targetTids)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(targetTids);

        var lines = output.Split('\n');
        var byTid = new Dictionary<int, LastSeenSwitchOut>();

        var i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i].TrimEnd('\r');
            if (raw.Length == 0 || raw.StartsWith('#'))
            {
                i++;
                continue;
            }

            var ev = TryParseHeader(raw);
            if (ev is null)
            {
                i++;
                continue;
            }

            i++;
            while (i < lines.Length)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) { i++; break; }
                if (line.Contains(" sched:sched_switch:", StringComparison.Ordinal)) break;
                if (!char.IsWhiteSpace(line[0])) break;
                var frame = ParseFrame(line);
                if (frame is not null) ev.Stack.Add(frame);
                i++;
            }

            if (!targetTids.Contains(ev.PrevTid)) continue;
            if (ev.Stack.Count == 0) continue;

            byTid[ev.PrevTid] = new LastSeenSwitchOut(
                Tid: ev.PrevTid,
                Comm: ev.PrevComm,
                PrevState: NormalizeState(ev.PrevState),
                Stack: ev.Stack,
                TimestampSeconds: ev.TimestampSeconds);
        }

        return byTid;
    }

    private static string NormalizeState(string s)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        return s[0].ToString();
    }

    private static SchedEvent? TryParseHeader(string line)
    {
        const string Marker = " sched:sched_switch:";
        var markerIdx = line.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIdx < 0) return null;

        var prefix = line[..markerIdx];
        var payload = line[(markerIdx + Marker.Length)..].Trim();
        var colonIdx = prefix.LastIndexOf(':');
        if (colonIdx < 0) return null;
        var beforeColon = prefix[..colonIdx];
        var lastSpace = beforeColon.LastIndexOf(' ');
        if (lastSpace < 0) return null;
        var tsToken = beforeColon[(lastSpace + 1)..];
        if (!double.TryParse(tsToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
        {
            return null;
        }

        if (!TryParsePayload(payload, out var prevComm, out var prevTid, out var prevState,
                out var nextComm, out var nextTid))
        {
            return null;
        }

        return new SchedEvent(ts, prevComm, prevTid, prevState, nextComm, nextTid, []);
    }

    private static bool TryParsePayload(string payload, out string prevComm, out int prevTid, out string prevState,
        out string nextComm, out int nextTid)
    {
        prevComm = string.Empty; prevTid = 0; prevState = "?";
        nextComm = string.Empty; nextTid = 0;

        if (payload.Contains("prev_pid=", StringComparison.Ordinal))
        {
            prevComm = ExtractKv(payload, "prev_comm=");
            prevTid = ExtractKvInt(payload, "prev_pid=");
            prevState = ExtractKv(payload, "prev_state=");
            nextComm = ExtractKv(payload, "next_comm=");
            nextTid = ExtractKvInt(payload, "next_pid=");
            return prevTid != 0 || nextTid != 0;
        }

        var arrow = payload.IndexOf("==>", StringComparison.Ordinal);
        if (arrow < 0) return false;
        var left = payload[..arrow].Trim();
        var right = payload[(arrow + 3)..].Trim();
        if (!TryParseCompactSide(left, out prevComm, out prevTid, out prevState)) return false;
        if (!TryParseCompactSide(right, out nextComm, out nextTid, out _)) return false;
        return true;
    }

    private static bool TryParseCompactSide(string s, out string comm, out int tid, out string state)
    {
        comm = string.Empty; tid = 0; state = "?";
        var colon = s.LastIndexOf(':');
        if (colon < 0) return false;
        comm = s[..colon];
        var rest = s[(colon + 1)..].Trim();
        var firstSpace = rest.IndexOf(' ');
        var tidToken = firstSpace > 0 ? rest[..firstSpace] : rest;
        if (!int.TryParse(tidToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out tid)) return false;

        if (firstSpace > 0)
        {
            foreach (var token in rest[(firstSpace + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('[')) continue;
                state = token;
                break;
            }
        }
        return true;
    }

    private static string ExtractKv(string payload, string key)
    {
        var idx = payload.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        var start = idx + key.Length;
        var end = payload.IndexOf(' ', start);
        if (end < 0) end = payload.Length;
        return payload[start..end];
    }

    private static int ExtractKvInt(string payload, string key)
    {
        var s = ExtractKv(payload, key);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static OffCpuFrame? ParseFrame(
        string line,
        Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? addressResolver = null)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return null;

        var lastOpen = trimmed.LastIndexOf('(');
        var lastClose = trimmed.LastIndexOf(')');
        string module;
        string symbolPart;
        if (lastOpen >= 0 && lastClose > lastOpen)
        {
            module = trimmed.Substring(lastOpen + 1, lastClose - lastOpen - 1);
            symbolPart = trimmed[..lastOpen].TrimEnd();
        }
        else
        {
            module = string.Empty;
            symbolPart = trimmed;
        }

        var firstSpace = symbolPart.IndexOf(' ');
        ulong? address = null;
        string symbol;
        if (firstSpace > 0)
        {
            var addrToken = symbolPart[..firstSpace];
            if (ulong.TryParse(addrToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
            {
                address = addr;
            }
            symbol = symbolPart[(firstSpace + 1)..].TrimStart();
        }
        else
        {
            symbol = symbolPart;
        }

        var plus = symbol.LastIndexOf("+0x", StringComparison.Ordinal);
        if (plus > 0) symbol = symbol[..plus];

        if (symbol.Length == 0) return null;
        DotnetDiagnostics.Core.Memory.MethodIdentity? identity = null;
        if (address.HasValue && addressResolver is not null)
        {
            identity = addressResolver(address.Value);
        }
        return new OffCpuFrame(Module: module, Method: symbol, Identity: identity);
    }
}

/// <summary>One paired off-CPU span: thread went OUT at <c>ts</c> with <c>BlockingStack</c>, came back IN <c>DurationMicros</c> µs later.</summary>
internal sealed record OffCpuSpan(int Tid, string Comm, long DurationMicros, string PrevState, IReadOnlyList<OffCpuFrame> BlockingStack, bool IsCensored = false);
