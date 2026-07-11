using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Guards against the class of bug fixed alongside issue #573's delta review: a collection
/// handle kind registered by a collector (<see cref="CollectionHandleKinds"/>) but never wired
/// into <c>query_snapshot</c>'s dispatch table, so following the collector's own next-action
/// hint returns <c>UnsupportedHandleKind</c>. Every <see cref="CollectionHandleKinds"/> constant
/// must be dispatchable by <see cref="QuerySnapshotTool"/>.
/// </summary>
public sealed class QuerySnapshotKindParityTests
{
    [Fact]
    public void QuerySnapshot_Dispatches_Every_CollectionHandleKind()
    {
        var declaredKinds = typeof(CollectionHandleKinds)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        declaredKinds.Should().NotBeEmpty();
        declaredKinds.Should().BeSubsetOf(QuerySnapshotTool.RegisteredKinds,
            "every CollectionHandleKinds constant is registered by an EventPipe collector and must be " +
            "reachable via query_snapshot, or its collect_* hints (and docs/tool-reference.md) will point at a dead end");
    }
}
