using System.Globalization;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Activities;

/// <summary>
/// Single-writer whole-window identity history. Never evict tombstones: at saturation all
/// attribution is withheld, since an unseen duplicate could invalidate an earlier pair.
/// </summary>
internal sealed class HttpDestinationCorrelationState
{
    internal const int MaxIdentities = 16384;
    internal const int MaxAuthorities = 4096;
    internal const string Provenance = "diagnostic-source-http-start";
    private readonly Dictionary<(string Trace, string Span), Entry> _entries = new();
    private readonly string? _traceId;
    private readonly int _identityCap;
    private readonly int _authorityCap;
    private int _authorities;
    private long _starts, _stops, _nonMatching, _invalidIds, _duplicateStarts, _duplicateStops;
    private long _conflicts, _invalidAuthorities, _identityCapEvents, _authorityCapEvents;

    internal HttpDestinationCorrelationState(string? traceId, int identityCap = MaxIdentities, int authorityCap = MaxAuthorities)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(identityCap, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(authorityCap, 1);
        _traceId = traceId;
        _identityCap = identityCap;
        _authorityCap = authorityCap;
    }

    internal void ObserveStart(IReadOnlyDictionary<string, string> arguments)
    {
        _starts++;
        var entry = Get(arguments.GetValueOrDefault("ActivityTraceId"), arguments.GetValueOrDefault("ActivitySpanId"));
        if (entry is null) return;
        var authority = ParseAuthority(arguments);
        if (entry.Starts != 0)
        {
            _duplicateStarts++;
            if (entry.Authority != authority) _conflicts++;
            entry.Starts = 2;
            return;
        }
        entry.Starts = 1;
        if (authority is null)
        {
            _invalidAuthorities++;
            entry.Unavailable = "invalid-authority";
        }
        else if (_authorities == _authorityCap)
        {
            _authorityCapEvents++;
            entry.Unavailable = "authority-cap";
        }
        else
        {
            _authorities++;
            entry.Authority = authority;
        }
    }

    internal void ObserveStop(CapturedActivity activity)
    {
        _stops++;
        var entry = Get(activity.TraceId, activity.SpanId);
        if (entry is null) return;
        if (entry.Stops != 0) _duplicateStops++;
        entry.Stops = Math.Min(2, entry.Stops + 1);
    }

    internal CapturedActivity Project(CapturedActivity activity, bool cleanStream, SensitiveDataRedactor redactor)
    {
        if (activity.SourceName != "System.Net.Http" || activity.OperationName != "System.Net.Http.HttpRequestOut")
            return activity;
        var unavailable = !cleanStream ? "transport-incomplete" : _identityCapEvents > 0 ? "identity-cap" : null;
        if (unavailable is null && TryKey(activity.TraceId, activity.SpanId, out var key) && _entries.TryGetValue(key, out var entry))
        {
            unavailable = entry.Starts > 1 || entry.Stops > 1 ? "ambiguous-identity"
                : entry.Starts == 0 ? "missing-start" : entry.Unavailable;
            if (unavailable is null && entry.Stops == 1 && entry.Authority is { } authority)
                return activity with { Destination = HttpDestinationPrivacy.Redact(authority, redactor) };
        }
        return activity with { Destination = new HttpActivityDestination(unavailable ?? "missing-identity") };
    }

    internal HttpDestinationCorrelation Snapshot(bool cleanStream, int available) => new(
        !cleanStream ? "transport-incomplete" : _identityCapEvents > 0 ? "identity-cap" : "observed",
        _identityCap, _authorityCap, _starts, _stops, _nonMatching, _invalidIds, _duplicateStarts, _duplicateStops,
        _conflicts, _invalidAuthorities, _identityCapEvents, _authorityCapEvents,
        _entries.Values.Count(e => e.Starts > 0 && e.Stops == 0),
        _entries.Values.Count(e => e.Stops > 0 && e.Starts == 0), available);

    private Entry? Get(string? traceId, string? spanId)
    {
        if (_traceId is not null && !string.Equals(_traceId, traceId, StringComparison.OrdinalIgnoreCase))
        {
            _nonMatching++;
            return null;
        }
        if (!TryKey(traceId, spanId, out var key))
        {
            _invalidIds++;
            return null;
        }
        if (_entries.TryGetValue(key, out var entry)) return entry;
        if (_entries.Count == _identityCap)
        {
            _identityCapEvents++;
            return null;
        }
        entry = new Entry();
        _entries.Add(key, entry);
        return entry;
    }

    private static bool TryKey(string? traceId, string? spanId, out (string Trace, string Span) key)
    {
        key = default;
        if (!ActivityTraceProjector.TryNormalizeTraceId(traceId, out var trace) ||
            spanId is not { Length: 16 } || !spanId.All(Uri.IsHexDigit) || spanId.All(c => c == '0'))
            return false;
        key = (trace, spanId.ToLowerInvariant());
        return true;
    }

    private static HttpActivityDestination? ParseAuthority(IReadOnlyDictionary<string, string> arguments)
    {
        var scheme = arguments.GetValueOrDefault("Scheme");
        var host = arguments.GetValueOrDefault("Host");
        if (scheme is not ("http" or "https") || host is not { Length: > 0 and <= 253 } ||
            Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown ||
            !int.TryParse(arguments.GetValueOrDefault("Port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            return null;
        return new HttpActivityDestination("available", scheme, host, port, Provenance);
    }

    private sealed class Entry
    {
        internal int Starts;
        internal int Stops;
        internal HttpActivityDestination? Authority;
        internal string? Unavailable;
    }
}
