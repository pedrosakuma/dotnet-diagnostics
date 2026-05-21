using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Groups captured thread stacks by a stable signature derived from the top-N frames. Intended for
/// <c>query_thread_snapshot(view="unique-stacks")</c> so the LLM can reason about K stack shapes
/// instead of O(N) individual threads.
/// </summary>
public static class ThreadSnapshotUniqueStackGrouper
{
    public const int DefaultFramesToHash = 20;
    public const int DefaultSampleThreadCount = 5;

    public static IReadOnlyList<UniqueThreadStackGroup> Group(
        IReadOnlyList<ManagedThread> threads,
        int framesToHash = DefaultFramesToHash,
        int minCount = 1,
        int topN = int.MaxValue,
        int sampleThreadCount = DefaultSampleThreadCount)
    {
        ArgumentNullException.ThrowIfNull(threads);
        if (framesToHash < 1) throw new ArgumentOutOfRangeException(nameof(framesToHash), "must be >= 1");
        if (minCount < 1) throw new ArgumentOutOfRangeException(nameof(minCount), "must be >= 1");
        if (topN < 1) throw new ArgumentOutOfRangeException(nameof(topN), "must be >= 1");
        if (sampleThreadCount < 1) throw new ArgumentOutOfRangeException(nameof(sampleThreadCount), "must be >= 1");
        if (threads.Count == 0) return Array.Empty<UniqueThreadStackGroup>();

        var groups = new Dictionary<string, StackGroupAccumulator>(StringComparer.Ordinal);
        foreach (var thread in threads)
        {
            var signatureFrames = thread.Frames.Take(framesToHash).ToArray();
            var signatureKey = BuildSignatureKey(signatureFrames);
            if (!groups.TryGetValue(signatureKey, out var group))
            {
                group = new StackGroupAccumulator(
                    ComputeStableHash(signatureKey),
                    signatureFrames.Reverse().ToArray(),
                    thread.InferredWaitReason);
                groups.Add(signatureKey, group);
            }

            group.AddThread(thread, sampleThreadCount);
        }

        return groups.Values
            .Where(group => group.ThreadCount >= minCount)
            .OrderByDescending(group => group.ThreadCount)
            .ThenBy(group => group.SignatureHash, StringComparer.Ordinal)
            .Take(topN)
            .Select(group => group.ToResult(threads.Count))
            .ToArray();
    }

    private static string BuildSignatureKey(ManagedStackFrame[] frames)
    {
        if (frames.Length == 0)
        {
            return "<empty>";
        }

        var builder = new StringBuilder(frames.Length * 64);
        foreach (var frame in frames)
        {
            builder.Append(BuildFrameKey(frame)).Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildFrameKey(ManagedStackFrame frame)
    {
        if (frame.Identity is { ModuleVersionId: Guid moduleVersionId, MetadataToken: int metadataToken })
        {
            return string.Concat(moduleVersionId.ToString("N", CultureInfo.InvariantCulture), ":", metadataToken.ToString("x8", CultureInfo.InvariantCulture));
        }

        if (frame.Identity is { } identity)
        {
            return string.Join(
                '|',
                identity.ModuleName ?? string.Empty,
                identity.TypeFullName ?? string.Empty,
                identity.MethodName,
                identity.GenericArity.ToString(CultureInfo.InvariantCulture),
                identity.MetadataToken?.ToString("x8", CultureInfo.InvariantCulture) ?? string.Empty,
                frame.Kind,
                frame.DisplayName);
        }

        return string.Join(
            '|',
            frame.Kind,
            frame.DisplayName,
            frame.TypeFullName ?? string.Empty,
            frame.ModuleName ?? string.Empty);
    }

    private static string ComputeStableHash(string signatureKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureKey)));

    private sealed class StackGroupAccumulator(string signatureHash, ManagedStackFrame[] canonicalFrames, string? inferredWaitReason)
    {
        private readonly List<ThreadSampleId> _sampleThreads = [];

        public string SignatureHash { get; } = signatureHash;

        public IReadOnlyList<ManagedStackFrame> CanonicalFrames { get; } = canonicalFrames;

        public string? InferredWaitReason { get; } = inferredWaitReason;

        public int ThreadCount { get; private set; }

        public void AddThread(ManagedThread thread, int sampleThreadCount)
        {
            ThreadCount++;
            if (_sampleThreads.Count < sampleThreadCount)
            {
                _sampleThreads.Add(new ThreadSampleId(thread.ManagedThreadId, thread.OSThreadId));
            }
        }

        public UniqueThreadStackGroup ToResult(int totalThreads)
            => new(
                SignatureHash,
                ThreadCount,
                totalThreads == 0 ? 0 : (double)ThreadCount / totalThreads,
                _sampleThreads.ToArray(),
                CanonicalFrames)
            {
                InferredWaitReason = InferredWaitReason,
            };
    }
}
