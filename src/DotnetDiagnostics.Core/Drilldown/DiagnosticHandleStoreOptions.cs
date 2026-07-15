namespace DotnetDiagnostics.Core.Drilldown;

/// <summary>Bounded in-memory diagnostic handle-store configuration shared by every host.</summary>
public sealed class DiagnosticHandleStoreOptions
{
    /// <summary>Configuration section used by the MCP server and standalone CLI.</summary>
    public const string SectionName = "Diagnostics:HandleStore";

    /// <summary>Default number of heavy artifacts retained in memory.</summary>
    public const int DefaultMaxEntries = 32;

    /// <summary>Hard safety ceiling; the store cannot be configured as unbounded.</summary>
    public const int MaxAllowedEntries = 1024;

    /// <summary>Maximum number of live heavy artifacts retained by the process.</summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    /// <summary>Throws when configuration would disable or excessively enlarge the safety bound.</summary>
    public void Validate()
    {
        if (MaxEntries is < 1 or > MaxAllowedEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEntries),
                MaxEntries,
                $"{SectionName}:MaxEntries must be between 1 and {MaxAllowedEntries}.");
        }
    }
}
