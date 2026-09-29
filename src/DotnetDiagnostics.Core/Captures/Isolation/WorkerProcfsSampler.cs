namespace DotnetDiagnostics.Core.Captures;

internal static class WorkerProcfsSampler
{
    internal static long ReadStartTime(int processId)
    {
        var bytes = File.ReadAllBytes($"/proc/{processId}/stat");
        if (!TryParseStartTime(bytes, out var startTime)) throw new IOException("Worker stat record is malformed.");
        return startTime;
    }

    internal static bool TryParseStartTime(ReadOnlySpan<byte> stat, out long startTime)
    {
        startTime = 0;
        var close = stat.LastIndexOf((byte)')');
        if (close < 0) return false;
        var rest = stat[(close + 1)..];
        var field = 2;
        var position = 0;
        while (field < 22)
        {
            if (position >= rest.Length || rest[position] != (byte)' ') return false;
            position++;
            field++;
            var start = position;
            while (position < rest.Length && rest[position] is not ((byte)' ' or (byte)'\n')) position++;
            if (position == start) return false;
            if (field == 22) return TryParseUnsigned(rest[start..position], out startTime) && startTime > 0;
        }
        return false;
    }

    private static bool TryParseUnsigned(ReadOnlySpan<byte> token, out long value)
    {
        value = 0;
        foreach (var digit in token)
        {
            if (digit is < (byte)'0' or > (byte)'9' || value > (long.MaxValue - 9) / 10) return false;
            value = value * 10 + (digit - '0');
        }
        return true;
    }
}
