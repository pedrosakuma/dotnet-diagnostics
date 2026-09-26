using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Explicit trusted host assets for the internal Linux import worker, not archive-supplied paths.</summary>
public sealed record PortableCaptureImportWorker(string Executable, string SqliteLibrary)
{
    internal void Validate()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw IsolatedCaptureWorker.Unsupported("ImportWorkerUnavailable");
        foreach (var path in new[] { Executable, SqliteLibrary })
        {
            if (path is null || !Path.IsPathFullyQualified(path) || path.Length > 2048 ||
                path.Contains('\0') || !File.Exists(path)) throw IsolatedCaptureWorker.Unsupported("ImportWorkerUnavailable");
            CapturePackage.RejectLinks(path);
        }
    }
}
