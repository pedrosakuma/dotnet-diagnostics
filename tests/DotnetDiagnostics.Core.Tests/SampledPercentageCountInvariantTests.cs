using System.Reflection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Issue #1077: a sampled percentage must never be returned without the sample count it rests on,
/// and summaries must state the denominator the shares are taken against.
/// </summary>
public sealed class SampledPercentageCountInvariantTests
{
    private const string Handle = "cpu-1";

    private static readonly string[] SampledNamespaces =
    [
        "DotnetDiagnostics.Core.CpuSampling",
        "DotnetDiagnostics.Core.Drilldown",
        "DotnetDiagnostics.Core.Memory",
    ];

    // Percent-named members that are not a sampled share: request thresholds, deltas between two
    // captures (each side carries its own counts), and non-sampled heap/process measurements.
    private static readonly HashSet<string> NotASampledShare = new(StringComparer.Ordinal)
    {
        "HotPathView.ThresholdPercent",
        "HotPathFrame.FractionOfParentPercent",
        "DiffRow`2.DeltaPct",
        "SampleDiff`2.MinDeltaPct",
    };

    [Fact]
    public void EveryPercentageBearingSampledResult_CarriesASiblingSampleCount()
    {
        var offenders = new List<string>();
        var inspected = 0;
        foreach (var type in typeof(CpuSampleQueryDispatcher).Assembly.GetExportedTypes()
                     .Where(t => SampledNamespaces.Contains(t.Namespace) && !t.IsEnum))
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var percent in properties.Where(IsPercentProperty))
            {
                if (NotASampledShare.Contains($"{type.Name}.{percent.Name}"))
                {
                    continue;
                }

                inspected++;
                var hasCount = properties.Any(p =>
                    p.PropertyType == typeof(long)
                    && (p.Name.Contains("Samples", StringComparison.Ordinal)
                        || p.Name.Equals("Samples", StringComparison.Ordinal)));
                if (!hasCount)
                {
                    offenders.Add($"{type.FullName}.{percent.Name}");
                }
            }
        }

        inspected.Should().BeGreaterThan(5, "the guard must actually find the percentage-bearing records");
        offenders.Should().BeEmpty("every sampled percentage needs a sibling long sample count (issue #1077)");
    }

    [Fact]
    public void ViewSummaries_StateTheTotalSampleDenominator()
    {
        var leaf = new CallTreeNode(new SampledFrame("App.dll", "App.Leaf()"), 3, 3, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("App.dll", "App.Root()"), 3, 0, [leaf]);
        var trace = new CpuSampleTraceArtifact(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 3, root);

        var summaries = new[]
        {
            CpuSampleQueryDispatcher.RenderTopMethods(trace, Handle, "exclusive", 5).Summary,
            CpuSampleQueryDispatcher.RenderByNamespace(trace, Handle, 5).Summary,
            CpuSampleQueryDispatcher.RenderHotPath(trace, Handle, 50).Summary,
            CpuSampleQueryDispatcher.RenderTriage(trace, Handle, 5, 50).Summary,
        };

        foreach (var summary in summaries)
        {
            summary.Should().Contain("of 3 ", "summaries must name the sample total their counts/percentages rest on");
        }
    }

    private static bool IsPercentProperty(PropertyInfo p)
        => (p.PropertyType == typeof(double) || p.PropertyType == typeof(float))
           && (p.Name.EndsWith("Percent", StringComparison.Ordinal) || p.Name.EndsWith("Pct", StringComparison.Ordinal));
}
