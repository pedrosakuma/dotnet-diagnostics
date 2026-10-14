using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static class CliStreamingExport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<object> HandleAsync(JsonElement root, IServiceProvider services, CancellationToken cancellationToken)
    {
        var requestId = String(root, "requestId");
        try
        {
            if (string.IsNullOrWhiteSpace(requestId)) return Fail(requestId, "A non-empty requestId is required.");
            var handle = String(root, "handle");
            if (string.IsNullOrWhiteSpace(handle) || handle.Length > 256 || handle.Any(char.IsControl))
                return Fail(requestId, "A valid CPU capture handle is required.");
            var lookup = services.GetRequiredService<IDiagnosticHandleStore>().TryGetWithKind(handle);
            if (lookup is null) return Fail(requestId, "The CPU capture handle is unknown or expired. Capture again.");
            if (lookup.Value.Kind != "cpu-sample" || lookup.Value.Artifact is not CpuSampleTraceArtifact artifact)
                return Fail(requestId, "Export requires a CPU-sample handle.");

            var format = String(root, "format");
            if (format == "summary")
            {
                var result = await CliCommands.RunAsync(services,
                    new CliOptions { Command = "export-summary", Handle = handle }, cancellationToken).ConfigureAwait(false);
                if (result.IsError || result.Cancelled || !result.RawHuman)
                    return Fail(requestId, FailureMessage(result));
                using var document = JsonDocument.Parse(result.Human);
                return new { type = "export", requestId, format, result = document.RootElement.Clone() };
            }
            if (format != "trace") return Fail(requestId, "Export format must be summary or trace.");
            var outputPath = String(root, "outputPath");
            if (!IsLocalAbsolutePath(outputPath)) return Fail(requestId, "Choose an absolute local output file in the native Save dialog.");
            if (String(root, "acknowledgeRisk") != "critical")
                return Fail(requestId, "Raw trace export requires acknowledgeRisk critical after explicit human confirmation.");
            if (string.IsNullOrWhiteSpace(artifact.TracePath))
                return Fail(requestId, "This capture has no raw trace. Capture CPU with exportTrace true first.");

            using var errors = new StringWriter(CultureInfo.InvariantCulture);
            var disposition = await CliSafetyPreflight.RunAsync(
                new CliOptions { Command = "get-bytes", Kind = "trace", AcknowledgeRisk = "critical" },
                handles: null, CliExecutionContext.OneShot, interactive: false,
                TextReader.Null, TextWriter.Null, errors,
                services.GetRequiredService<IArtifactRootProvider>().Root, cancellationToken).ConfigureAwait(false);
            if (disposition != CliSafetyPreflightDisposition.Proceed)
                return Fail(requestId, $"Trace export safety preflight rejected the request: {errors}");

            // The source is the stored capture's trace, never a path supplied by the webview.
            var materialized = await ByteMaterializationUseCases.MaterializeTraceBytes(
                services.GetRequiredService<IDumpByteSource>(), principalAllowsLiteralScope: true,
                artifact.TracePath, outputPath!, FileChunkReader.MaxChunkBytes,
                logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
            if (materialized.Error is not null || materialized.Data is null)
                return Fail(requestId, materialized.Error?.Message ?? materialized.Summary);
            return new { type = "export", requestId, format, result = materialized.Data };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail(requestId, exception.Message);
        }
    }

    private static bool IsLocalAbsolutePath(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && !path.Any(char.IsControl)
            && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
            && !path.StartsWith("//", StringComparison.Ordinal) && !path.Split('/', '\\').Contains("..")
            && !Directory.Exists(path) && !path.EndsWith('/') && !path.EndsWith('\\');

    private static string FailureMessage(CliCommandResult result)
    {
        var envelope = JsonSerializer.SerializeToElement(result.Envelope, JsonOptions);
        return envelope.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String
            ? summary.GetString()! : "CPU export failed.";
    }

    private static string? String(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static object Fail(string? requestId, string message)
        => new { type = "error", requestId, code = "export_failed", message };
}
