using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Mcp.Security;

namespace DotnetDiagnostics.Mcp.Tools;

public sealed partial class DurableCaptureTools
{
    internal async Task<DiagnosticResult<object>> CompareAsync(
        IPrincipalAccessor accessor, JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryAccess(accessor, out var access) || accessor.Current?.HasScope("investigation-export") != true)
            return Forbidden<object>("Current comparison authorization is required.");
        try
        {
            if (Encoding.UTF8.GetByteCount(input.GetRawText()) > 64 * 1024)
                throw new CaptureStoreException(CaptureErrorCode.CapacityExceeded, "ComparisonRequestBytes.");
            Fields(input, ["baseline", "candidate"]);
            Fields(input.GetProperty("baseline"), ["captureId", "artifactId"]);
            Fields(input.GetProperty("candidate"), ["captureId", "artifactId"]);
            var request = JsonSerializer.Deserialize(input, HistoricalComparisonJsonContext.Default.HistoricalComparisonRequest)
                ?? throw new JsonException();
            var result = await _captures.CompareHistoricalAsync(request, access!, (capture, artifact, view, token) =>
            {
                token.ThrowIfCancellationRequested();
                var current = accessor.Current;
                if (current is null || current.OwnershipKey != access!.OwnerId ||
                    capture.OwnerId != current.OwnershipKey && !current.HasScope(BearerPrincipal.RootScope) ||
                    !current.HasScope("investigation-export") || AuthorizeCapture(current, capture, artifact, view) is not null)
                    throw new CaptureStoreException(CaptureErrorCode.Forbidden, "Current comparison authorization is required.");
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            return Bound(DiagnosticResult.Ok<object>(result, "Baseline to candidate retained-evidence comparison; not a causal verdict."));
        }
        catch (CaptureStoreException exception)
        {
            return exception.Code == CaptureErrorCode.Forbidden
                ? Forbidden<object>("Current comparison authorization is required.") : Bound(StoreFailure<object>(exception));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return Invalid("captureComparison requires baseline/candidate objects with only captureId and artifactId.");
        }
    }

    private static void Fields(JsonElement input, string[] names)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new JsonException();
        if (seen.Count != names.Length) throw new JsonException();
    }
}
