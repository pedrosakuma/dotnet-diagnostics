using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Flat parent-index rows preserve deep trees without recursive JSON or CLR traversal.</summary>
internal sealed class CallTreeSnapshotEncoding : JsonConverter<CallTreeNode>
{
    public override CallTreeNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var rows = JsonSerializer.Deserialize<List<CallTreeSnapshotRow>>(ref reader, options)
            ?? throw new JsonException("Null call tree.");
        if (rows.Count == 0) throw new JsonException("Empty call tree.");
        var children = new List<CallTreeNode>?[rows.Count];
        CallTreeNode? root = null;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i] ?? throw new JsonException("Null call-tree row.");
            if (row.Frame is null || (i == 0 ? row.Parent != -1 : row.Parent < 0 || row.Parent >= i))
                throw new JsonException("Invalid call-tree parent or frame.");
            var descendants = children[i];
            descendants?.Reverse();
            var node = new CallTreeNode(row.Frame, row.InclusiveSamples, row.ExclusiveSamples,
                descendants ?? [], row.Identity) { SelfSamples = row.SelfSamples };
            if (i == 0) root = node;
            else (children[row.Parent] ??= []).Add(node);
        }
        return root!;
    }

    public override void Write(Utf8JsonWriter writer, CallTreeNode value, JsonSerializerOptions options)
    {
        var ancestors = new HashSet<CallTreeNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(CallTreeNode Node, int Index, IEnumerator<CallTreeNode> Children)>();
        var index = 0;
        writer.WriteStartArray();
        WriteNode(value, -1);
        try
        {
            while (pending.TryPeek(out var current))
            {
                if (!current.Children.MoveNext())
                {
                    current.Children.Dispose();
                    pending.Pop();
                    ancestors.Remove(current.Node);
                    continue;
                }
                WriteNode(current.Children.Current, current.Index);
            }
        }
        finally
        {
            foreach (var current in pending) current.Children.Dispose();
        }
        writer.WriteEndArray();

        void WriteNode(CallTreeNode node, int parent)
        {
            if (node is null || node.Frame is null || node.Children is null)
                throw new JsonException("Null call-tree node, frame, or children.");
            if (!ancestors.Add(node)) throw new JsonException("Cycle in call tree.");
            JsonSerializer.Serialize(writer, new CallTreeSnapshotRow(parent, node.Frame,
                node.InclusiveSamples, node.ExclusiveSamples, node.Identity, node.SelfSamples), options);
            // Flush per row so a deep/infinite producer cannot grow traversal state past the byte cap.
            writer.Flush();
            pending.Push((node, index++, node.Children.GetEnumerator()));
        }
    }
}

internal sealed record CallTreeSnapshotRow(
    int Parent,
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    MethodIdentity? Identity,
    SelfSampleBreakdown? SelfSamples);

internal sealed record SymbolSnapshotRow<T>(SymbolRef Key, T Value);

/// <summary>Symbol keys are structured pairs, never delimiter-concatenated strings or type names.</summary>
internal sealed class SymbolMapSnapshotEncoding<T> : JsonConverter<IReadOnlyDictionary<SymbolRef, T>> where T : class
{
    public override IReadOnlyDictionary<SymbolRef, T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var rows = JsonSerializer.Deserialize<List<SymbolSnapshotRow<T>>>(ref reader, options)
            ?? throw new JsonException("Null symbol map.");
        var result = new Dictionary<SymbolRef, T>();
        foreach (var row in rows)
        {
            if (row is null || row.Key is null || row.Key.Module is null || row.Key.MethodFullName is null
                || row.Value is null || !result.TryAdd(row.Key, row.Value))
                throw new JsonException("Invalid or duplicate symbol key.");
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<SymbolRef, T> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var row in value)
        {
            if (row.Key is null || row.Value is null) throw new JsonException("Null symbol key or value.");
            JsonSerializer.Serialize(writer, new SymbolSnapshotRow<T>(row.Key, row.Value), options);
            writer.Flush();
        }
        writer.WriteEndArray();
    }
}
