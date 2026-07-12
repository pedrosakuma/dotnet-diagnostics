using System.Text.RegularExpressions;

namespace DotnetDiagnostics.Core.Internal;

/// <summary>
/// Caches compiled wildcard-filter regexes with a small size cap and FIFO eviction, so a client
/// that repeatedly supplies distinct source/category filter strings cannot grow the cache
/// unbounded for the lifetime of the process.
/// </summary>
internal sealed class BoundedWildcardRegexCache
{
    private const int DefaultCapacity = 64;

    private readonly int _capacity;
    private readonly Dictionary<string, Regex> _cache;
    private readonly Queue<string> _insertionOrder;
    private readonly object _lock = new();

    public BoundedWildcardRegexCache(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        _cache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        _insertionOrder = new Queue<string>();
    }

    public Regex GetOrAdd(string pattern, Func<string, Regex> factory)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }

            var regex = factory(pattern);
            _cache[pattern] = regex;
            _insertionOrder.Enqueue(pattern);

            while (_cache.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            {
                _cache.Remove(oldest);
            }

            return regex;
        }
    }
}
