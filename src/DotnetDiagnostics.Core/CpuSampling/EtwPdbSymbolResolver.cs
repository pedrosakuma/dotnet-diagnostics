using System.Runtime.InteropServices;
using Dia2Lib;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DotnetDiagnostics.Core.CpuSampling;

internal enum NativeSymbolRangeStatus
{
    Unavailable,
    RangeMissing,
    LookupFailed,
    OutsideRange,
    InRange,
}

internal enum NativeSymbolResolverOpenStatus
{
    Ready,
    MissingPdbIdentity,
    MatchingPdbUnavailable,
    DiaUnavailable,
    PdbRejected,
}

internal enum NativeSymbolProvenance
{
    None,
    PdbDia,
}

internal readonly record struct NativeSymbolResolution(
    string? Name,
    NativeSymbolRangeStatus RangeStatus,
    NativeSymbolProvenance Provenance,
    Guid? PdbSignature,
    int? PdbAge,
    uint? FunctionStartRva,
    ulong? FunctionLength)
{
    public bool CanInternName
        => RangeStatus == NativeSymbolRangeStatus.InRange
            && !string.IsNullOrWhiteSpace(Name)
            && Provenance == NativeSymbolProvenance.PdbDia;

    public static NativeSymbolResolution FromPdb(
        string? name,
        uint addressRva,
        uint functionStartRva,
        ulong functionLength,
        Guid pdbSignature,
        int pdbAge)
    {
        if (pdbSignature == Guid.Empty || pdbAge < 0)
        {
            return Unavailable(NativeSymbolRangeStatus.Unavailable);
        }

        var status = functionLength == 0
            ? NativeSymbolRangeStatus.RangeMissing
            : IsInRange(addressRva, functionStartRva, functionLength)
                ? NativeSymbolRangeStatus.InRange
                : NativeSymbolRangeStatus.OutsideRange;
        return new NativeSymbolResolution(
            status == NativeSymbolRangeStatus.InRange ? name : null,
            status,
            NativeSymbolProvenance.PdbDia,
            pdbSignature,
            pdbAge,
            functionStartRva,
            functionLength);
    }

    public static NativeSymbolResolution Unavailable(NativeSymbolRangeStatus status)
        => new(null, status, NativeSymbolProvenance.None, null, null, null, null);

    private static bool IsInRange(uint addressRva, uint startRva, ulong length)
    {
        var address = (ulong)addressRva;
        var start = (ulong)startRva;
        return address >= start && address - start < length;
    }
}

internal sealed class EtwPdbSymbolResolver : IDisposable
{
    private readonly IDiaDataSource dataSource;
    private readonly IDiaSession session;
    private readonly Guid pdbSignature;
    private readonly int pdbAge;
    private bool disposed;

    private EtwPdbSymbolResolver(
        IDiaDataSource dataSource,
        IDiaSession session,
        Guid pdbSignature,
        int pdbAge)
    {
        this.dataSource = dataSource;
        this.session = session;
        this.pdbSignature = pdbSignature;
        this.pdbAge = pdbAge;
    }

    public static bool TryOpen(
        SymbolReader symbolReader,
        TraceModuleFile module,
        out EtwPdbSymbolResolver? resolver,
        out NativeSymbolResolverOpenStatus status)
    {
        resolver = null;
        if (module.PdbSignature == Guid.Empty || module.PdbAge < 0 || string.IsNullOrWhiteSpace(module.PdbName))
        {
            status = NativeSymbolResolverOpenStatus.MissingPdbIdentity;
            return false;
        }

        var pdbPath = symbolReader.FindSymbolFilePath(
            module.PdbName, module.PdbSignature, module.PdbAge, module.FilePath);
        if (string.IsNullOrWhiteSpace(pdbPath))
        {
            status = NativeSymbolResolverOpenStatus.MatchingPdbUnavailable;
            return false;
        }

        return TryOpenPdb(pdbPath, module.PdbSignature, module.PdbAge, out resolver, out status);
    }

