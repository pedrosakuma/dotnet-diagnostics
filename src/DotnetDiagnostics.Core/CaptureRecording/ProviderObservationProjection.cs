using System.Globalization;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>
/// Projects explicitly selected, already interpreted provider scalars. Never reads payloads.
/// UTC timestamps use round-trip strings; UInt64 dimensions exceeding Int64 remain exact decimal
/// strings rather than lossy doubles. Aggregate and retained-evidence categories are not occurrence logs.
/// </summary>
internal static class ProviderObservationProjection
{
    internal static CaptureObservation Create(
        string category, DateTimeOffset? timestamp, long? threadId, string? name,
        params (string Name, object? Value)[] fields) =>
        new(category, timestamp, threadId, name, fields.Select(static field => field.Value switch
        {
            null => CaptureObservationField.Null(field.Name),
            string value => CaptureObservationField.String(field.Name, value),
            bool value => CaptureObservationField.Bool(field.Name, value),
            int value => CaptureObservationField.Int64(field.Name, value),
            long value => CaptureObservationField.Int64(field.Name, value),
            uint value => CaptureObservationField.Int64(field.Name, value),
            ulong value when value <= long.MaxValue => CaptureObservationField.Int64(field.Name, (long)value),
            ulong value => CaptureObservationField.String(field.Name, value.ToString(CultureInfo.InvariantCulture)),
            ushort value => CaptureObservationField.Int64(field.Name, value),
            byte value => CaptureObservationField.Int64(field.Name, value),
            double value => CaptureObservationField.Double(field.Name, value),
            float value => CaptureObservationField.Double(field.Name, value),
            DateTimeOffset value => CaptureObservationField.String(field.Name, value.ToString("O", CultureInfo.InvariantCulture)),
            Guid value => CaptureObservationField.String(field.Name, value.ToString("D")),
            _ => throw new ArgumentException($"Unsupported provider scalar '{field.Name}'."),
        }).ToArray());
}
