namespace DotnetDiagnosticsMcp.Core.Comparison;

/// <summary>
/// Projects a single collector artifact into a portable <see cref="ComparableSnapshot"/>.
/// One implementation per comparable kind; both the handle-based diff (server) and the
/// file-based compare (CLI) route through the same projector so the comparison substrate is
/// identical across modes.
/// </summary>
public interface IComparableProjector
{
    /// <summary>The collector kind this projector handles (matches <see cref="ComparableSnapshot.Kind"/>).</summary>
    string Kind { get; }

    /// <summary>True when <paramref name="artifact"/> is the concrete type this projector understands.</summary>
    bool CanProject(object artifact);

    /// <summary>
    /// Builds a bounded snapshot from <paramref name="artifact"/>. <paramref name="label"/>
    /// distinguishes the capture within a journey (e.g. <c>baseline</c> / <c>after</c> / a pod name).
    /// </summary>
    ComparableSnapshot Project(object artifact, string label);
}
