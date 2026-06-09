using System.Globalization;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Symbols;

/// <summary>
/// Classification of a raw instruction pointer / address against the module map of a
/// process or dump. Replaces surfacing a bare hex address with a meaningful location
/// (issue #275). The <see cref="NativeAddressKind.UnmappedOrNotCaptured"/> bucket is
/// deliberately conservative: a dump may simply not have captured a region, so we never
/// claim a definitive "unmapped" without a memory-readability probe saying otherwise.
/// </summary>
public enum NativeAddressKind
{
    /// <summary>Address falls inside a loaded native module's image range.</summary>
    Module,

    /// <summary>Address resolves to a managed method (JIT/R2R code), via ClrMD.</summary>
    Managed,

    /// <summary>Address is readable memory but not inside any loaded module (JIT stub, anonymous mapping, heap).</summary>
    MappedNonModule,

    /// <summary>Address is not inside any module and either not readable or not probed — a freed hole or a region the dump did not capture.</summary>
    UnmappedOrNotCaptured,
}

/// <summary>One loaded module's image range, used to map an address to (module, RVA).</summary>
/// <param name="ImageBase">Image base virtual address.</param>
/// <param name="Size">Image size in bytes. Always &gt; 0 (zero/negative entries are dropped by <see cref="NativeModuleMap.Build"/>).</param>
/// <param name="FileName">Module file name or full path as reported by the data target.</param>
/// <param name="BuildId">Lower-case hex build-id (ELF) when available; <c>null</c> otherwise.</param>
/// <param name="IsManaged">True when the data target flagged the module as a managed PE.</param>
public sealed record NativeModuleRange(ulong ImageBase, ulong Size, string FileName, string? BuildId, bool IsManaged)
{
    /// <summary>Exclusive end of the image range. Guaranteed &gt; <see cref="ImageBase"/> by the builder.</summary>
    public ulong End => ImageBase + Size;
}

/// <summary>
/// The classified location of a single address. <see cref="Display"/> is always safe to show to
/// the LLM (never a bare hex), e.g. <c>libcrypto.so.3+0x1edc0</c> or
/// <c>&lt;unmapped-or-not-captured 0x7f18cc41edc0&gt;</c>.
/// </summary>
public sealed record NativeAddressLocation(
    ulong Address,
    NativeAddressKind Kind,
    string? Module,
    string? ModulePath,
    ulong? Rva,
    string? BuildId,
    bool? Readable,
    MethodIdentity? ManagedMethod,
    string Display);

/// <summary>
/// Immutable, sorted module map supporting overlap-aware containment lookup. Built once per
/// capture (or per re-open) from the data target's module list.
/// </summary>
public sealed class NativeModuleMap
{
    private readonly NativeModuleRange[] _byBase;

    private NativeModuleMap(NativeModuleRange[] byBase) => _byBase = byBase;

    /// <summary>Number of modules retained in the map.</summary>
    public int Count => _byBase.Length;

    /// <summary>
    /// Builds a map from raw module records. Entries with a zero/negative size or whose
    /// <c>ImageBase + Size</c> would overflow are dropped (they cannot bound a lookup).
    /// </summary>
    public static NativeModuleMap Build(IEnumerable<NativeModuleRange> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var kept = new List<NativeModuleRange>();
        foreach (var m in modules)
        {
            if (m.Size == 0) continue;
            if (m.ImageBase > ulong.MaxValue - m.Size) continue; // would overflow End
            kept.Add(m);
        }

        kept.Sort(static (a, b) => a.ImageBase.CompareTo(b.ImageBase));
        return new NativeModuleMap(kept.ToArray());
    }

