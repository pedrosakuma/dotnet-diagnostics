using System.Globalization;
using DotnetDiagnostics.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// ClrMD-backed <see cref="IFrameVariableResolver"/>. Mirrors the re-open strategy of
/// <see cref="DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver"/>: a dump-origin snapshot
/// reloads the dump (time-consistent), a live-origin snapshot re-attaches (best-effort). Frame
/// variables are recovered from <see cref="ClrThread.EnumerateStackRoots"/> attributed to each
/// frame — the closest ClrMD 3.x equivalent of <c>!clrstack -a</c>.
/// </summary>
public sealed class ClrMdFrameVariableResolver : IFrameVariableResolver
{
    private const int MaxStringPreviewLength = 256;

    public Task<FrameVariablesResult> ResolveAsync(
        ThreadSnapshotArtifact artifact,
        int managedThreadId,
        bool includeSensitiveValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Task.Run(() => Resolve(artifact, managedThreadId, includeSensitiveValues, cancellationToken), cancellationToken);
    }

    private static FrameVariablesResult Resolve(
        ThreadSnapshotArtifact artifact,
        int managedThreadId,
        bool includeSensitiveValues,
        CancellationToken ct)
    {
        using var target = OpenTarget(artifact);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("No CLR runtime present in the origin; frame locals require a managed runtime.");
        using var runtime = clrInfo.CreateRuntime();

        var thread = runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == managedThreadId)
            ?? throw new InvalidOperationException($"Managed thread {managedThreadId} not present in the origin.");

        var warnings = new List<string>();

        // Index stack roots by their owning frame's stack pointer so each variable lands on the
        // right frame. Roots whose StackFrame is null are kept in a fallback bucket.
        var rootsByFrameSp = new Dictionary<ulong, List<ClrStackRoot>>();
        var orphanRoots = new List<ClrStackRoot>();
        foreach (var root in thread.EnumerateStackRoots())
        {
            ct.ThrowIfCancellationRequested();
            var sp = root.StackFrame?.StackPointer ?? 0;
            if (sp == 0)
            {
                orphanRoots.Add(root);
                continue;
            }
            if (!rootsByFrameSp.TryGetValue(sp, out var list))
            {
                list = new List<ClrStackRoot>();
                rootsByFrameSp[sp] = list;
            }
            list.Add(root);
        }

        var frames = new List<FrameVariables>();
        var index = 0;
        foreach (var f in thread.EnumerateStackTrace())
        {
            ct.ThrowIfCancellationRequested();
            if (f.Method is null && f.Kind != ClrStackFrameKind.ManagedMethod) continue;

            var display = f.Method?.Signature ?? f.Method?.Name ?? f.FrameName ?? "<unknown>";
            var typeFqn = f.Method?.Type?.Name;
            var modulePath = f.Method?.Type?.Module?.Name;
            var moduleName = !string.IsNullOrEmpty(modulePath) ? System.IO.Path.GetFileName(modulePath) : null;

            rootsByFrameSp.TryGetValue(f.StackPointer, out var frameRoots);
            var variables = (frameRoots ?? Enumerable.Empty<ClrStackRoot>())
                .Select(r => ToVariable(r, includeSensitiveValues))
                .ToArray();

            frames.Add(new FrameVariables(
                FrameIndex: index++,
                DisplayName: display,
                TypeFullName: typeFqn,
                ModuleName: moduleName,
                InstructionPointer: $"0x{f.InstructionPointer:x}",
                StackPointer: $"0x{f.StackPointer:x}",
                Variables: variables));
        }

        if (orphanRoots.Count > 0)
        {
            warnings.Add($"{orphanRoots.Count} stack root(s) could not be attributed to a specific frame.");
        }
        if (frames.All(fr => fr.Variables.Count == 0))
        {
            warnings.Add("No object-typed locals/parameters were recoverable; value-type (struct/primitive) slots and optimized-away locals are not enumerable via ClrMD.");
        }

        return new FrameVariablesResult(thread.ManagedThreadId, thread.OSThreadId, frames)
        {
            CurrentExceptionType = thread.CurrentException?.Type?.Name,
            CurrentExceptionMessage = includeSensitiveValues ? thread.CurrentException?.Message : null,
            Warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    private static FrameVariable ToVariable(ClrStackRoot root, bool includeSensitiveValues)
    {
        var obj = root.Object;
        var location = root.RegisterName is { Length: > 0 } reg
            ? $"{reg}{(root.RegisterOffset != 0 ? "+0x" + root.RegisterOffset.ToString("x", CultureInfo.InvariantCulture) : string.Empty)}"
            : $"0x{root.Address:x}";

        string? preview = null;
        if (includeSensitiveValues && obj.IsValid && !obj.IsNull && obj.Type?.IsString == true)
        {
            try { preview = obj.AsString(MaxStringPreviewLength); }
            catch (Exception) { preview = null; }
        }

        return new FrameVariable(
            Name: null,
            TypeFullName: obj.Type?.Name,
            Address: $"0x{obj.Address:x}",
            Location: location,
            IsPinned: root.IsPinned,
            IsInterior: root.IsInterior)
        {
            ValuePreview = preview,
        };
    }

    private static DataTarget OpenTarget(ThreadSnapshotArtifact artifact)
    {
        if (artifact.Origin == ThreadSnapshotOrigin.Dump)
        {
            if (string.IsNullOrEmpty(artifact.DumpFilePath))
            {
                throw new InvalidOperationException("Dump-origin thread snapshot has no retained dump path; cannot inspect frame locals.");
            }
            return ClrMdDumpLoader.Load(artifact.DumpFilePath);
        }
        if (artifact.ProcessId <= 0)
        {
            throw new InvalidOperationException("Live-origin thread snapshot has no usable process id; cannot inspect frame locals.");
        }
        return DataTarget.AttachToProcess(artifact.ProcessId, suspend: true);
    }
}
