using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Explicit trusted host assets for the internal Linux import worker, not archive-supplied paths.</summary>
public sealed record PortableCaptureImportWorker(string Executable, string SqliteLibrary)
{
    internal static string? CurrentRuntimeIdentifier
        => GetSupportedRuntimeIdentifier(OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.RuntimeIdentifier);

    internal static string? GetSupportedRuntimeIdentifier(bool isLinux, Architecture architecture, string runtimeIdentifier)
    {
        if (!isLinux) return null;
        return (architecture, runtimeIdentifier) switch
        {
            (Architecture.X64, "linux-x64") => "linux-x64",
            (Architecture.Arm64, "linux-arm64") => "linux-arm64",
            _ => null
        };
    }

    /// <summary>Validates trusted configured assets and supported platform without launching a worker.</summary>
    public void Validate()
    {
        if (CurrentRuntimeIdentifier is null)
            throw IsolatedCaptureWorker.Unsupported("ImportWorkerUnavailable");
        foreach (var path in new[] { Executable, SqliteLibrary })
        {
            if (path is null || !Path.IsPathFullyQualified(path) || path.Length > 2048 ||
                path.Contains('\0') || !File.Exists(path)) throw IsolatedCaptureWorker.Unsupported("ImportWorkerUnavailable");
            CapturePackage.RejectLinks(path);
        }
    }
}