    /// <summary>
    /// Finds the most specific module containing <paramref name="address"/> (smallest range when
    /// overlapping). Returns <c>false</c> when no module contains the address.
    /// </summary>
    public bool TryResolve(ulong address, out NativeModuleRange module, out ulong rva)
    {
        module = null!;
        rva = 0;
        if (_byBase.Length == 0) return false;

        // Binary search for the last entry whose ImageBase <= address.
        int lo = 0, hi = _byBase.Length - 1, start = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (_byBase[mid].ImageBase <= address)
            {
                start = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (start < 0) return false;

        // Walk left over earlier entries and pick the most specific (smallest) containing range —
        // handles overlapping module records in degraded dumps. Module lists are small (hundreds),
        // and this loop only runs to completion for the rare unmapped / overlap case; an exact module
        // hit is usually found at (or just below) the binary-search start.
        NativeModuleRange? best = null;
        for (var i = start; i >= 0; i--)
        {
            var candidate = _byBase[i];
            if (address < candidate.End && address >= candidate.ImageBase)
            {
                if (best is null || candidate.Size < best.Size)
                {
                    best = candidate;
                }
            }
        }

        if (best is null) return false;
        module = best;
        rva = address - best.ImageBase;
        return true;
    }
}

/// <summary>
/// Pure address-classification logic (issue #275). Independent of ClrMD so it can be unit-tested
/// with synthetic module maps and probe delegates. The ClrMD-backed adapter supplies the module
/// map, the managed-method resolver, and the memory-readability probe.
/// </summary>
public static class NativeAddressClassifier
{
    /// <summary>
    /// Classifies <paramref name="address"/> against <paramref name="map"/>.
    /// </summary>
    /// <param name="address">The raw address / instruction pointer.</param>
    /// <param name="map">Loaded-module map.</param>
    /// <param name="resolveManaged">Optional managed-method resolver (ClrMD GetMethodByInstructionPointer). May return null.</param>
    /// <param name="probeReadable">Optional readability probe. Returns true/false when known, null when not probed.</param>
    public static NativeAddressLocation Resolve(
        ulong address,
        NativeModuleMap map,
        Func<ulong, MethodIdentity?>? resolveManaged = null,
        Func<ulong, bool?>? probeReadable = null)
    {
        ArgumentNullException.ThrowIfNull(map);

        var managed = resolveManaged?.Invoke(address);
        var hasModule = map.TryResolve(address, out var module, out var rva);

        if (hasModule)
        {
            var moduleName = ToFileName(module.FileName);
            // A managed method that also lives inside a (managed) module image: surface both — the
            // managed identity for reasoning and the (module, rva, buildId) for native handoff.
            if (managed is not null)
            {
                return new NativeAddressLocation(
                    address,
                    NativeAddressKind.Managed,
                    moduleName,
                    module.FileName,
                    rva,
                    module.BuildId,
                    Readable: true,
                    ManagedMethod: managed,
                    Display: ManagedDisplay(managed, moduleName, rva));
            }

            return new NativeAddressLocation(
                address,
                NativeAddressKind.Module,
                moduleName,
                module.FileName,
                rva,
                module.BuildId,
                Readable: true,
                ManagedMethod: null,
                Display: $"{moduleName}+0x{rva:x}");
        }

        // No module. A managed method with no module image is still a useful answer.
        if (managed is not null)
        {
            return new NativeAddressLocation(
                address,
                NativeAddressKind.Managed,
                managed.ModuleName,
                managed.ModulePath,
                Rva: null,
                BuildId: null,
                Readable: true,
                ManagedMethod: managed,
                Display: ManagedDisplay(managed, managed.ModuleName, rva: null));
        }

        var readable = probeReadable?.Invoke(address);
        if (readable == true)
        {
            return new NativeAddressLocation(
                address,
                NativeAddressKind.MappedNonModule,
                Module: null,
                ModulePath: null,
                Rva: null,
                BuildId: null,
                Readable: true,
                ManagedMethod: null,
                Display: $"<mapped-non-module 0x{address:x}>");
        }

        return new NativeAddressLocation(
            address,
            NativeAddressKind.UnmappedOrNotCaptured,
            Module: null,
            ModulePath: null,
            Rva: null,
            BuildId: null,
            Readable: readable,
            ManagedMethod: null,
            Display: $"<unmapped-or-not-captured 0x{address:x}>");
    }

    private static string ManagedDisplay(MethodIdentity managed, string? moduleName, ulong? rva)
    {
        var label = managed.MethodName;
        if (!string.IsNullOrEmpty(managed.TypeFullName))
        {
            label = $"{managed.TypeFullName}.{managed.MethodName}";
        }

        if (!string.IsNullOrEmpty(moduleName) && rva is { } r)
        {
            return $"{label} ({moduleName}+0x{r:x})";
        }

        return label;
    }

    private static string ToFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var idx = path.LastIndexOfAny(['/', '\\']);
        return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
    }

    /// <summary>Parses a decimal or 0x-prefixed hex address. Returns false on malformed input.</summary>
    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
    }
}
