using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    private const long MaximumGcPressureArtifactBytes = 256 * 1024;

    private static async Task<CliCommandResult> GcPressureAsync(
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(options.CaptureFile!);
        try
        {
            var bytes = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var captureId = TryGetString(root, "id");
            var schemaVersion = TryGetInt32(root, "schemaVersion");
            GcPressureCharacterizationResult result;
            if (schemaVersion != GcPressureCharacterizer.CurrentCaptureSchemaVersion
                || !HasCurrentContract(root))
            {
                result = GcPressureCharacterizer.IncompatibleLegacy(
                    captureId,
                    schemaVersion == GcPressureCharacterizer.CurrentCaptureSchemaVersion
                        ? null
                        : schemaVersion);
            }
            else
            {
                var capture = JsonSerializer.Deserialize<GcPressureCapture>(
                    bytes,
                    GcPressureCharacterizer.CreateJsonOptions(writeIndented: false))
                    ?? throw new JsonException("The GC pressure capture is empty.");
                result = GcPressureCharacterizer.Evaluate(capture);
            }

            var summary = $"GC pressure characterization selected {ActionName(result.NextAction)} for capture {result.Identity.CaptureId}.";
            return BuildResult(DiagnosticResult.Ok(result, summary), RenderGcPressure);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or NotSupportedException)
        {
            return Failure("InvalidGcPressureArtifact", exception.Message);
        }
    }

    private static void RenderGcPressure(
        System.Text.StringBuilder builder,
        GcPressureCharacterizationResult result)
    {
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"  capture          : {result.Identity.CaptureId}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  scenario         : {result.Identity.Scenario}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  runtime          : {result.Identity.RuntimeVersion} ({result.Identity.OperatingSystem}/{result.Identity.Architecture})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  windows          : {result.LohSize.Median.Counts.Windows} x {result.Identity.WindowSeconds:0.###}s");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  availability     : {AvailabilityName(result.LohSize.Median.Availability)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  selected action  : {ActionName(result.NextAction)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  reason           : {result.Reason}");
        if (result.Persistence is { } persistence)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  persistence      : pressure={persistence.PressureWindows}, allocation={persistence.AllocationPressureWindows}, longest-allocation-run={persistence.LongestAllocationPressureRun}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"  cooldown recovery: {FormatNullable(result.PostLoadStabilized)}");
        }

        builder.AppendLine();
        builder.AppendLine("  bounded evidence:");
        RenderMetric(builder, result.LohSize);
        RenderMetric(builder, result.Gen2Rate);
        RenderMetric(builder, result.PauseDuration);
        RenderMetric(builder, result.SuspensionTime);

        if (result.TimingObservations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  timing observations:");
            foreach (var timing in result.TimingObservations)
            {
                builder.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"    - {timing.FirstSignal}@w{timing.FirstWindow} / {timing.SecondSignal}@w{timing.SecondWindow}: {timing.Interpretation}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("  limitations:");
        foreach (var limitation in result.Limitations)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    - {limitation}");
        }
    }

    private static void RenderMetric(
        System.Text.StringBuilder builder,
        GcPressureMetricEvidence metric)
    {
        var availability = AvailabilityName(metric.Median.Availability);
        var median = FormatMetricNumber(metric.Median.Estimate);
        var p95 = FormatMetricNumber(metric.P95);
        var slope = FormatMetricNumber(metric.Slope.Slope);
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"    {metric.QuantityId,-26} availability={availability}; median={median} {metric.Unit}; p95={p95} {metric.Unit}; slope={slope} {metric.Unit}/window");
    }

    private static CliCommandResult Failure(string code, string message)
        => BuildResult<object>(
            DiagnosticResult.Fail<object>(
                "GC pressure characterization failed.",
                new DiagnosticError(code, message)),
            static (_, _) => { });

    private static string? TryGetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static int? TryGetInt32(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.TryGetInt32(out var value)
                ? value
                : null;

    private static bool HasCurrentContract(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && root.TryGetProperty("scenario", out var scenario)
            && scenario.ValueKind == JsonValueKind.String
            && root.TryGetProperty("runtimeVersion", out var runtime)
            && runtime.ValueKind == JsonValueKind.String
            && root.TryGetProperty("operatingSystem", out var operatingSystem)
            && operatingSystem.ValueKind == JsonValueKind.String
            && root.TryGetProperty("architecture", out var architecture)
            && architecture.ValueKind == JsonValueKind.String
            && root.TryGetProperty("windowSeconds", out var windowSeconds)
            && windowSeconds.ValueKind == JsonValueKind.Number
            && root.TryGetProperty("windows", out var windows)
            && windows.ValueKind == JsonValueKind.Array
            && windows.EnumerateArray().All(HasCurrentWindowContract)
            && root.TryGetProperty("notes", out var notes)
            && notes.ValueKind == JsonValueKind.Array
            && notes.EnumerateArray().All(note => note.ValueKind == JsonValueKind.String);

    private static bool HasCurrentWindowContract(JsonElement window)
        => window.ValueKind == JsonValueKind.Object
            && HasNumber(window, "index")
            && HasString(window, "phase")
            && HasNumber(window, "lohSizeBytes")
            && HasNumber(window, "gen2CollectionsPerSecond")
            && HasNumber(window, "pauseP95Milliseconds")
            && HasNumber(window, "timeInGcPercent")
            && HasNumber(window, "sourceEvents")
            && HasNumber(window, "lostOrDropped")
            && HasString(window, "representation");

    private static bool HasNumber(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number;

    private static bool HasString(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String;

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumGcPressureArtifactBytes)
        {
            throw new InvalidDataException(
                $"gc-pressure input is limited to {MaximumGcPressureArtifactBytes} bytes; '{path}' is {stream.Length} bytes.");
        }

        var bytes = new byte[MaximumGcPressureArtifactBytes + 1];
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(totalRead, bytes.Length - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead > MaximumGcPressureArtifactBytes)
        {
            throw new InvalidDataException(
                $"gc-pressure input is limited to {MaximumGcPressureArtifactBytes} bytes; '{path}' exceeded the limit while it was read.");
        }

        return bytes.AsSpan(0, totalRead).ToArray();
    }

    private static string FormatMetricNumber(double? value)
        => value is null ? "unavailable" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatNullable(bool? value)
        => value switch
        {
            true => "observed",
            false => "not observed",
            null => "unavailable",
        };

    private static string AvailabilityName(GcPressureAvailability availability)
        => availability switch
        {
            GcPressureAvailability.Available => "available",
            GcPressureAvailability.InsufficientObservations => "insufficient_observations",
            GcPressureAvailability.Incompatible => "incompatible",
            GcPressureAvailability.LossDetected => "loss_detected",
            GcPressureAvailability.UnsupportedApproximation => "unsupported_approximation",
            _ => throw new ArgumentOutOfRangeException(nameof(availability)),
        };

    private static string ActionName(GcPressureNextAction action)
        => action switch
        {
            GcPressureNextAction.AllocationInvestigation => "allocation-investigation",
            GcPressureNextAction.RetainedHeapInvestigation => "retained-heap-investigation",
            GcPressureNextAction.ContinuedObservation => "continued-observation",
            GcPressureNextAction.JustifiedInconclusion => "justified-inconclusion",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
}
