using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Mcp.Hosting;

public sealed record PortableTransferOptions(PortableCaptureImportWorker? Worker)
{
    internal static PortableTransferOptions FromConfiguration(IConfiguration? configuration)
    {
        var executable = configuration?["DOTNET_DIAGNOSTICS_IMPORT_WORKER"];
        var library = configuration?["DOTNET_DIAGNOSTICS_SQLITE_LIBRARY"];
        if (string.IsNullOrEmpty(executable) && string.IsNullOrEmpty(library)) return new(Worker: null);
        if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(library))
            throw new InvalidOperationException("Both trusted portable import worker and SQLite library paths must be configured.");
        var worker = new PortableCaptureImportWorker(executable, library);
        worker.Validate();
        return new(worker);
    }
}
