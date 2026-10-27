using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Safety;
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

    internal const string TypeIdentityFormatMessage =
        "'typeIdentity' must be an object with a non-empty typeFullName and optional bounded moduleName, modulePath, moduleVersionId, and positive metadataToken fields.";

    internal static bool TryReadTypeIdentity(JsonElement root, out TypeIdentity? identity)
    {
        identity = null;
        if (!root.TryGetProperty("typeIdentity", out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Object
            || !TryReadBoundedString(element, "typeFullName", required: true, 1024, out var typeFullName)
            || !TryReadBoundedString(element, "moduleName", required: false, 1024, out var moduleName)
            || !TryReadBoundedString(element, "modulePath", required: false, 4096, out var modulePath))
            return false;

        Guid? moduleVersionId = null;
        if (element.TryGetProperty("moduleVersionId", out var mvidElement)
            && mvidElement.ValueKind != JsonValueKind.Null)
        {
            if (mvidElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(mvidElement.GetString(), out var parsedMvid))
                return false;
            moduleVersionId = parsedMvid;
        }

        int? metadataToken = null;
        if (element.TryGetProperty("metadataToken", out var tokenElement)
            && tokenElement.ValueKind != JsonValueKind.Null)
        {
            if (tokenElement.ValueKind != JsonValueKind.Number
                || !tokenElement.TryGetInt32(out var parsedToken)
                || parsedToken <= 0)
                return false;
            metadataToken = parsedToken;
        }

        identity = new TypeIdentity(typeFullName!)
        {
            ModuleName = moduleName,
            ModulePath = modulePath,
            ModuleVersionId = moduleVersionId,
            MetadataToken = metadataToken,
        };
        return true;
    }

    internal static string CreateTypeInstanceCursor(
        string handle,
        TypeIdentity typeIdentity,
        HeapSnapshotArtifact snapshot,
        ulong afterAddress)
    {
        var fingerprint = TypeInstanceCursorFingerprint(handle, typeIdentity, snapshot);
        return $"v1.{afterAddress:x16}.{fingerprint}";
    }

    internal static bool TryParseTypeInstanceCursor(
        string? cursor,
        string handle,
        TypeIdentity typeIdentity,
        HeapSnapshotArtifact snapshot,
        out ulong afterAddress)
    {
        afterAddress = 0;
        if (cursor is null)
            return true;
        var parts = cursor.Split('.', StringSplitOptions.None);
        return parts.Length == 3
            && parts[0] == "v1"
            && parts[1].Length == 16
            && ulong.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out afterAddress)
            && afterAddress != 0
            && string.Equals(parts[2], TypeInstanceCursorFingerprint(handle, typeIdentity, snapshot), StringComparison.Ordinal);
    }

    internal static bool TryReadArrayRange(JsonElement root, string view, out HeapObjectInspectionOptions? options)
    {
        options = null;
        var hasStart = root.TryGetProperty("arrayStartIndex", out var start);
        var hasCount = root.TryGetProperty("arrayCount", out var count);
        if (!hasStart && !hasCount) return true;
        if (!string.Equals(view.Trim(), "object", StringComparison.OrdinalIgnoreCase)) return false;
        var startIndex = 0;
        var requestedCount = HeapObjectInspectionOptions.MaxArrayCount;
        if (hasStart && (start.ValueKind != JsonValueKind.Number || !start.TryGetInt32(out startIndex) || startIndex < 0))
            return false;
        if (hasCount && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out requestedCount)
            || requestedCount < 1 || requestedCount > HeapObjectInspectionOptions.MaxArrayCount))
            return false;
        options = new HeapObjectInspectionOptions(startIndex, requestedCount);
        return true;
    }

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

    internal static object ProjectTypeInstance(HeapTypeInstance instance) => new
    {
        address = FormatAddress(instance.Address),
        instance.Size,
        instance.SegmentKind,
        instance.Generation,
    };

    /// <summary>
    /// Keeps only the static fields whose referenced object is exactly <paramref name="address"/> (a
    /// projection of the already-captured list; nothing is re-read from the target).
    /// </summary>
    internal static HeapSnapshotArtifact FilterStaticFields(HeapSnapshotArtifact snapshot, ulong address) =>
        snapshot.StaticFields is null
            ? snapshot
            : snapshot with { StaticFields = [.. snapshot.StaticFields.Where(field => field.ValueAddress == address)] };

    /// <summary>
    /// Safety request for an <c>object</c>/<c>gcroot</c> read. A dump origin is re-read offline (no ptrace
    /// attach, no suspension) and carries the dump-source heap profile (Moderate/Warn); only a live origin
    /// reopens the target and keeps the generic query profile (High/Acknowledge).
    /// </summary>
    internal static CliOptions CreateObjectReadSafetyOptions(
        HeapSnapshotOrigin origin, string handle, string view, string? acknowledgeRisk) =>
        origin == HeapSnapshotOrigin.Dump
            ? new CliOptions { Command = "inspect-heap", Sources = [DiagnosticOperationCatalog.HeapSources.Dump], AcknowledgeRisk = acknowledgeRisk }
            : new CliOptions { Command = "query", Handle = handle, View = view, AcknowledgeRisk = acknowledgeRisk };

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
            fields = element.Fields?.Select(ProjectField),
        }),
        arrayStartIndex = inspection.ArrayStartIndex,
        arrayRequestedCount = inspection.ArrayRequestedCount,
        isString = inspection.IsString,
        stringValue = inspection.StringValue,
        stringValueTruncated = inspection.StringValueTruncated,
        fields = inspection.Fields?.Select(ProjectField),
        warnings = inspection.Warnings,
    };

    private static object ProjectField(HeapObjectField field) => new
    {
        name = field.Name,
        typeFullName = field.TypeFullName,
        value = field.Value,
        objectAddress = field.ObjectAddress is { } address ? FormatAddress(address) : null,
        referencedTypeFullName = field.ReferencedTypeFullName,
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

    private static bool TryReadBoundedString(
        JsonElement root,
        string propertyName,
        bool required,
        int maxLength,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return !required;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return value is not null
            && (!required || !string.IsNullOrWhiteSpace(value))
            && value.Length <= maxLength
            && !value.Any(char.IsControl);
    }

    private static string TypeInstanceCursorFingerprint(
        string handle,
        TypeIdentity typeIdentity,
        HeapSnapshotArtifact snapshot)
    {
        var payload = string.Join('\n',
            handle,
            snapshot.Origin,
            snapshot.ProcessId.ToString(CultureInfo.InvariantCulture),
            snapshot.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            typeIdentity.TypeFullName,
            typeIdentity.ModuleName ?? string.Empty,
            typeIdentity.ModulePath ?? string.Empty,
            typeIdentity.ModuleVersionId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty,
            typeIdentity.MetadataToken?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)).AsSpan(0, 12));
    }
}
