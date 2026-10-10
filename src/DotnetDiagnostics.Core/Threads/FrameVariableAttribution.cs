namespace DotnetDiagnostics.Core.Threads;

/// <summary>Identity of one stack frame as seen by the stack walk.</summary>
internal readonly record struct FrameIdentity(ulong StackPointer, ulong InstructionPointer, ulong MethodHandle);

internal sealed class FrameAttributionResult<T>
{
    public FrameAttributionResult(int frameCount)
    {
        PerFrame = new List<T>[frameCount];
        for (var i = 0; i < frameCount; i++) PerFrame[i] = new List<T>();
    }

    public List<T>[] PerFrame { get; }

    /// <summary>Roots whose identity matches several emitted frames; deliberately not assigned to any of them.</summary>
    public int Ambiguous { get; set; }

    /// <summary>Roots whose frame matches no emitted frame.</summary>
    public int Unmatched { get; set; }
}

/// <summary>
/// Attributes stack roots to emitted frames. Stack pointers are not unique per frame (runtime
/// transition frames and the managed frame they belong to can report the same one), so the key is
/// the full <see cref="FrameIdentity"/>. When several frames are indistinguishable the root is
/// reported as ambiguous instead of being duplicated onto each of them.
/// </summary>
internal static class FrameVariableAttribution
{
    public static FrameAttributionResult<T> Attribute<T>(
        IReadOnlyList<FrameIdentity> frames,
        IEnumerable<(FrameIdentity Frame, T Root)> roots)
    {
        var result = new FrameAttributionResult<T>(frames.Count);
        var bySp = new Dictionary<ulong, List<int>>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (!bySp.TryGetValue(frames[i].StackPointer, out var list)) bySp[frames[i].StackPointer] = list = new List<int>();
            list.Add(i);
        }

        foreach (var (frame, root) in roots)
        {
            if (!bySp.TryGetValue(frame.StackPointer, out var sameSp))
            {
                result.Unmatched++;
                continue;
            }

            var exact = -1;
            var exactCount = 0;
            foreach (var i in sameSp)
            {
                if (frames[i] != frame) continue;
                exactCount++;
                exact = i;
            }

            if (exactCount == 1) result.PerFrame[exact].Add(root);
            else if (exactCount > 1) result.Ambiguous++;
            else if (sameSp.Count == 1) result.PerFrame[sameSp[0]].Add(root);
            else result.Ambiguous++;
        }
        return result;
    }
}
