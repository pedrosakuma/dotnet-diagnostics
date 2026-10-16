namespace DotnetDiagnostics.Core.Dump;

/// <summary>Detailed inspection of one managed object captured via a heap snapshot handle.</summary>
public sealed record HeapObjectInspection(
    ulong Address,
    string TypeFullName,
    long Size,
    string SegmentKind,
    string Generation)
{
    public bool IsArray { get; init; }
    public int? ArrayLength { get; init; }
    public IReadOnlyList<HeapArrayElement>? ArraySample { get; init; }
    public int? ArrayStartIndex { get; init; }
    public int? ArrayRequestedCount { get; init; }
    public bool IsString { get; init; }
    public string? StringValue { get; init; }
    public bool StringValueTruncated { get; init; }
    public IReadOnlyList<HeapObjectField>? Fields { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>One field row from <see cref="HeapObjectInspection"/>.</summary>
public sealed record HeapObjectField(string Name, string TypeFullName, string Value)
{
    public ulong? ObjectAddress { get; init; }
    public string? ReferencedTypeFullName { get; init; }
}

/// <summary>One sampled array element from <see cref="HeapObjectInspection"/>.</summary>
public sealed record HeapArrayElement(int Index, string TypeFullName, string Value)
{
    public ulong? ObjectAddress { get; init; }
    public string? ReferencedTypeFullName { get; init; }
    /// <summary>Flattened value-type fields, with dotted paths for nested structs. No interior addresses are object references.</summary>
    public IReadOnlyList<HeapObjectField>? Fields { get; init; }
}

/// <summary>A bounded, zero-based linear range of managed array elements to inspect.</summary>
public sealed record HeapObjectInspectionOptions(int ArrayStartIndex = 0, int ArrayCount = 8)
{
    public const int MaxArrayCount = 8;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ArrayStartIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(ArrayCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ArrayCount, MaxArrayCount);
    }
}

/// <summary>Shortest GC-root chain currently found for a target object.</summary>
public sealed record HeapGcRootInspection(
    ulong Address,
    string TypeFullName,
    IReadOnlyList<RetentionFrame> Chain,
    bool Truncated)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Transitive closure size rooted at one managed object.</summary>
public sealed record HeapObjectSizeInspection(
    ulong Address,
    string TypeFullName,
    long RetainedBytes,
    int ObjectCount,
    bool Truncated)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}
