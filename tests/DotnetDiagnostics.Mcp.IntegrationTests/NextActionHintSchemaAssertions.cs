using System.ComponentModel;
using System.Reflection;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

internal static class NextActionHintSchemaAssertions
{
    private static readonly IReadOnlyDictionary<string, ToolSchema> Schemas = BuildSchemas();

    private static readonly IReadOnlyDictionary<string, DiscriminatorSchema> Discriminators =
        new Dictionary<string, DiscriminatorSchema>(StringComparer.Ordinal)
        {
            ["collect_events"] = new("kind", CollectEventsTool.AllowedKinds),
            ["collect_sample"] = new("kind", CollectSampleTool.AllowedKinds),
            ["get_bytes"] = new("kind", GetBytesTool.AllowedKinds),
            ["inspect_heap"] = new(
                "source",
                [InspectHeapTool.SourceLive, InspectHeapTool.SourceDump, InspectHeapTool.SourceGcDump]),
            ["inspect_process"] = new(
                "view",
                [
                    InspectProcessTool.ListView,
                    InspectProcessTool.InfoView,
                    InspectProcessTool.CapabilitiesView,
                    InspectProcessTool.ContainerView,
                    InspectProcessTool.MemoryTrendView,
                    InspectProcessTool.RuntimeConfigView,
                    InspectProcessTool.ResourcesView,
                    InspectProcessTool.RequestsNowView,
                    InspectProcessTool.TriageView,
                    InspectProcessTool.PreflightView,
                ]),
            ["list_orchestrator"] = new(
                "kind",
                [ListOrchestratorTool.KindPods, ListOrchestratorTool.KindInvestigations]),
        };

    public static void ShouldMatchCanonicalSchema(this NextActionHint hint)
    {
        Schemas.Should().ContainKey(hint.NextTool,
            $"'{hint.NextTool}' must be a reflected canonical MCP tool");

        if (hint.SuggestedArguments is not { } arguments)
        {
            return;
        }

        var schema = Schemas[hint.NextTool];
        arguments.Keys.Should().OnlyContain(
            key => schema.Parameters.ContainsKey(key),
            $"every suggested argument for '{hint.NextTool}' must exist in its reflected input schema");

        foreach (var required in schema.RequiredParameters)
        {
            arguments.Should().ContainKey(required,
                $"'{hint.NextTool}' requires '{required}' for a replayable suggested-argument bag");
            arguments[required].Should().NotBeNull(
                $"required suggested argument '{required}' for '{hint.NextTool}' cannot be null");
        }

        foreach (var (name, value) in arguments)
        {
            if (value is null)
            {
                continue;
            }

            IsCompatible(value, schema.Parameters[name]).Should().BeTrue(
                $"suggested argument '{name}' for '{hint.NextTool}' must match reflected type " +
                $"'{schema.Parameters[name].Name}', but received '{value.GetType().Name}'");
        }

        if (Discriminators.TryGetValue(hint.NextTool, out var discriminator)
            && arguments.TryGetValue(discriminator.Parameter, out var discriminatorValue))
        {
            discriminatorValue.Should().BeOfType<string>();
            discriminator.AllowedValues.Should().Contain(
                (string)discriminatorValue!,
                $"'{hint.NextTool}.{discriminator.Parameter}' must use a canonical discriminator value");
        }
    }

    private static IReadOnlyDictionary<string, ToolSchema> BuildSchemas()
    {
        var schemas = new Dictionary<string, ToolSchema>(StringComparer.Ordinal);
        foreach (var type in PodLocalToolSurfaces.GetSurfaceTypes(
                     enableOrchestratorTools: true,
                     enableAzureDiscoveryTools: true))
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute?.Name is not { Length: > 0 } name)
                {
                    continue;
                }

                var parameters = method.GetParameters()
                    .Where(parameter => parameter.GetCustomAttribute<DescriptionAttribute>() is not null)
                    .ToDictionary(parameter => parameter.Name!, parameter => parameter.ParameterType, StringComparer.Ordinal);
                var required = method.GetParameters()
                    .Where(parameter => parameter.GetCustomAttribute<DescriptionAttribute>() is not null
                                        && !parameter.HasDefaultValue)
                    .Select(parameter => parameter.Name!)
                    .ToHashSet(StringComparer.Ordinal);

                schemas.Add(name, new ToolSchema(parameters, required));
            }
        }

        return schemas;
    }

    private static bool IsCompatible(object value, Type parameterType)
    {
        var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        if (targetType.IsInstanceOfType(value))
        {
            return true;
        }

        return targetType.IsEnum
               && value is string enumName
               && Enum.TryParse(targetType, enumName, ignoreCase: true, out _);
    }

    private sealed record ToolSchema(
        IReadOnlyDictionary<string, Type> Parameters,
        IReadOnlySet<string> RequiredParameters);

    private sealed record DiscriminatorSchema(string Parameter, IReadOnlyList<string> AllowedValues);
}
