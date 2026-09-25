namespace DotnetDiagnostics.Core.CaptureRecording;

internal enum CaptureObservationValueKind { Null, String, Integer, Number, Boolean }

internal readonly record struct CaptureObservationField
{
    private CaptureObservationField(string name, CaptureObservationValueKind kind,
        string? text = null, long integer = 0, double number = 0, bool boolean = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Kind = kind;
        Text = text;
        Integer = integer;
        Number = number;
        Boolean = boolean;
    }

    internal string Name { get; }
    internal CaptureObservationValueKind Kind { get; }
    internal string? Text { get; }
    internal long Integer { get; }
    internal double Number { get; }
    internal bool Boolean { get; }

    internal static CaptureObservationField Null(string name) => new(name, CaptureObservationValueKind.Null);
    internal static CaptureObservationField String(string name, string? value)
        => value is null ? Null(name) : new(name, CaptureObservationValueKind.String, text: value);
    internal static CaptureObservationField Int64(string name, long value)
        => new(name, CaptureObservationValueKind.Integer, integer: value);
    internal static CaptureObservationField Double(string name, double value)
        => new(name, CaptureObservationValueKind.Number, number: value);
    internal static CaptureObservationField Bool(string name, bool value)
        => new(name, CaptureObservationValueKind.Boolean, boolean: value);
}

/// <summary>A sanitized, interpreted observation, not an unfiltered provider payload.</summary>
internal sealed record CaptureObservation(
    string Category,
    DateTimeOffset? Timestamp,
    long? ThreadId,
    string? Name,
    IReadOnlyList<CaptureObservationField> Fields);
