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

/// <summary>One managed object discovered by a bounded exact-type heap enumeration.</summary>
public sealed record HeapTypeInstance(
    ulong Address,
    long Size,
    string SegmentKind,
    string Generation);

/// <summary>Bounds and continuation point for one exact-type heap enumeration page.</summary>
public sealed record HeapTypeInstanceQueryOptions(
    int PageSize = 50,
    int MaxScannedObjects = 250_000,
    ulong AfterAddress = 0)
{
    public const int MaxPageSize = 100;
    public const int MaxObjectScanCount = 250_000;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PageSize, MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxScannedObjects, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxScannedObjects, MaxObjectScanCount);
    }
}

/// <summary>
/// One bounded page of instances for an exact managed type. <see cref="NextAfterAddress"/> is an
/// internal continuation point; hosts should bind it to the owning handle and type before exposing
/// it as a cursor.
/// </summary>
public sealed record HeapTypeInstancesInspection(
    TypeIdentity TypeIdentity,
    IReadOnlyList<HeapTypeInstance> Instances,
    int PageSize,
    int ScannedObjects,
    bool Complete,
    ulong? NextAfterAddress,
    DateTimeOffset EnumeratedAt,
    string Consistency)
{
    public IReadOnlyList<string>? Warnings { get; init; }
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
