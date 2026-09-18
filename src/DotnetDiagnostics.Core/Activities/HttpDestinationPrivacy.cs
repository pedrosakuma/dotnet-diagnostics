using System.Globalization;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Activities;

/// <summary>Redacts structured authority without changing original Activity tags.</summary>
public static class HttpDestinationPrivacy
{
    public static HttpActivityDestination? Redact(HttpActivityDestination? destination, SensitiveDataRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        if (destination is null) return null;
        if (destination.Scheme is null && destination.Host is null && destination.Port is null) return destination;
        var scheme = redactor.Redact(destination.Scheme);
        var host = redactor.Redact(destination.Host);
        var port = destination.Port?.ToString(CultureInfo.InvariantCulture);
        // Scan both individual components and their authority form, so configured host:port
        // or scheme://host patterns cannot be bypassed by the structured representation.
        if (scheme != destination.Scheme || host != destination.Host || redactor.Redact(port) != port ||
            redactor.Redact($"{destination.Scheme}://{destination.Host}:{port}") != $"{destination.Scheme}://{destination.Host}:{port}")
            return new HttpActivityDestination("redacted", Provenance: destination.Provenance);
        return destination;
    }

    public static ActivityCapture Redact(ActivityCapture capture, SensitiveDataRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.Activities.Any(a => a.Destination is not null)) return capture;
        var activities = capture.Activities.Select(a => a with { Destination = Redact(a.Destination, redactor) }).ToArray();
        return capture with
        {
            Activities = activities,
            HttpDestinationCorrelation = capture.HttpDestinationCorrelation is { } facts
                ? facts with { Available = activities.Count(a => a.Destination?.Availability == "available") } : null,
        };
    }
}
