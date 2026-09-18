using System.Text;

namespace DotnetDiagnostics.TestSupport;

/// <summary>Insertion-bounded evidence that survives startup and teardown failures.</summary>
public sealed class LiveSampleEvidence(Action<string>? observer = null)
{
    public const int OutputTailCharacters = 4096;
    public const int PhaseTailCharacters = 8192;
    private readonly object _gate = new();
    private readonly BoundedTextTail _stdout = new(OutputTailCharacters);
    private readonly BoundedTextTail _stderr = new(OutputTailCharacters);
    private readonly BoundedTextTail _phases = new(PhaseTailCharacters);
    private readonly BoundedTextTail _errors = new(2048);
    private long _oversizedLines;

    internal void OversizedLine()
    {
        lock (_gate) _oversizedLines++;
    }

    public void Mark(string phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        var text = $"{DateTimeOffset.UtcNow:O} {phase[..Math.Min(phase.Length, 512)]}";
        lock (_gate) _phases.Append(text + "\n");
        try { observer?.Invoke(text); }
        catch (Exception ex) { Error("phase-observer", ex); }
    }

    internal void Output(bool stderr, ReadOnlySpan<char> text)
    {
        lock (_gate) (stderr ? _stderr : _stdout).Append(text);
    }

    internal void Error(string phase, Exception error)
    {
        var message = error.Message;
        lock (_gate) _errors.Append($"{phase}: {error.GetType().Name}: {message[..Math.Min(message.Length, 512)]}\n");
    }

    public string Describe()
    {
        lock (_gate)
            return $"phases (cap={PhaseTailCharacters}, droppedCharacters={_phases.DroppedCharacters}):\n{_phases}\n" +
                $"stdout (cap={OutputTailCharacters}, droppedCharacters={_stdout.DroppedCharacters}):\n{_stdout}\n" +
                $"stderr (cap={OutputTailCharacters}, droppedCharacters={_stderr.DroppedCharacters}):\n{_stderr}\n" +
                $"URL parser oversizedLines={_oversizedLines}, lineCap={LiveSampleOutput.MaximumLineCharacters}\n" +
                $"errors (cap=2048, droppedCharacters={_errors.DroppedCharacters}):\n{_errors}";
    }
}

internal sealed class BoundedTextTail(int capacity)
{
    private readonly StringBuilder _text = new(capacity);
    public long DroppedCharacters { get; private set; }
    public void Append(ReadOnlySpan<char> text)
    {
        var drop = Math.Max(0, _text.Length + (long)text.Length - capacity);
        DroppedCharacters += drop;
        if (text.Length >= capacity)
        {
            _text.Clear();
            _text.Append(text[^capacity..]);
            return;
        }
        if (drop > 0) _text.Remove(0, (int)drop);
        _text.Append(text);
    }
    public override string ToString() => _text.ToString();
}

internal static class LiveSampleOutput
{
    internal const int MaximumLineCharacters = 2048;

    internal static async Task DrainAsync(TextReader reader, bool stderr, LiveSampleEvidence evidence,
        Action<string> lineObserved, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var line = new StringBuilder(MaximumLineCharacters);
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            evidence.Output(stderr, buffer.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (!truncated) lineObserved(line.ToString().TrimEnd('\r'));
                    line.Clear();
                    truncated = false;
                }
                else if (line.Length < MaximumLineCharacters) line.Append(buffer[i]);
                else if (!truncated) { truncated = true; evidence.OversizedLine(); }
            }
        }
        if (!truncated && line.Length > 0) lineObserved(line.ToString());
    }
}
