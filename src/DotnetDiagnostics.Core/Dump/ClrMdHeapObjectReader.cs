using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdHeapObjectReader
{
    public static bool? TryReadBoolField(ClrObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var field = FindFieldByName(obj.Type, name);
            if (field is null || !field.IsPrimitive || field.ElementType != ClrElementType.Boolean)
            {
                continue;
            }

            try
            {
                return field.Read<bool>(obj.Address, interior: false);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static IEnumerable<ClrInstanceField> EnumerateInstanceFields(ClrType? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.Fields)
            {
                yield return field;
            }
        }
    }

    public static ClrInstanceField? FindFieldByName(ClrType? type, string fieldName)
        => EnumerateInstanceFields(type).FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));
}
