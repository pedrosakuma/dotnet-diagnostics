using System.Globalization;
using DotnetDiagnostics.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// ClrMD-backed <see cref="IThreadStaticFieldResolver"/>. Mirrors
/// <see cref="ClrMdFrameVariableResolver"/>'s re-open strategy: a dump-origin snapshot reloads the
/// dump (time-consistent), a live-origin snapshot re-attaches (best-effort — the process may have
/// moved on). <see cref="ClrType.ThreadStaticFields"/> and <see cref="ClrThreadStaticField"/>'s
/// per-thread <c>IsInitialized</c>/<c>Read&lt;T&gt;</c>/<c>ReadObject</c>/<c>ReadStruct</c>/
/// <c>ReadString</c> overloads are the ClrMD 4.x surface this resolver drives.
/// </summary>
public sealed class ClrMdThreadStaticFieldResolver : IThreadStaticFieldResolver
{
    private const int MaxStringPreviewLength = 256;

    public Task<ThreadStaticFieldsResult> ResolveAsync(
        ThreadSnapshotArtifact artifact,
        string typeFullName,
        bool includeSensitiveValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeFullName);
        return Task.Run(() => Resolve(artifact, typeFullName, includeSensitiveValues, cancellationToken), cancellationToken);
    }

    private static ThreadStaticFieldsResult Resolve(
        ThreadSnapshotArtifact artifact,
        string typeFullName,
        bool includeSensitiveValues,
        CancellationToken ct)
    {
        using var target = OpenTarget(artifact);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("No CLR runtime present in the origin; thread-static fields require a managed runtime.");
        using var runtime = clrInfo.CreateRuntime();

        var type = runtime.Heap.GetTypeByName(typeFullName)
            ?? throw new InvalidOperationException($"Type '{typeFullName}' was not found among the loaded types in the origin.");

        var warnings = new List<string>();
        var threads = new List<ThreadStaticFieldsForThread>();
        var missingThreadCount = 0;

        foreach (var snapshotThread in artifact.Threads)
        {
            ct.ThrowIfCancellationRequested();
            var thread = runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == snapshotThread.ManagedThreadId);
            if (thread is null)
            {
                // Live-origin only: the thread captured in the snapshot has since exited. Dump
                // origin is time-consistent, so this should never happen for a dump.
                missingThreadCount++;
                continue;
            }

            var fields = new List<ThreadStaticFieldValue>(type.ThreadStaticFields.Length);
            foreach (var field in type.ThreadStaticFields)
            {
                ct.ThrowIfCancellationRequested();
                fields.Add(ToFieldValue(field, thread, includeSensitiveValues));
            }
            threads.Add(new ThreadStaticFieldsForThread(thread.ManagedThreadId, thread.OSThreadId, fields));
        }

        if (missingThreadCount > 0)
        {
            warnings.Add($"{missingThreadCount} thread(s) captured in the snapshot were no longer present in the re-opened live origin.");
        }
        if (type.ThreadStaticFields.IsDefaultOrEmpty)
        {
            warnings.Add($"Type '{typeFullName}' declares no [ThreadStatic] fields.");
        }

        return new ThreadStaticFieldsResult(type.Name ?? typeFullName, threads)
        {
            Warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    private static ThreadStaticFieldValue ToFieldValue(ClrThreadStaticField field, ClrThread thread, bool includeSensitiveValues)
    {
        var name = field.Name ?? $"<offset+0x{field.Offset:x}>";
        var typeName = field.Type?.Name ?? field.ElementType.ToString();

        bool isInitialized;
        try
        {
            isInitialized = field.IsInitialized(thread);
        }
        catch (Exception)
        {
            // Best-effort: an unreadable slot is reported as uninitialized rather than failing the
            // whole view, mirroring frame-vars' tolerance of partial ClrMD read failures.
            isInitialized = false;
        }

        if (!isInitialized)
        {
            return new ThreadStaticFieldValue(name, typeName, IsInitialized: false);
        }

        string? address = null;
        try
        {
            address = $"0x{field.GetAddress(thread):x}";
        }
        catch (Exception)
        {
            // Address is best-effort metadata; a failure here must not block the value preview.
        }

        var (preview, truncated) = includeSensitiveValues ? ReadPreview(field, thread) : (null, false);

        return new ThreadStaticFieldValue(name, typeName, IsInitialized: true)
        {
            Address = address,
            ValuePreview = preview,
            ValuePreviewTruncated = truncated,
        };
    }

    private static (string? Preview, bool Truncated) ReadPreview(ClrThreadStaticField field, ClrThread thread)
    {
        try
        {
            if (field.IsObjectReference)
            {
                var reference = field.ReadObject(thread);
                if (!reference.IsValid || reference.IsNull)
                {
                    return ("null", false);
                }
                if (reference.Type?.IsString == true)
                {
                    var value = field.ReadString(thread);
                    return Truncate(value);
                }
                return ($"0x{reference.Address:x} ({reference.Type?.Name ?? "<object>"})", false);
            }

            if (field.IsPrimitive)
            {
                return (ReadPrimitive(field, thread), false);
            }

            if (field.IsValueType)
            {
                var structValue = field.ReadStruct(thread);
                return structValue.IsValid && structValue.Type is not null
                    ? ($"<value-type {structValue.Type.Name}, size={structValue.Size}>", false)
                    : ("<invalid value-type>", false);
            }

            return ($"<{field.ElementType}>", false);
        }
        catch (Exception ex)
        {
            return ($"<unreadable: {ex.GetType().Name}>", false);
        }
    }

    private static (string? Preview, bool Truncated) Truncate(string? value)
    {
        if (value is null)
        {
            return (null, false);
        }
        return value.Length > MaxStringPreviewLength
            ? (value[..MaxStringPreviewLength], true)
            : (value, false);
    }

    private static string ReadPrimitive(ClrThreadStaticField field, ClrThread thread) => field.ElementType switch
    {
        ClrElementType.Boolean => field.Read<bool>(thread) ? "true" : "false",
        ClrElementType.Char => $"'{field.Read<char>(thread)}'",
        ClrElementType.Int8 => field.Read<sbyte>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => field.Read<byte>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => field.Read<short>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => field.Read<ushort>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => field.Read<int>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => field.Read<uint>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => field.Read<long>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => field.Read<ulong>(thread).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt => ((long)field.Read<nint>(thread)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeUInt => ((ulong)field.Read<nuint>(thread)).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => field.Read<float>(thread).ToString("R", CultureInfo.InvariantCulture),
        ClrElementType.Double => field.Read<double>(thread).ToString("R", CultureInfo.InvariantCulture),
        _ => $"<{field.ElementType}>",
    };

    private static DataTarget OpenTarget(ThreadSnapshotArtifact artifact)
    {
        if (artifact.Origin == ThreadSnapshotOrigin.Dump)
        {
            if (string.IsNullOrEmpty(artifact.DumpFilePath))
            {
                throw new InvalidOperationException("Dump-origin thread snapshot has no retained dump path; cannot inspect thread-static fields.");
            }
            return ClrMdDumpLoader.Load(artifact.DumpFilePath);
        }
        if (artifact.ProcessId <= 0)
        {
            throw new InvalidOperationException("Live-origin thread snapshot has no usable process id; cannot inspect thread-static fields.");
        }
        return DataTarget.AttachToProcess(artifact.ProcessId, suspend: true);
    }
}
