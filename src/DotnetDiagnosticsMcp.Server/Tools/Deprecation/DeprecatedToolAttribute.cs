namespace DotnetDiagnosticsMcp.Server.Tools.Deprecation;

/// <summary>
/// Marks an <c>[McpServerTool]</c> method as deprecated in favor of a successor tool, with a
/// stated removal version. Scaffolding for the RFC 0002 tool-surface consolidation rollout
/// (issue #204): the attribute is consumed by <see cref="ToolDeprecationRegistry"/> to
/// (a) append a deprecation notice to the tool's MCP description (visible in
/// <c>tools/list</c>) and (b) emit a once-per-process audit-log event the first time a
/// deprecated tool is invoked.
/// </summary>
/// <remarks>
/// <para>
/// This attribute does NOT remove or alter the deprecated tool's behavior. Sub-issues
/// #205–#213 each add a successor tool first, then stamp <c>[DeprecatedTool]</c> on the
/// legacy method so callers continue to work through a deprecation window.
/// </para>
/// <para>Format mirrors the B5.4 <c>LegacyDiagnosticsFlagDeprecation</c> pattern: a single
/// human-readable string per emission point, kept verbatim so tests can assert on wording.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DeprecatedToolAttribute : Attribute
{
    public DeprecatedToolAttribute(string successorTool, string removalVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(successorTool);
        ArgumentException.ThrowIfNullOrWhiteSpace(removalVersion);
        SuccessorTool = successorTool;
        RemovalVersion = removalVersion;
    }

    /// <summary>Name of the MCP tool callers should migrate to.</summary>
    public string SuccessorTool { get; }

    /// <summary>Version in which this tool is scheduled to be removed (e.g. <c>"0.7.0"</c>).</summary>
    public string RemovalVersion { get; }

    /// <summary>Optional free-form note (e.g. extra migration hint).</summary>
    public string? Note { get; init; }
}
