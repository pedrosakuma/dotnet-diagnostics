using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Builds a merged caller→callee tree from sampled stacks. Used by both the managed
/// (<see cref="EventPipeCpuSampler"/>) and native (perf-based) samplers so the on-the-wire
/// shape of <see cref="CallTreeNode"/> is identical regardless of capture source.
/// </summary>
/// <remarks>
/// The aggregation key is composed by the caller — typically <c>module + "!" + display</c>
/// so two methods with the same FQN in different modules don't collide. <c>Display</c> is
/// the human-readable name surfaced in the tree node.
/// </remarks>
internal sealed class CallTreeBuilder
{
    private readonly Node _root = new(new SampledFrame(string.Empty, "<root>"));
    private readonly int _maxNodes;
    private int _nodeCount = 1;

    public CallTreeBuilder(int maxNodes = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodes, 1);
        _maxNodes = maxNodes;
    }

    public void AddStack(
        List<(string Key, string Module, string Display)> rootToLeaf,
        string leafKey,
        SelfSampleBreakdown? leafSelfSamples = null,
        long weight = 1)
        => AddStack(
            rootToLeaf.Select(frame => (frame.Key, frame.Module, frame.Display, Identity: (MethodIdentity?)null)).ToList(),
            leafKey,
            leafSelfSamples, weight);

    public void AddStack(
        List<(string Key, string Module, string Display, MethodIdentity? Identity)> rootToLeaf,
        string leafKey,
        SelfSampleBreakdown? leafSelfSamples = null,
        long weight = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        if (weight != 1 && leafSelfSamples is not null)
            throw new ArgumentException("Weighted stacks cannot carry CPU sample-state counts.", nameof(leafSelfSamples));
        var current = _root;
        current.Inclusive = checked(current.Inclusive + weight);
        for (var i = 0; i < rootToLeaf.Count; i++)
        {
            var (key, module, display, identity) = rootToLeaf[i];
            if (!current.Children.TryGetValue(key, out var child))
            {
                if (_nodeCount >= _maxNodes)
                    throw new SampleLimitExceededException($"Call tree reached MaxNodes={_maxNodes}; shorten the capture or narrow the workload. No partial profile is returned.");
                child = new Node(new SampledFrame(module, display), identity);
                current.Children[key] = child;
                _nodeCount++;
            }

            child.Inclusive = checked(child.Inclusive + weight);
            if (key == leafKey && i == rootToLeaf.Count - 1)
            {
                child.Exclusive = checked(child.Exclusive + weight);
                if (leafSelfSamples is not null)
                {
                    child.RunningSelf += leafSelfSamples.RunningSamples;
                    child.WaitingSelf += leafSelfSamples.WaitingSamples;
                    child.UnknownSelf += leafSelfSamples.UnknownSamples;
                }
            }
            current = child;
        }
    }

    public CallTreeNode Build() => Materialize(_root);

    private static CallTreeNode Materialize(Node n)
    {
        var children = n.Children.Values
            .OrderByDescending(c => c.Inclusive)
            .Select(Materialize)
            .ToList();
        return new CallTreeNode(n.Frame, n.Inclusive, n.Exclusive, children)
        {
            Identity = n.Identity,
            SelfSamples = n.RunningSelf > 0 || n.WaitingSelf > 0 || n.UnknownSelf > 0
                ? new SelfSampleBreakdown(n.RunningSelf, n.WaitingSelf, n.UnknownSelf)
                : null,
        };
    }

    private sealed class Node
    {
        public Node(SampledFrame frame, MethodIdentity? identity = null)
        {
            Frame = frame;
            Identity = identity;
        }

        public SampledFrame Frame { get; }
        public MethodIdentity? Identity { get; }
        public long Inclusive;
        public long Exclusive;
        public long RunningSelf;
        public long WaitingSelf;
        public long UnknownSelf;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }
}

internal sealed class SampleLimitExceededException(string message) : InvalidOperationException(message);
