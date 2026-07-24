using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Symbols;

/// <summary>
/// Resolves arbitrary addresses against the module map of a process or dump on demand (issue #275).
/// Unlike frame enrichment — which classifies the addresses ClrMD already walked — this answers
/// "what lives at <c>0x…</c>?" for any pointer, including ones on native threads ClrMD never visits
/// (e.g. the TSD-cleanup thread in dotnet/runtime#128525).
/// </summary>
public interface INativeAddressResolver
{
    /// <summary>
    /// Re-opens the origin of <paramref name="artifact"/> (dump file or live pid) and classifies each
    /// address. Dump origin is time-consistent with the snapshot; live origin is best-effort (the
    /// process may have moved on or exited).
    /// </summary>
    Task<IReadOnlyList<NativeAddressLocation>> ResolveAsync(
        ThreadSnapshotArtifact artifact,
        IReadOnlyList<ulong> addresses,
        CancellationToken cancellationToken = default);
}

/// <summary>ClrMD-backed <see cref="INativeAddressResolver"/>.</summary>
public sealed class ClrMdNativeAddressResolver : INativeAddressResolver
{
    private readonly MvidReader _mvidReader = new();

    public Task<IReadOnlyList<NativeAddressLocation>> ResolveAsync(
        ThreadSnapshotArtifact artifact,
        IReadOnlyList<ulong> addresses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<NativeAddressLocation>>(Array.Empty<NativeAddressLocation>());
        }

        return Task.Run<IReadOnlyList<NativeAddressLocation>>(() => Resolve(artifact, addresses, cancellationToken), cancellationToken);
    }

    private List<NativeAddressLocation> Resolve(
        ThreadSnapshotArtifact artifact,
        IReadOnlyList<ulong> addresses,
        CancellationToken ct)
    {
        using var target = OpenTarget(artifact);
        var reader = target.DataReader;
        var map = NativeModuleMap.Build(reader.EnumerateModules().Select(static m => new NativeModuleRange(
            ImageBase: m.ImageBase,
            Size: m.ImageSize > 0 ? (ulong)m.ImageSize : 0,
            FileName: m.FileName ?? string.Empty,
            BuildId: m.BuildId.IsDefaultOrEmpty ? null : Convert.ToHexString(m.BuildId.AsSpan()).ToLowerInvariant(),
            IsManaged: m.IsManaged)));

        // A CLR runtime is optional — a NativeAOT or partial dump may not expose one. When absent the
        // managed dimension is simply skipped; native module classification still works.
        ClrRuntime? runtime = null;
        var clrInfo = target.ClrVersions.FirstOrDefault();
        if (clrInfo is not null)
        {
            try { runtime = clrInfo.CreateRuntime(); }
            catch (Exception) { runtime = null; }
        }

        try
        {
            var results = new List<NativeAddressLocation>(addresses.Count);
            foreach (var address in addresses)
            {
                ct.ThrowIfCancellationRequested();
                var location = NativeAddressClassifier.Resolve(
                    address,
                    map,
                    resolveManaged: runtime is null ? null : addr => ToIdentity(runtime.GetMethodByInstructionPointer(addr)),
                    probeReadable: addr => ProbeReadable(reader, addr));
                results.Add(location);
            }

            return results;
        }
        finally
        {
            runtime?.Dispose();
        }
    }

    private static DataTarget OpenTarget(ThreadSnapshotArtifact artifact)
    {
        if (artifact.Origin == ThreadSnapshotOrigin.Dump)
        {
            if (string.IsNullOrEmpty(artifact.DumpFilePath))
            {
                throw new InvalidOperationException("Dump-origin thread snapshot has no retained dump path; cannot resolve addresses.");
            }

            return ClrMdDumpLoader.Load(artifact.DumpFilePath);
        }

        if (artifact.ProcessId <= 0)
        {
            throw new InvalidOperationException("Live-origin thread snapshot has no usable process id; cannot resolve addresses.");
        }

        return DataTarget.AttachToProcess(artifact.ProcessId, suspend: true);
    }

    private MethodIdentity? ToIdentity(ClrMethod? method)
    {
        if (method is null) return null;
        var modulePath = method.Type?.Module?.Name;
        var moduleName = string.IsNullOrEmpty(modulePath) ? null : System.IO.Path.GetFileName(modulePath);
        return new MethodIdentity(
            MethodName: method.Name ?? "<unknown>",
            GenericArity: 0,
            ModuleName: moduleName,
            ModulePath: modulePath,
            ModuleVersionId: _mvidReader.TryRead(modulePath),
            MetadataToken: method.MetadataToken == 0 ? null : method.MetadataToken,
            TypeFullName: method.Type?.Name);
    }

    private static bool? ProbeReadable(IDataReader reader, ulong address)
    {
        try
        {
            Span<byte> one = stackalloc byte[1];
            return reader.Read(address, one) == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
