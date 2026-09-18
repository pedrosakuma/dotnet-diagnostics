using System.Runtime.CompilerServices;

namespace BadCodeSample;

internal static class CultureLookupWorkload
{
    // These entrypoints own the real lookup operation. Keep their stack identities observable
    // without constraining inlining or algorithms inside the framework or native globalization.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static LookupResult RunCultureSensitive(string[] keys, IReadOnlyDictionary<string, bool> flags, int loops)
    {
        var hits = RunLookups(keys, flags, loops);
        return new LookupResult(loops, hits, "InvariantCultureIgnoreCase");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static LookupResult RunOrdinal(string[] keys, IReadOnlyDictionary<string, bool> flags, int loops)
    {
        var hits = RunLookups(keys, flags, loops);
        return new LookupResult(loops, hits, "OrdinalIgnoreCase");
    }

    private static long RunLookups(string[] keys, IReadOnlyDictionary<string, bool> flags, int loops)
    {
        var hits = 0L;
        for (var i = 0; i < loops; i++)
        {
            var key = keys[i % keys.Length];
            if (flags.TryGetValue(key, out var enabled) && enabled)
            {
                hits++;
            }
        }
        return hits;
    }

    internal sealed record LookupResult(int Loops, long Hits, string Comparer);
}
