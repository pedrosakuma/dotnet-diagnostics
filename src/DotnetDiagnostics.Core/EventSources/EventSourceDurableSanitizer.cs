using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>
/// Sanitizes durable copies only. Provider authorization and the original ephemeral capture are
/// unchanged; allowing an unsafe provider does not authorize raw credential persistence.
/// </summary>
internal static class EventSourceDurableSanitizer
{
    private static readonly SensitiveDataRedactor DefaultRedactor = new();

    internal static EventSourceCapture Sanitize(EventSourceCapture capture, SensitiveDataRedactor? redactor = null) =>
        capture with
        {
            Events = capture.Events.Select(observation => observation with
            {
                Payload = observation.Payload.ToDictionary(
                    static field => field.Key,
                    field => SanitizeValue(field.Key, field.Value, redactor),
                    StringComparer.Ordinal),
            }).ToArray(),
        };

    internal static string SanitizeValue(string name, string value, SensitiveDataRedactor? redactor = null)
    {
        // Credential values need not resemble secrets (for example Password="abc"). Inspect
        // the field name as well as applying the existing pattern policy to ordinary values.
        var normalized = string.Concat(name.Where(static c => char.IsLetterOrDigit(c))).ToUpperInvariant();
        if (normalized.EndsWith("PASSWORD", StringComparison.Ordinal)
            || normalized.EndsWith("PASSWD", StringComparison.Ordinal)
            || normalized.EndsWith("PWD", StringComparison.Ordinal)
            || normalized.EndsWith("SECRET", StringComparison.Ordinal)
            || normalized.EndsWith("SECRETKEY", StringComparison.Ordinal)
            || normalized.EndsWith("APIKEY", StringComparison.Ordinal)
            || normalized.EndsWith("ACCESSKEY", StringComparison.Ordinal)
            || normalized.EndsWith("PRIVATEKEY", StringComparison.Ordinal)
            || normalized.EndsWith("TOKEN", StringComparison.Ordinal)
            || normalized.EndsWith("AUTHORIZATION", StringComparison.Ordinal)
            || normalized.EndsWith("CREDENTIAL", StringComparison.Ordinal)
            || normalized.EndsWith("CREDENTIALS", StringComparison.Ordinal)
            || normalized.EndsWith("COOKIE", StringComparison.Ordinal))
            return SensitiveDataRedactor.RedactedPlaceholder;
        return (redactor ?? DefaultRedactor).Redact(value)!;
    }
}
