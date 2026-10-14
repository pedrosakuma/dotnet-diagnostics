using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Address-navigation helpers for the stream protocol's <c>query</c> request (issue #1168): strict
/// request-boundary validation of exact 64-bit hexadecimal addresses and wire projections that keep
/// every address a <c>0x</c> hexadecimal string (never a JSON number, which a JavaScript consumer
/// would round above 2^53).
/// </summary>
internal static class CliStreamingQueryNavigation
{
    /// <summary>Heap views that read one object through ClrMD and therefore need an address and an acknowledgement.</summary>
    internal static readonly IReadOnlySet<string> ObjectReadViews =
        new HashSet<string>(StringComparer.Ordinal) { "object", "gcroot" };

    /// <summary>The captured-data view that accepts an optional exact-address filter (referencing statics).</summary>
    internal const string StaticFieldsView = "static-fields";

    internal const string AddressFormatMessage =
        "'address' must be a JSON string holding a non-zero 0x-prefixed hexadecimal value of at most 16 digits (never a JSON number).";

    internal enum AddressStatus
    {
        Absent,
        Valid,
        Invalid,
    }

    /// <summary>
    /// Reads the optional <c>address</c> property. Only a JSON string of the form <c>0x</c> followed by
    /// 1-16 hexadecimal digits that is not zero is accepted; numbers, decimal strings, whitespace and
    /// other shapes are <see cref="AddressStatus.Invalid"/> so no value is ever silently coerced.
    /// </summary>
    internal static AddressStatus TryReadAddress(JsonElement root, out ulong address)
    {
        address = 0;
        if (!root.TryGetProperty("address", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return AddressStatus.Absent;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return AddressStatus.Invalid;
        }

        return TryParseHexAddress(element.GetString(), out address) ? AddressStatus.Valid : AddressStatus.Invalid;
    }

    internal static bool TryParseHexAddress(string? value, out ulong address)
    {
        address = 0;
        if (value is null
            || value.Length < 3
            || value.Length > 18
            || value[0] != '0'
            || (value[1] != 'x' && value[1] != 'X'))
        {
            return false;
        }

        foreach (var c in value.AsSpan(2))
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address)
            && address != 0;
    }

    internal static string FormatAddress(ulong address) =>
        "0x" + address.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>
    /// Keeps only the static fields whose referenced object is exactly <paramref name="address"/> (a
    /// projection of the already-captured list; nothing is re-read from the target).
    /// </summary>
    internal static HeapSnapshotArtifact FilterStaticFields(HeapSnapshotArtifact snapshot, ulong address) =>
        snapshot.StaticFields is null
            ? snapshot
            : snapshot with { StaticFields = [.. snapshot.StaticFields.Where(field => field.ValueAddress == address)] };

    internal static object ProjectObject(HeapObjectInspection inspection) => new
    {
        address = FormatAddress(inspection.Address),
        typeFullName = inspection.TypeFullName,
        size = inspection.Size,
        segmentKind = inspection.SegmentKind,
        generation = inspection.Generation,
        isArray = inspection.IsArray,
        arrayLength = inspection.ArrayLength,
        arraySample = inspection.ArraySample?.Select(static element => new
        {
            index = element.Index,
            typeFullName = element.TypeFullName,
            value = element.Value,
            objectAddress = element.ObjectAddress is { } address ? FormatAddress(address) : null,
            referencedTypeFullName = element.ReferencedTypeFullName,
        }),
        isString = inspection.IsString,
        stringValue = inspection.StringValue,
        stringValueTruncated = inspection.StringValueTruncated,
        fields = inspection.Fields?.Select(static field => new
        {
            name = field.Name,
            typeFullName = field.TypeFullName,
            value = field.Value,
            objectAddress = field.ObjectAddress is { } address ? FormatAddress(address) : null,
            referencedTypeFullName = field.ReferencedTypeFullName,
        }),
        warnings = inspection.Warnings,
    };

    internal static object ProjectGcRoot(HeapGcRootInspection inspection) => new
    {
        address = FormatAddress(inspection.Address),
        typeFullName = inspection.TypeFullName,
        chain = inspection.Chain.Select(static frame => new
        {
            typeFullName = frame.TypeFullName,
            objectAddress = FormatAddress(frame.ObjectAddress),
            rootKind = frame.RootKind,
        }),
        truncated = inspection.Truncated,
        warnings = inspection.Warnings,
    };
}
