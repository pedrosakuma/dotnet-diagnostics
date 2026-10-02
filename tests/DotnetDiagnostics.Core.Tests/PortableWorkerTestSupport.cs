using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

internal static class PortableWorkerTestSupport
{
    internal static bool IsSupported => PortableCaptureImportWorker.CurrentRuntimeIdentifier is not null;

    internal static string Worker => Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER")
        ?? Path.Combine(AppContext.BaseDirectory, "capture-worker");

    internal static string SqliteLibrary => Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY")
        ?? Path.Combine(AppContext.BaseDirectory, "runtimes",
            PortableCaptureImportWorker.CurrentRuntimeIdentifier ?? throw new PlatformNotSupportedException(),
            "native", "libe_sqlite3.so");
}