    internal static bool TryOpenPdb(
        string pdbPath,
        Guid pdbSignature,
        int pdbAge,
        out EtwPdbSymbolResolver? resolver,
        out NativeSymbolResolverOpenStatus status)
    {
        resolver = null;
        if (pdbSignature == Guid.Empty || pdbAge < 0)
        {
            status = NativeSymbolResolverOpenStatus.MissingPdbIdentity;
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            status = NativeSymbolResolverOpenStatus.DiaUnavailable;
            return false;
        }

        IDiaDataSource? dataSource = null;
        IDiaSession? session = null;
        try
        {
            dataSource = NativeDiaLoader.CreateDataSource();
            var expectedSignature = pdbSignature;
            dataSource.loadAndValidateDataFromPdb(
                pdbPath,
                ref expectedSignature,
                0,
                checked((uint)pdbAge));
            dataSource.openSession(out session);
            if (session is null)
            {
                status = NativeSymbolResolverOpenStatus.PdbRejected;
                return false;
            }

            resolver = new EtwPdbSymbolResolver(
                dataSource,
                session,
                pdbSignature,
                pdbAge);
            dataSource = null;
            session = null;
            status = NativeSymbolResolverOpenStatus.Ready;
            return true;
        }
        catch (COMException)
        {
            status = dataSource is null
                ? NativeSymbolResolverOpenStatus.DiaUnavailable
                : NativeSymbolResolverOpenStatus.PdbRejected;
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            status = NativeSymbolResolverOpenStatus.DiaUnavailable;
            return false;
        }
        finally
        {
            ReleaseComObject(session);
            ReleaseComObject(dataSource);
        }
    }

    public NativeSymbolResolution Resolve(uint addressRva)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        IDiaSymbol symbol = null!;
        try
        {
            session.findSymbolByRVA(addressRva, SymTagEnum.SymTagFunction, out symbol);
            if (symbol is null)
            {
                return NativeSymbolResolution.Unavailable(NativeSymbolRangeStatus.Unavailable);
            }

            var resolution = NativeSymbolResolution.FromPdb(
                symbol.name,
                addressRva,
                symbol.relativeVirtualAddress,
                symbol.length,
                pdbSignature,
                pdbAge);
            if (!resolution.CanInternName)
            {
                return resolution;
            }

            var displayName = symbol.name;
            string undecoratedName = string.Empty;
            symbol.get_undecoratedNameEx(4096, out undecoratedName);
            if (!string.IsNullOrWhiteSpace(undecoratedName))
            {
                displayName = undecoratedName;
            }

            return resolution with { Name = displayName };
        }
        catch (COMException)
        {
            return NativeSymbolResolution.Unavailable(NativeSymbolRangeStatus.LookupFailed);
        }
        finally
        {
            ReleaseComObject(symbol);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ReleaseComObject(session);
        ReleaseComObject(dataSource);
    }

    private static void ReleaseComObject(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}

internal sealed class EtwPdbSymbolResolverPool : IDisposable
{
    private readonly Dictionary<ModuleFileIndex, EtwPdbSymbolResolver> resolvers = [];
    private readonly Dictionary<NativeSymbolResolverOpenStatus, int> openStatusCounts = [];
    private bool disposed;

    public IReadOnlyDictionary<NativeSymbolResolverOpenStatus, int> OpenStatusCounts
        => openStatusCounts;

    public static EtwPdbSymbolResolverPool Open(
        SymbolReader? symbolReader,
        IReadOnlyList<TraceModuleFile> modules)
    {
        var pool = new EtwPdbSymbolResolverPool();
        if (symbolReader is null)
        {
            return pool;
        }

        foreach (var module in modules)
        {
            if (pool.resolvers.ContainsKey(module.ModuleFileIndex))
            {
                continue;
            }

            if (EtwPdbSymbolResolver.TryOpen(symbolReader, module, out var resolver, out var status))
            {
                pool.resolvers.Add(module.ModuleFileIndex, resolver!);
            }

            pool.openStatusCounts[status] = pool.openStatusCounts.GetValueOrDefault(status) + 1;
        }

        return pool;
    }

    public NativeSymbolResolution Resolve(TraceCodeAddress? codeAddress)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (codeAddress?.ModuleFile is not { } module
            || codeAddress.Address < module.ImageBase
            || codeAddress.Address - module.ImageBase > uint.MaxValue
            || !resolvers.TryGetValue(module.ModuleFileIndex, out var resolver))
        {
            return NativeSymbolResolution.Unavailable(NativeSymbolRangeStatus.Unavailable);
        }

        return resolver.Resolve((uint)(codeAddress.Address - module.ImageBase));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var resolver in resolvers.Values)
        {
            resolver.Dispose();
        }
    }
}
