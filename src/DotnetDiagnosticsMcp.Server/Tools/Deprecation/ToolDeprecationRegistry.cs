using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools.Deprecation;

/// <summary>
/// Tool-name → <see cref="DeprecatedToolAttribute"/> index built once at startup by scanning
/// the supplied tool-surface types. Mirrors <c>ToolScopeRegistry</c>'s shape so the
/// scaffolding for RFC 0002 stays familiar.
/// </summary>
/// <remarks>
/// <para>The registry is also responsible for the once-per-tool audit-log emission triggered
/// on the first call to a deprecated tool — the once-flags live on the registry instance so
/// they survive across CallTool invocations (and reset cleanly via
/// <see cref="ResetForTesting"/>).</para>
/// </remarks>
public sealed class ToolDeprecationRegistry
{
    /// <summary>Per-tool deprecation record exposed to filters / tests.</summary>
    /// <param name="ToolName">The deprecated tool's MCP name.</param>
    /// <param name="SuccessorTool">Replacement tool name.</param>
    /// <param name="RemovalVersion">Version in which the deprecated tool will be removed.</param>
    /// <param name="Note">Optional extra hint.</param>
    public sealed record Entry(string ToolName, string SuccessorTool, string RemovalVersion, string? Note)
    {
        /// <summary>Human-readable notice appended to the tool's MCP description and emitted on
        /// first invocation. Format is fixed so tests / clients can pattern-match.</summary>
        public string Notice =>
            string.IsNullOrWhiteSpace(Note)
                ? $"DEPRECATED: '{ToolName}' will be removed in {RemovalVersion}. Use '{SuccessorTool}' instead."
                : $"DEPRECATED: '{ToolName}' will be removed in {RemovalVersion}. Use '{SuccessorTool}' instead. {Note}";
    }

    private readonly ImmutableDictionary<string, Entry> _byToolName;
    // One int per tool name. 0 = not yet warned, 1 = warning emitted.
    private readonly Dictionary<string, int> _warnedFlags;
    private readonly ILogger<ToolDeprecationRegistry> _logger;

    private ToolDeprecationRegistry(
        ImmutableDictionary<string, Entry> byToolName,
        ILogger<ToolDeprecationRegistry>? logger)
    {
        _byToolName = byToolName;
        _warnedFlags = byToolName.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        _logger = logger ?? NullLogger<ToolDeprecationRegistry>.Instance;
    }

    /// <summary>Empty registry — used by the DI default before sub-issues stamp
    /// <c>[DeprecatedTool]</c> onto any real method.</summary>
    public static ToolDeprecationRegistry Empty { get; } =
        new(ImmutableDictionary<string, Entry>.Empty, NullLogger<ToolDeprecationRegistry>.Instance);

    /// <summary>Look up the deprecation record for <paramref name="toolName"/>, or <c>null</c>
    /// when the tool is not registered as deprecated.</summary>
    public Entry? TryGet(string toolName)
        => _byToolName.TryGetValue(toolName, out var entry) ? entry : null;

    /// <summary>Every deprecated tool currently tracked.</summary>
    public IReadOnlyCollection<Entry> Entries => _byToolName.Values.ToArray();

    /// <summary>
    /// Records that <paramref name="toolName"/> was invoked. When the tool is registered as
    /// deprecated, emits the audit-log warning exactly once per process (subsequent calls are
    /// no-ops). Returns the matching <see cref="Entry"/> for callers that want to surface the
    /// notice further, or <c>null</c> when the tool is not deprecated.
    /// </summary>
    public Entry? NotifyInvocation(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return null;
        if (!_byToolName.TryGetValue(toolName, out var entry)) return null;

        // Atomic test-and-set so concurrent CallTool requests still produce exactly one log
        // line. Dictionary slots are pre-allocated in the constructor so this is a hot-path
        // CompareExchange on a stable int, no allocation, no contention beyond the slot.
        lock (_warnedFlags)
        {
            if (_warnedFlags[toolName] == 0)
            {
                _warnedFlags[toolName] = 1;
                _logger.LogWarning(
                    "Tool '{Tool}' is deprecated and will be removed in {RemovalVersion}. Migrate callers to '{Successor}'.",
                    entry.ToolName, entry.RemovalVersion, entry.SuccessorTool);
            }
        }

        return entry;
    }

    /// <summary>Clears the once-per-process flags. Test-only helper named with the
    /// double-underscore prefix so it cannot be confused with a production API.</summary>
    public void ResetForTesting()
    {
        lock (_warnedFlags)
        {
            foreach (var key in _warnedFlags.Keys.ToArray())
            {
                _warnedFlags[key] = 0;
            }
        }
    }

    /// <summary>Scans the supplied tool-surface types for methods carrying both
    /// <c>[McpServerTool]</c> and <see cref="DeprecatedToolAttribute"/>, indexed by tool name.
    /// Tools without the attribute are ignored (this is the default for every tool today).
    /// </summary>
    public static ToolDeprecationRegistry Build(
        IEnumerable<Type> toolSurfaceTypes,
        ILogger<ToolDeprecationRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(toolSurfaceTypes);

        var builder = ImmutableDictionary.CreateBuilder<string, Entry>(StringComparer.Ordinal);
        foreach (var type in toolSurfaceTypes)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;
                var deprAttr = method.GetCustomAttribute<DeprecatedToolAttribute>();
                if (deprAttr is null) continue;

                var toolName = toolAttr.Name;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    throw new InvalidOperationException(
                        $"[McpServerTool] on {type.FullName}.{method.Name} must specify Name to be marked deprecated.");
                }

                builder[toolName] = new Entry(toolName, deprAttr.SuccessorTool, deprAttr.RemovalVersion, deprAttr.Note);
            }
        }

        return new ToolDeprecationRegistry(builder.ToImmutable(), logger);
    }
}
