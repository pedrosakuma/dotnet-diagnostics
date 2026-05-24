using DotnetDiagnosticsMcp.Core.Drilldown;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// RFC 0002 / #204 — covers the <see cref="HandleOrigin"/> metadata addition on the handle
/// store plus the stub <see cref="HandleAuthorizationTable"/> that the unified
/// <c>query_snapshot</c> tool (#207) will consume.
/// </summary>
public sealed class HandleOriginAndAuthorizationTests
{
    [Fact]
    public void Register_DefaultsToLiveOrigin_WhenEvictWhenProcessExitsTrue()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(123, "cpu-sample", new object(), TimeSpan.FromMinutes(1));

        handle.Origin.Should().Be(HandleOrigin.Live);
    }

    [Fact]
    public void Register_InfersDumpOrigin_WhenEvictWhenProcessExitsFalse()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            123, "heap-snapshot", new object(), TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false);

        handle.Origin.Should().Be(HandleOrigin.Dump);
    }

    [Fact]
    public void Register_ExplicitOrigin_OverridesInference()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            123, "heap-snapshot", new object(), TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false,
            origin: HandleOrigin.Imported);

        handle.Origin.Should().Be(HandleOrigin.Imported);
    }

    [Fact]
    public void HandleAuthorizationTable_ResolvesStubbedEntry()
    {
        HandleAuthorizationTable.TryGetRequiredScope(HandleOrigin.Live, "retention-paths")
            .Should().Be("sensitive-heap-read");
        HandleAuthorizationTable.TryGetRequiredScope(HandleOrigin.Dump, "retention-paths")
            .Should().Be("sensitive-heap-read");
    }

    [Fact]
    public void HandleAuthorizationTable_ReturnsNull_ForUnregisteredView()
    {
        HandleAuthorizationTable.TryGetRequiredScope(HandleOrigin.Live, "summary")
            .Should().BeNull();
        HandleAuthorizationTable.TryGetRequiredScope(HandleOrigin.Imported, "retention-paths")
            .Should().BeNull("Imported origin is intentionally NOT in the stub table — sub-issue #207 populates it");
    }
}
