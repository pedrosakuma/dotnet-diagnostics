using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>Applies the same metadata-only or pattern-redacted value policy to heap inspection clients.</summary>
public static class HeapObjectValueSanitizer
{
    public static HeapObjectInspection Sanitize(
        HeapObjectInspection inspection, SensitiveDataRedactor redactor, bool includeSensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(redactor);

        string Value(string value) => includeSensitiveValues
            ? redactor.Redact(value)!
            : SensitiveDataRedactor.MetadataOnlyPlaceholder;
        HeapObjectField Field(HeapObjectField field) => field with { Value = Value(field.Value) };

        return inspection with
        {
            Fields = inspection.Fields?.Select(Field).ToArray(),
            ArraySample = inspection.ArraySample?.Select(element => element with
            {
                Value = Value(element.Value),
                Fields = element.Fields?.Select(Field).ToArray(),
            }).ToArray(),
            StringValue = inspection.IsString
                ? includeSensitiveValues
                    ? redactor.Redact(inspection.StringValue)
                    : SensitiveDataRedactor.MetadataOnlyPlaceholder
                : inspection.StringValue,
        };
    }
}
