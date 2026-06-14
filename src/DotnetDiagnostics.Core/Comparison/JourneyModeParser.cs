namespace DotnetDiagnostics.Core.Comparison;

/// <summary>Parses user-facing journey mode strings shared by MCP and CLI entry points.</summary>
public static class JourneyModeParser
{
    public static bool TryParse(string? value, out JourneyMode mode)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "trend", StringComparison.OrdinalIgnoreCase))
        {
            mode = JourneyMode.Trend;
            return true;
        }

        if (string.Equals(value, "dispersion", StringComparison.OrdinalIgnoreCase))
        {
            mode = JourneyMode.Dispersion;
            return true;
        }

        mode = JourneyMode.Trend;
        return false;
    }
}
