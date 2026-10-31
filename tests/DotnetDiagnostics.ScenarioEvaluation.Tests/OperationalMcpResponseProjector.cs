using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpResponseProjector
{
    public static string Apply(
        string raw,
        OperationalMcpArm arm,
        OperationalMcpProjection projection,
        out bool didProject,
        out string? projectedSchema)
    {
        didProject = false;
        projectedSchema = null;
        if (arm != OperationalMcpArm.Control)
        {
            return raw;
        }

        var root = JsonNode.Parse(raw)?.AsObject()
            ?? throw new JsonException("The MCP response was not a JSON object.");
        if (root["error"] is not null
            || root["data"] is not JsonObject data
            || data["schema"]?.GetValue<string>() is not { } schema
            || !string.Equals(schema, projection.Schema, StringComparison.Ordinal))
        {
            return raw;
        }

        root["data"] = null;
        root["summary"] = projection.NeutralSummary;
        root["hints"] = new JsonArray();
        root.Remove("evaluationProjection");
        didProject = true;
        projectedSchema = schema;
        return root.ToJsonString();
    }
}
