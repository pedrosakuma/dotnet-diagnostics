using System.Collections.Immutable;

namespace DotnetDiagnostics.Core.Drilldown;

/// <summary>
/// #207 — static map from <c>(HandleOrigin, viewName)</c> to the modifier scope
/// the unified <c>query_snapshot</c> tool must require when authorizing the drill-down.
/// </summary>
/// <remarks>
/// <para>The split-collector/unified-drilldown pattern (see <c>AGENTS.md</c> §"One MCP tool
/// per concept") lets a single drilldown tool serve handles that originated from either a
/// live attach or an offline dump file. The two paths can carry different privacy and
/// trust profiles for the <i>same</i> view name — a duplicate-string preview from a live
/// process is operator-controlled, but the same preview from an imported dump may have
/// come from a different host with different consent. This table is where those policy
/// differences are encoded.</para>
/// <para>The table is intentionally stubbed at #204 with a representative entry. Sub-issue
/// #207 (the unified <c>query_snapshot</c> tool) is what populates it for real and wires
/// it into the authorization filter.</para>
/// </remarks>
public static class HandleAuthorizationTable
{
    /// <summary>Composite key — origin + view (case-sensitive ordinal on the view name).</summary>
    public readonly record struct ViewKey(HandleOrigin Origin, string ViewName);

    private static readonly ImmutableDictionary<ViewKey, string> Entries =
        ImmutableDictionary.CreateRange(new[]
        {
            // Live heap snapshots only expose retention paths (which transitively expose
            // managed-string content) to principals holding the sensitive-heap-read scope.
            // Dump-origin snapshots take the same policy through a separate row to make the
            // legacy-boundary intent explicit at the table level.
            new KeyValuePair<ViewKey, string>(new ViewKey(HandleOrigin.Live, "retention-paths"), "sensitive-heap-read"),
            new KeyValuePair<ViewKey, string>(new ViewKey(HandleOrigin.Dump, "retention-paths"), "sensitive-heap-read"),
            new KeyValuePair<ViewKey, string>(new ViewKey(HandleOrigin.Live, "events"), "sensitive-parameter-read"),
        });

    /// <summary>Returns the required scope for <paramref name="origin"/>/<paramref name="view"/>,
    /// or <c>null</c> when no special scope is required beyond whatever the surrounding
    /// tool's <c>[RequireScope]</c> already enforces.</summary>
    public static string? TryGetRequiredScope(HandleOrigin origin, string view)
    {
        if (string.IsNullOrWhiteSpace(view)) return null;
        return Entries.TryGetValue(new ViewKey(origin, view.Trim()), out var scope) ? scope : null;
    }

    /// <summary>Every registered (origin, view) → scope mapping. Exposed for documentation
    /// generators and unit tests; not intended for hot-path authorization.</summary>
    public static IReadOnlyDictionary<ViewKey, string> All => Entries;
}
