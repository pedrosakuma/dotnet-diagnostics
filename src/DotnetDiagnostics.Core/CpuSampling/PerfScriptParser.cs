using System.Globalization;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Parser for the textual output of <c>perf script</c>. Each sample begins with a header
/// line (process / pid / timestamp / event) followed by one frame per indented line and
/// is terminated by a blank line. We only care about the indented stack frames here.
/// </summary>
/// <remarks>
/// Frame format (whitespace-separated):
/// <code>
///     ffffabcd Method+0x12 (/path/to/module.so)
/// </code>
/// or for kernel / anonymous frames:
/// <code>
///     ffffabcd [unknown] ([kernel.kallsyms])
/// </code>
/// The <c>Method</c> field may contain spaces inside <c>::operator()</c> etc; the module
/// is always the last parenthesised token on the line, so we parse from the right.
/// </remarks>
internal static class PerfScriptParser
{
    /// <summary>
    /// Parses textual <c>perf script</c> output into a sequence of stacks. Each stack is a
    /// list of frames ordered leaf→root (the same orientation <c>perf script</c> uses).
    /// </summary>
    /// <param name="output">Captured stdout of <c>perf script -i &lt;file&gt;</c>.</param>
    /// <param name="processId">When non-zero, frames from samples whose header reports a different
    /// pid are skipped. Matches the &quot;process 1234&quot; segment of the header line.</param>
    public static IReadOnlyList<PerfSample> Parse(string output, int processId = 0)
    {
        ArgumentNullException.ThrowIfNull(output);

        var samples = new List<PerfSample>();
        using var reader = new StringReader(output);
        ParseAsync(
            reader,
            processId,
            sample =>
            {
                samples.Add(sample);
                return true;
            },
            CancellationToken.None).GetAwaiter().GetResult();
        return samples;
    }

    public static async Task<PerfScriptParseResult> ParseAsync(
        TextReader reader,
        int processId,
        Func<PerfSample, bool> onSample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onSample);

        HashSet<int>? acceptedTids = null;
        if (processId != 0)
        {
            acceptedTids = TryReadProcessTids(processId);
        }

        long samplesEmitted = 0;
        string? pendingHeader = null;

        while (true)
        {
            var rawHeader = pendingHeader ?? await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            pendingHeader = null;
            if (rawHeader is null)
            {
                break;
            }

            var header = rawHeader.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(header) || header.StartsWith('#'))
            {
                continue;
            }

            if (char.IsWhiteSpace(header[0]))
            {
                continue;
            }

            var samplePid = TryExtractPid(header);
            var shouldSkip = samplePid != 0 &&
                (acceptedTids is { } tids
                    ? !tids.Contains(samplePid)
                    : processId != 0 && samplePid != processId);
            var frames = shouldSkip ? null : new List<PerfFrame>();

            while (true)
            {
                var rawLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (rawLine is null)
                {
                    break;
                }

                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                if (!char.IsWhiteSpace(line[0]))
                {
                    pendingHeader = line;
                    break;
                }

                if (frames is null)
                {
                    continue;
                }

                var frame = ParseFrame(line);
                if (frame is not null)
                {
                    frames.Add(frame);
                }
            }

            if (frames is not { Count: > 0 })
            {
                continue;
            }

            samplesEmitted++;
            if (!onSample(new PerfSample(samplePid, frames)))
            {
                return new PerfScriptParseResult(samplesEmitted, Completed: false);
            }
        }

        return new PerfScriptParseResult(samplesEmitted, Completed: true);
    }

    private static readonly char[] HeaderSeparators = [' ', '\t'];

    private static HashSet<int>? TryReadProcessTids(int processId)
    {
        try
        {
            var taskDir = $"/proc/{processId}/task";
            if (!Directory.Exists(taskDir))
            {
                return null;
            }

            var set = new HashSet<int>();
            foreach (var dir in Directory.EnumerateDirectories(taskDir))
            {
                var name = Path.GetFileName(dir);
                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
                {
                    set.Add(tid);
                }
            }

            set.Add(processId);
            return set;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryExtractPid(string header)
    {
        foreach (var token in header.Split(HeaderSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return 0;
    }

    private static PerfFrame? ParseFrame(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
        {
            return null;
        }

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
        var symbol = firstSpace > 0 ? symbolPart[(firstSpace + 1)..].TrimStart() : symbolPart;
        var plus = symbol.LastIndexOf("+0x", StringComparison.Ordinal);
        if (plus > 0)
        {
            symbol = symbol[..plus];
        }

        return symbol.Length == 0 ? null : new PerfFrame(Module: module, Symbol: symbol);
    }
}

internal readonly record struct PerfScriptParseResult(long SamplesEmitted, bool Completed);

internal sealed record PerfSample(int ProcessId, IReadOnlyList<PerfFrame> Frames);

internal sealed record PerfFrame(string Module, string Symbol);
