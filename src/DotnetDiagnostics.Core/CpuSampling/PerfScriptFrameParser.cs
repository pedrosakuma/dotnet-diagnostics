using System.Globalization;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.OffCpu;

namespace DotnetDiagnostics.Core.CpuSampling;

internal static class PerfScriptFrameParser
{
    public static PerfFrame? Parse(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var moduleOpen = FindModuleOpen(trimmed);
        string module;
        string symbolPart;
        if (moduleOpen >= 0)
        {
            module = trimmed.Substring(moduleOpen + 2, trimmed.Length - moduleOpen - 3);
            symbolPart = trimmed[..moduleOpen].TrimEnd();
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
            var addressToken = symbolPart[..firstSpace];
            if (TryParseHexAddress(addressToken, out var parsedAddress))
            {
                address = parsedAddress;
            }

            symbol = symbolPart[(firstSpace + 1)..].TrimStart();
        }
        else
        {
            symbol = symbolPart;
        }

        var plus = symbol.LastIndexOf("+0x", StringComparison.Ordinal);
        if (plus > 0)
        {
            symbol = symbol[..plus];
        }

        return symbol.Length == 0 ? null : new PerfFrame(module, symbol, address);
    }

    public static bool IsUnresolvedJitCandidate(PerfFrame frame)
        => IsUnknownSymbol(frame.Symbol) || IsDoublemapperModule(frame.Module);

    private static bool IsUnknownSymbol(string symbol)
        => string.Equals(symbol, "[unknown]", StringComparison.Ordinal) ||
           (symbol.Length > 2 &&
            symbol[0] == '[' &&
            symbol[^1] == ']' &&
            symbol.Contains("unknown", StringComparison.OrdinalIgnoreCase));

    private static bool IsDoublemapperModule(string module)
        => module.Contains("memfd:doublemapper", StringComparison.Ordinal);

    private static int FindModuleOpen(string trimmed)
    {
        if (!trimmed.EndsWith(')'))
        {
            return -1;
        }

        // perf renders the module as the final " (...)" segment. Module paths themselves can
        // contain parentheses, e.g. "/memfd:doublemapper (deleted)", so LastIndexOf('(') is wrong.
        var absolutePath = trimmed.IndexOf(" (/", StringComparison.Ordinal);
        if (absolutePath >= 0)
        {
            return absolutePath;
        }

        var bracketedModule = trimmed.IndexOf(" ([", StringComparison.Ordinal);
        if (bracketedModule >= 0)
        {
            return bracketedModule;
        }

        return trimmed.LastIndexOf(" (", StringComparison.Ordinal);
    }

    private static bool TryParseHexAddress(string token, out ulong address)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            token = token[2..];
        }

        return ulong.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}

internal sealed class PerfJitFrameSymbolizer
{
    private const int MaxCachedAddresses = 16_384;

    private readonly JitMapResult? _jitMap;
    private readonly Dictionary<ulong, JitMapResolvedFrame?> _cache = new();

    public PerfJitFrameSymbolizer(JitMapResult? jitMap)
    {
        _jitMap = jitMap is { MethodCount: > 0 } ? jitMap : null;
    }

    public long CandidateFrames { get; private set; }
    public long ResolvedFrames { get; private set; }
    public long UnresolvedCandidateFrames { get; private set; }

    public PerfFrame Symbolize(PerfFrame frame)
    {
        var isCandidate = PerfScriptFrameParser.IsUnresolvedJitCandidate(frame);
        if (isCandidate)
        {
            CandidateFrames++;
        }

        if (frame.Address is not { } address)
        {
            if (isCandidate)
            {
                UnresolvedCandidateFrames++;
            }

            return frame;
        }

        var resolved = Resolve(address);
        if (resolved is null)
        {
            if (isCandidate)
            {
                UnresolvedCandidateFrames++;
            }

            return frame;
        }

        var resolvedFrame = resolved.Value;
        ResolvedFrames++;
        var identity = resolvedFrame.Identity;
        var display = !string.IsNullOrEmpty(resolvedFrame.DisplayName)
            ? resolvedFrame.DisplayName
            : BuildDisplayName(identity, frame.Symbol);
        var module = identity.ModuleName ?? identity.ModulePath ?? frame.Module;
        return frame with
        {
            Module = module,
            Symbol = display,
            Identity = identity,
        };
    }

    private JitMapResolvedFrame? Resolve(ulong address)
    {
        if (_jitMap is null)
        {
            return null;
        }

        if (_cache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var resolved = _jitMap.ResolveFrame(address);
        if (_cache.Count < MaxCachedAddresses)
        {
            _cache[address] = resolved;
        }

        return resolved;
    }

    private static string BuildDisplayName(MethodIdentity identity, string fallback)
    {
        if (!string.IsNullOrEmpty(identity.TypeFullName) && !string.IsNullOrEmpty(identity.MethodName))
        {
            return identity.TypeFullName + "." + identity.MethodName;
        }

        return string.IsNullOrEmpty(identity.MethodName) ? fallback : identity.MethodName;
    }
}

internal static class PerfJitSymbolizationNotes
{
    public static void Add(ICollection<string> notes, long candidateFrames, long resolvedFrames, long unresolvedCandidateFrames)
    {
        ArgumentNullException.ThrowIfNull(notes);

        if (candidateFrames <= 0 || unresolvedCandidateFrames <= 0)
        {
            return;
        }

        if (resolvedFrames <= 0)
        {
            notes.Add(
                $"CoreCLR JIT frame symbolization was unavailable for {unresolvedCandidateFrames:N0} /memfd:doublemapper or bracketed unknown frame(s); showing raw perf frames for those entries.");
            return;
        }

        notes.Add(
            $"CoreCLR JIT frame symbolization resolved {resolvedFrames:N0} frame(s), but {unresolvedCandidateFrames:N0} /memfd:doublemapper or bracketed unknown frame(s) did not match the captured JIT map and remain raw perf frames.");
    }
}
