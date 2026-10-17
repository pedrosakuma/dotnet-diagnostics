using System.Globalization;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdHeapDrilldownService
{
    private const int MaxStringPreviewLength = 256;
    private const int MaxFieldDepth = 3;
    private const int MaxFieldCount = 256;
    private const int GcRootDepthLimit = 64;
    private const int MaxRetainedGraphObjects = 250_000;

    public static HeapObjectInspection InspectObject(ClrRuntime runtime, ulong address, HeapObjectInspectionOptions? options = null)
    {
        var rangeRequested = options is not null;
        options ??= new HeapObjectInspectionOptions();
        options.Validate();
        var obj = GetRequiredObject(runtime, address);
        if (!obj.IsArray && rangeRequested)
            throw new ArgumentException("Array ranges require an array object.", nameof(options));
        var segment = runtime.Heap.GetSegmentByAddress(address)
            ?? throw new InvalidOperationException($"No GC segment contains object 0x{address:x}.");
        var warnings = new List<string>();

        var inspection = new HeapObjectInspection(
            Address: obj.Address,
            TypeFullName: obj.Type!.Name ?? "<unknown>",
            Size: (long)obj.Size,
            SegmentKind: segment.Kind.ToString(),
            Generation: segment.GetGeneration(obj.Address).ToString());

        if (obj.Type.IsString)
        {
            var value = obj.AsString(MaxStringPreviewLength);
            inspection = inspection with
            {
                IsString = true,
                StringValue = value,
                StringValueTruncated = value is not null && value.Length >= MaxStringPreviewLength,
            };
        }

        if (obj.IsArray)
        {
            inspection = inspection with
            {
                IsArray = true,
                ArrayLength = obj.AsArray().Length,
                ArrayStartIndex = options.ArrayStartIndex,
                ArrayRequestedCount = options.ArrayCount,
                ArraySample = SampleArray(obj, options, warnings),
            };
        }
        else
        {
            inspection = inspection with
            {
                Fields = ReadObjectFields(obj, warnings),
            };
        }

        return warnings.Count > 0
            ? inspection with { Warnings = warnings }
            : inspection;
    }

    public static HeapGcRootInspection InspectGcRoot(ClrRuntime runtime, ulong address)
    {
        var obj = GetRequiredObject(runtime, address);
        var target = new HashSet<ulong> { address };
        var warnings = new List<string>();
        var skippedStackRoots = ClrMdRootEnumerator.SkipsStackRoots(runtime);
        if (skippedStackRoots)
        {
            warnings.Add(ClrMdRootEnumerator.CreateSkippedStackRootsWarning(
                "This GC-root chain excludes stack roots; a returned chain is limited to the remaining handle, static, and finalizer roots."));
        }

        var rootByObject = ClrMdRetentionAnalyzer.BuildRootByObjectMap(runtime, target, GcRootDepthLimit, MaxRetainedGraphObjects, out var bfsCapHit, CancellationToken.None);
        if (bfsCapHit)
        {
            warnings.Add($"GC-root BFS hit its safety cap before exhausting the search space for 0x{address:x}; chain may be truncated.");
        }

        var reachedByBfs = rootByObject.ContainsKey(address);
        var chain = ClrMdRetentionAnalyzer.BuildTypedRootChain(runtime, address, rootByObject, GcRootDepthLimit, out var truncated);
        if (!reachedByBfs || chain.Count == 0 || chain[0].RootKind is null)
        {
            var omission = skippedStackRoots
                ? " Stack roots were not enumerated, so a live GC-root path may have been omitted."
                : string.Empty;
            throw new InvalidOperationException($"No GC root path could be found for object 0x{address:x}.{omission} If this came from a live snapshot, the object may have moved or been collected since capture.");
        }

        return new HeapGcRootInspection(
            Address: obj.Address,
            TypeFullName: obj.Type!.Name ?? "<unknown>",
            Chain: chain,
            Truncated: truncated || bfsCapHit)
        {
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    public static HeapObjectSizeInspection InspectObjectSize(ClrRuntime runtime, ulong address)
    {
        var obj = GetRequiredObject(runtime, address);
        var visited = new HashSet<ulong>();
        var queue = new Queue<ClrObject>();
        queue.Enqueue(obj);
        long retainedBytes = 0;
        var truncated = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.IsNull || !current.IsValid || current.Type is null) continue;
            if (!visited.Add(current.Address)) continue;

            retainedBytes += (long)current.Size;
            if (visited.Count >= MaxRetainedGraphObjects)
            {
                truncated = true;
                break;
            }

            foreach (var child in current.EnumerateReferences(carefully: true, considerDependantHandles: true))
            {
                if (!child.IsNull && child.IsValid && child.Type is not null && !visited.Contains(child.Address))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return new HeapObjectSizeInspection(
            Address: obj.Address,
            TypeFullName: obj.Type!.Name ?? "<unknown>",
            RetainedBytes: retainedBytes,
            ObjectCount: visited.Count,
            Truncated: truncated)
        {
            Warnings = truncated
                ? [$"Object graph walk hit its safety cap of {MaxRetainedGraphObjects:N0} objects; retained bytes are a lower bound."]
                : null,
        };
    }

    private static ClrObject GetRequiredObject(ClrRuntime runtime, ulong address)
    {
        if (address == 0)
        {
            throw new ArgumentException("Object address must be a non-zero managed object reference.", nameof(address));
        }

        var obj = runtime.Heap.GetObject(address);
        if (obj.IsNull || !obj.IsValid || obj.Type is null || obj.IsFree)
        {
            throw new InvalidOperationException($"No valid managed object exists at address 0x{address:x}. If this came from a live snapshot, the object may have moved or been collected since capture.");
        }

        return obj;
    }

    private static List<HeapObjectField> ReadObjectFields(ClrObject obj, List<string> warnings)
    {
        var fields = new List<HeapObjectField>();
        AppendFields(obj.Address, interior: false, obj.Type!, prefix: null, depth: 0, fields, warnings);
        return fields;
    }

    private static void AppendFields(
        ulong address,
        bool interior,
        ClrType type,
        string? prefix,
        int depth,
        List<HeapObjectField> fields,
        List<string> warnings,
        int fieldLimit = MaxFieldCount)
    {
        if (depth > MaxFieldDepth || fields.Count >= fieldLimit) return;

        foreach (var field in type.Fields)
        {
            if (fields.Count >= fieldLimit)
            {
                warnings.Add($"Field expansion hit MaxFieldCount={MaxFieldCount} (remaining range budget {fieldLimit}); additional fields are omitted.");
                return;
            }

            var baseName = field.Name ?? $"<offset+0x{field.Offset:x}>";
            var fieldName = string.IsNullOrEmpty(prefix) ? baseName : $"{prefix}.{baseName}";

            try
            {
                if (field.IsObjectReference)
                {
                    var reference = field.ReadObject(address, interior);
                    fields.Add(FormatReferenceField(fieldName, field.Type, reference));
                    continue;
                }

                if (field.IsPrimitive)
                {
                    fields.Add(new HeapObjectField(fieldName, field.Type?.Name ?? field.ElementType.ToString(), ReadPrimitive(field, address, interior)));
                    continue;
                }

                if (field.IsValueType && field.Type is not null)
                {
                    var structValue = field.ReadStruct(address, interior);
                    if (!structValue.IsValid || structValue.Type is null)
                    {
                        fields.Add(new HeapObjectField(fieldName, field.Type.Name ?? "<value-type>", "<invalid value-type>"));
                        continue;
                    }

                    if (depth >= MaxFieldDepth || structValue.Type.Fields.Length == 0)
                    {
                        if (depth >= MaxFieldDepth && structValue.Type.Fields.Length > 0)
                            warnings.Add($"Field '{fieldName}' reached MaxFieldDepth={MaxFieldDepth}; nested fields are omitted.");
                        fields.Add(new HeapObjectField(fieldName, structValue.Type.Name ?? "<value-type>", $"<value-type size={structValue.Size}>"));
                        continue;
                    }

                    AppendFields(structValue.Address, interior: true, structValue.Type, fieldName, depth + 1, fields, warnings, fieldLimit);
                    continue;
                }

                fields.Add(new HeapObjectField(fieldName, field.Type?.Name ?? field.ElementType.ToString(), $"<{field.ElementType}>"));
            }
            catch (Exception ex)
            {
                warnings.Add($"Reading field '{fieldName}' failed: {ex.GetType().Name} ({ex.Message}).");
            }
        }
    }

    private static HeapObjectField FormatReferenceField(string name, ClrType? declaredType, ClrObject reference)
    {
        if (reference.IsNull || !reference.IsValid || reference.Type is null)
        {
            return new HeapObjectField(name, declaredType?.Name ?? "<object>", "null");
        }

        if (reference.Type.IsString)
        {
            var preview = reference.AsString(MaxStringPreviewLength);
            var value = preview is null ? $"0x{reference.Address:x}" : $"0x{reference.Address:x} \"{preview}\"";
            return new HeapObjectField(name, declaredType?.Name ?? reference.Type.Name ?? "System.String", value)
            {
                ObjectAddress = reference.Address,
                ReferencedTypeFullName = reference.Type.Name,
            };
        }

        return new HeapObjectField(name, declaredType?.Name ?? reference.Type.Name ?? "<object>", $"0x{reference.Address:x}")
        {
            ObjectAddress = reference.Address,
            ReferencedTypeFullName = reference.Type.Name,
        };
    }

    private static List<HeapArrayElement> SampleArray(ClrObject obj, HeapObjectInspectionOptions options, List<string> warnings)
    {
        var array = obj.AsArray();
        if (options.ArrayStartIndex >= array.Length && (options.ArrayStartIndex != 0 || array.Length != 0))
            throw new ArgumentOutOfRangeException(nameof(options), "ArrayStartIndex must be below the current array length (or zero for an empty array).");
        var count = Math.Min(array.Length - options.ArrayStartIndex, options.ArrayCount);
        var elementType = array.Type.ComponentType;
        if (array.Rank is < 1 or > 32)
            throw new InvalidOperationException("Array rank must be between 1 and 32.");
        var elements = new List<HeapArrayElement>(count);
        var remainingFields = MaxFieldCount;

        for (var offset = 0; offset < count; offset++)
        {
            var i = options.ArrayStartIndex + offset;
            try
            {
                elements.Add(ReadArrayElement(array, elementType, i, warnings, ref remainingFields));
            }
            catch (Exception ex)
            {
                warnings.Add($"Reading array element [{i}] failed: {ex.GetType().Name} ({ex.Message}).");
            }
        }

        if (array.Length > count)
            warnings.Add($"Array range [{options.ArrayStartIndex}, {options.ArrayStartIndex + count}) inspected with MaxArrayCount={HeapObjectInspectionOptions.MaxArrayCount}; {array.Length - count} elements outside this range are not inspected.");
        return elements;
    }

    private static HeapArrayElement ReadArrayElement(ClrArray array, ClrType? elementType, int index, List<string> warnings, ref int remainingFields)
    {
        var typeName = elementType?.Name ?? "<unknown>";
        var indices = GetArrayIndices(array, index);
        if (elementType?.IsObjectReference == true || elementType?.IsString == true || elementType?.IsArray == true)
        {
            var value = array.GetObjectValue(indices);
            if (value.IsNull || !value.IsValid || value.Type is null)
            {
                return new HeapArrayElement(index, typeName, "null");
            }

            if (value.Type.IsString)
            {
                var preview = value.AsString(MaxStringPreviewLength);
                return new HeapArrayElement(index, typeName, preview is null ? $"0x{value.Address:x}" : $"0x{value.Address:x} \"{preview}\"")
                {
                    ObjectAddress = value.Address,
                    ReferencedTypeFullName = value.Type.Name,
                };
            }

            return new HeapArrayElement(index, typeName, $"0x{value.Address:x}")
            {
                ObjectAddress = value.Address,
                ReferencedTypeFullName = value.Type.Name,
            };
        }

        if (elementType?.IsValueType == true && !elementType.IsPrimitive)
        {
            var structValue = array.GetStructValue(indices);
            var fields = new List<HeapObjectField>();
            if (structValue.IsValid && structValue.Type is not null)
            {
                if (remainingFields > 0)
                {
                    AppendFields(structValue.Address, interior: true, structValue.Type, prefix: null, depth: 0,
                        fields, warnings, remainingFields);
                    remainingFields -= fields.Count;
                }
                else
                    warnings.Add($"Array element [{index}] fields omitted: MaxFieldCount={MaxFieldCount} shared across this array range.");
            }
            else
                warnings.Add($"Array element [{index}] has no valid value-type layout; its fields could not be inspected.");
            return new HeapArrayElement(index, typeName, structValue.IsValid ? $"<value-type size={structValue.Size}>" : "<invalid value-type>")
            {
                Fields = fields,
            };
        }

        return new HeapArrayElement(index, typeName, ReadPrimitiveArrayValue(array, elementType, indices));
    }

    private static int[] GetArrayIndices(ClrArray array, int linearIndex)
    {
        var indices = new int[array.Rank];
        for (var dimension = indices.Length - 1; dimension >= 0; dimension--)
        {
            var length = array.GetLength(dimension);
            if (length <= 0) throw new InvalidOperationException("An array element cannot have an empty dimension.");
            indices[dimension] = checked(array.GetLowerBound(dimension) + linearIndex % length);
            linearIndex /= length;
        }
        return indices;
    }

    private static string ReadPrimitiveArrayValue(ClrArray array, ClrType? elementType, int[] index) => elementType?.ElementType switch
    {
        ClrElementType.Boolean => array.GetValue<bool>(index) ? "true" : "false",
        ClrElementType.Char => $"'{array.GetValue<char>(index)}'",
        ClrElementType.Int8 => array.GetValue<sbyte>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => array.GetValue<byte>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => array.GetValue<short>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => array.GetValue<ushort>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => array.GetValue<int>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => array.GetValue<uint>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => array.GetValue<long>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => array.GetValue<ulong>(index).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => ((long)array.GetValue<nint>(index)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeUInt => ((ulong)array.GetValue<nuint>(index)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => array.GetValue<float>(index).ToString("R", CultureInfo.InvariantCulture),
        ClrElementType.Double => array.GetValue<double>(index).ToString("R", CultureInfo.InvariantCulture),
        _ => $"<{elementType?.ElementType.ToString() ?? "Unknown"}>",
    };

    private static string ReadPrimitive(ClrInstanceField field, ulong address, bool interior) => field.ElementType switch
    {
        ClrElementType.Boolean => field.Read<bool>(address, interior) ? "true" : "false",
        ClrElementType.Char => $"'{field.Read<char>(address, interior)}'",
        ClrElementType.Int8 => field.Read<sbyte>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => field.Read<byte>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => field.Read<short>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => field.Read<ushort>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => field.Read<int>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => field.Read<uint>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => field.Read<long>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => field.Read<ulong>(address, interior).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => ((long)field.Read<nint>(address, interior)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeUInt => ((ulong)field.Read<nuint>(address, interior)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => field.Read<float>(address, interior).ToString("R", CultureInfo.InvariantCulture),
        ClrElementType.Double => field.Read<double>(address, interior).ToString("R", CultureInfo.InvariantCulture),
        _ => $"<{field.ElementType}>",
    };
}
