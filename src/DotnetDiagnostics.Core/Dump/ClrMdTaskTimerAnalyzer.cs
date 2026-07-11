using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

internal static class ClrMdTaskTimerAnalyzer
{
    public static void Aggregate(ClrObject obj, long objSize, RawTaskTimerAggregation sink)
    {
        var type = obj.Type;
        if (type is null)
        {
            return;
        }

        var typeName = type.Name ?? "<unknown>";
        if (IsTimerContainerType(typeName))
        {
            var timerObject = TryResolveScheduledTimer(obj);
            if (timerObject is null)
            {
                return;
            }

            var scheduledTimer = timerObject.Value;
            if (!sink.SeenTimerAddresses.Add(scheduledTimer.Address))
            {
                return;
            }

            var timerTypeName = scheduledTimer.Type?.Name ?? typeName;
            sink.TotalTimers++;
            var callback = TryReadTimerCallback(scheduledTimer);
            var isCanceled = ClrMdHeapObjectReader.TryReadBoolField(scheduledTimer, "_canceled", "m_canceled", "_disposed");
            var key = new TimerCallbackKey(
                TimerTypeFullName: timerTypeName,
                CallbackTargetTypeFullName: callback?.TargetTypeFullName,
                DeclaringTypeFullName: callback?.DeclaringTypeFullName,
                MethodName: callback?.MethodName,
                MethodSignature: callback?.MethodSignature,
                ModulePath: callback?.ModulePath,
                MetadataToken: callback?.MetadataToken ?? 0,
                IsCanceled: isCanceled);

            if (!sink.TimersByCallback.TryGetValue(key, out var timer))
            {
                timer = new RawTimerCallbackStat(key, callback?.Method);
                sink.TimersByCallback[key] = timer;
            }

            timer.Count++;
        }

        if (IsTaskType(type))
        {
            sink.TotalTasks++;
            AggregateTaskType(type, objSize, sink.TasksByType);
        }

        if (typeName.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal))
        {
            sink.TotalTaskCompletionSources++;
            AggregateTaskType(type, objSize, sink.TaskCompletionSourcesByType);
        }
    }

    public static TaskTimerLeakView BuildView(
        RawTaskTimerAggregation agg,
        int topN,
        Func<ClrType?, TypeIdentity?> buildTypeIdentity,
        Func<string?, Guid?> tryReadMvid)
    {
        var timerRows = agg.TimersByCallback.Values
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Key.TimerTypeFullName, StringComparer.Ordinal)
            .Take(topN)
            .Select(t =>
            {
                Memory.MethodIdentity? method = null;
                if (t.Method is not null && t.Key.MetadataToken != 0)
                {
                    method = new Memory.MethodIdentity(
                        ModuleName: t.Key.ModulePath is { } mp ? Path.GetFileName(mp) : null,
                        ModulePath: t.Key.ModulePath,
                        ModuleVersionId: tryReadMvid(t.Key.ModulePath),
                        MetadataToken: t.Key.MetadataToken,
                        TypeFullName: t.Key.DeclaringTypeFullName ?? "<unknown>",
                        MethodName: t.Key.MethodName ?? "<unknown>",
                        GenericArity: 0);
                }

                return new TimerCallbackStat(
                    TimerTypeFullName: t.Key.TimerTypeFullName,
                    CallbackTargetTypeFullName: t.Key.CallbackTargetTypeFullName,
                    DeclaringTypeFullName: t.Key.DeclaringTypeFullName,
                    MethodName: t.Key.MethodName,
                    MethodSignature: t.Key.MethodSignature,
                    Count: t.Count)
                {
                    IsCanceled = t.Key.IsCanceled,
                    Method = method,
                };
            })
            .ToArray();

        var notes = new List<string>();
        if (agg.TotalTimers > timerRows.Sum(row => row.Count))
        {
            notes.Add("Timer callback rows are truncated to the top groups retained in the snapshot.");
        }

        return new TaskTimerLeakView(
            TotalTimers: agg.TotalTimers,
            TotalTasks: agg.TotalTasks,
            TotalTaskCompletionSources: agg.TotalTaskCompletionSources,
            TimersByCallback: timerRows,
            TasksByType: BuildTaskTypeStats(agg.TasksByType, topN, buildTypeIdentity),
            TaskCompletionSourcesByType: BuildTaskTypeStats(agg.TaskCompletionSourcesByType, topN, buildTypeIdentity),
            Notes: notes);
    }

    private static TaskTypeStat[] BuildTaskTypeStats(
        Dictionary<TypeKey, RawTypeStat> agg,
        int topN,
        Func<ClrType?, TypeIdentity?> buildTypeIdentity)
        => agg.Values
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => t.Bytes)
            .Take(topN)
            .Select(t => new TaskTypeStat(
                TypeFullName: t.TypeName,
                ModuleName: t.ModuleName is { } mn ? Path.GetFileName(mn) : null,
                Count: t.Count,
                TotalBytes: t.Bytes)
            {
                Identity = buildTypeIdentity(t.ClrType),
            })
            .ToArray();

    private static bool IsTimerContainerType(string typeName)
        => typeName.Equals("System.Threading.Timer", StringComparison.Ordinal) ||
           typeName.StartsWith("System.Threading.TimerQueue", StringComparison.Ordinal) ||
           typeName.StartsWith("System.Threading.TimerHolder", StringComparison.Ordinal);

    private static bool IsScheduledTimerType(string typeName)
        => typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal);

    private static bool IsTaskType(ClrType type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, "System.Threading.Tasks.Task", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AggregateTaskType(ClrType type, long objSize, Dictionary<TypeKey, RawTypeStat> sink)
    {
        var key = new TypeKey(type.Name ?? "<unknown>", type.Module?.Name);
        if (!sink.TryGetValue(key, out var stat))
        {
            stat = new RawTypeStat(key.TypeName, key.ModuleName, type);
            sink[key] = stat;
        }

        stat.Count++;
        stat.Bytes += objSize;
    }

    private static RawTimerCallback? TryReadTimerCallback(ClrObject timer)
        => TryReadTimerCallback(timer, depth: 0);

    private static RawTimerCallback? TryReadTimerCallback(ClrObject timer, int depth)
    {
        if (timer.IsNull || !timer.IsValid || timer.Type is null || depth > 2)
        {
            return null;
        }

        foreach (var field in ClrMdHeapObjectReader.EnumerateInstanceFields(timer.Type))
        {
            if (!field.IsObjectReference)
            {
                continue;
            }

            ClrObject value;
            try
            {
                value = field.ReadObject(timer.Address, interior: false);
            }
            catch
            {
                continue;
            }

            if (value.IsNull || !value.IsValid || value.Type is null)
            {
                continue;
            }

            if (value.IsDelegate)
            {
                var callback = TryBuildTimerCallback(value);
                if (callback is not null)
                {
                    return callback;
                }
            }

            var nestedTypeName = value.Type.Name;
            if (nestedTypeName is not null && IsTimerContainerType(nestedTypeName))
            {
                var nested = TryReadTimerCallback(value, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static ClrObject? TryResolveScheduledTimer(ClrObject timer)
        => TryResolveScheduledTimer(timer, depth: 0);

    private static ClrObject? TryResolveScheduledTimer(ClrObject timer, int depth)
    {
        if (timer.IsNull || !timer.IsValid || timer.Type is null || depth > 2)
        {
            return null;
        }

        var typeName = timer.Type.Name;
        if (typeName is not null && IsScheduledTimerType(typeName))
        {
            return timer;
        }

        foreach (var field in ClrMdHeapObjectReader.EnumerateInstanceFields(timer.Type))
        {
            if (!field.IsObjectReference)
            {
                continue;
            }

            ClrObject value;
            try
            {
                value = field.ReadObject(timer.Address, interior: false);
            }
            catch
            {
                continue;
            }

            if (value.IsNull || !value.IsValid || value.Type is null)
            {
                continue;
            }

            var nestedTypeName = value.Type.Name;
            if (nestedTypeName is null || !IsTimerContainerType(nestedTypeName))
            {
                continue;
            }

            var nested = TryResolveScheduledTimer(value, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static RawTimerCallback? TryBuildTimerCallback(ClrObject callbackDelegate)
    {
        try
        {
            var del = callbackDelegate.AsDelegate();
            foreach (var target in del.EnumerateDelegateTargets())
            {
                var method = target.Method;
                if (method is null)
                {
                    continue;
                }

                var targetObj = target.TargetObject;
                return new RawTimerCallback(
                    TargetTypeFullName: targetObj.IsNull ? null : targetObj.Type?.Name,
                    DeclaringTypeFullName: method.Type?.Name ?? "<unknown>",
                    MethodName: method.Name ?? "<unknown>",
                    MethodSignature: method.Signature,
                    ModulePath: method.Type?.Module?.Name,
                    MetadataToken: (int)method.MetadataToken,
                    Method: method);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    internal sealed class RawTaskTimerAggregation
    {
        public long TotalTimers;
        public long TotalTasks;
        public long TotalTaskCompletionSources;
        internal HashSet<ulong> SeenTimerAddresses { get; } = new();
        internal Dictionary<TimerCallbackKey, RawTimerCallbackStat> TimersByCallback { get; } = new();
        internal Dictionary<TypeKey, RawTypeStat> TasksByType { get; } = new();
        internal Dictionary<TypeKey, RawTypeStat> TaskCompletionSourcesByType { get; } = new();
    }

    internal readonly record struct TypeKey(string TypeName, string? ModuleName);

    internal sealed class RawTypeStat
    {
        public RawTypeStat(string typeName, string? moduleName, ClrType clrType)
        {
            TypeName = typeName;
            ModuleName = moduleName;
            ClrType = clrType;
        }

        public string TypeName { get; }
        public string? ModuleName { get; }
        public ClrType ClrType { get; }
        public long Count;
        public long Bytes;
    }

    internal readonly record struct TimerCallbackKey(
        string TimerTypeFullName,
        string? CallbackTargetTypeFullName,
        string? DeclaringTypeFullName,
        string? MethodName,
        string? MethodSignature,
        string? ModulePath,
        int MetadataToken,
        bool? IsCanceled);

    internal sealed class RawTimerCallbackStat
    {
        public RawTimerCallbackStat(TimerCallbackKey key, ClrMethod? method)
        {
            Key = key;
            Method = method;
        }

        public TimerCallbackKey Key { get; }
        public ClrMethod? Method { get; }
        public long Count;
    }

    private sealed record RawTimerCallback(
        string? TargetTypeFullName,
        string DeclaringTypeFullName,
        string MethodName,
        string? MethodSignature,
        string? ModulePath,
        int MetadataToken,
        ClrMethod Method);
}
