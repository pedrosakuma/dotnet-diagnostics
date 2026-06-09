using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.Internal;

/// <summary>
/// Inspects the loaded native modules of a target process to derive runtime-flavor signals.
/// A CoreCLR-hosted .NET app always loads <c>libcoreclr.so</c> (Linux/macOS) or
/// <c>coreclr.dll</c> (Windows). A self-contained NativeAOT binary never does. This signal
/// is more reliable than relying on EventPipe event traffic for runtime classification.
/// </summary>
internal static class LoadedModuleInspector
{
    public static LoadedModuleSignature? TryGetSignature(int processId)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return InspectLinux(processId);
            }

            return InspectViaProcessModules(processId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static LoadedModuleSignature InspectLinux(int processId)
    {
        var mapsPath = $"/proc/{processId}/maps";
        if (!File.Exists(mapsPath))
        {
            return new LoadedModuleSignature(Inspected: false, HasCoreClr: false);
        }

        using var stream = new FileStream(mapsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Match libcoreclr.so or libcoreclr.dylib (the latter via PInvoke is unlikely on Linux but cheap).
            if (line.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase))
            {
                return new LoadedModuleSignature(Inspected: true, HasCoreClr: true);
            }
        }

        return new LoadedModuleSignature(Inspected: true, HasCoreClr: false);
    }

    private static LoadedModuleSignature InspectViaProcessModules(int processId)
    {
        using var process = Process.GetProcessById(processId);
        foreach (ProcessModule module in process.Modules)
        {
            try
            {
                var name = module.ModuleName ?? string.Empty;
                if (name.StartsWith("coreclr", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("libcoreclr", StringComparison.OrdinalIgnoreCase))
                {
                    return new LoadedModuleSignature(Inspected: true, HasCoreClr: true);
                }
            }
            finally
            {
                module.Dispose();
            }
        }

        return new LoadedModuleSignature(Inspected: true, HasCoreClr: false);
    }
}

internal sealed record LoadedModuleSignature(bool Inspected, bool HasCoreClr);
