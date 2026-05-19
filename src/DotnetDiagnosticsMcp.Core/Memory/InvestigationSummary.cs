namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// Portable investigation summary (schema v1). 5-20 KB JSON designed to be pasted into a PR,
/// ADR or ticket and fed back into a future investigation. The server stays stateless: the
/// LLM owns persistence (it pastes the JSON into a doc, then supplies it on the next run).
/// </summary>
public sealed record InvestigationSummary(
    string Schema,
    string InvestigationId,
    DateTimeOffset CreatedAt,
    int ProcessId,
    InvestigationProvenance Provenance,
    InvestigationFindings Findings,
    string? PreviousInvestigationId = null,
    InvestigationFixTarget? TargetsFix = null,
    string? Notes = null)
{
    public const string SchemaV1 = "dotnet-diagnostics-mcp/investigation-summary/v1";
}

/// <summary>Where the observation was made — build + container provenance survive re-deploys.</summary>
public sealed record InvestigationProvenance(string? Hostname)
{
    public BuildProvenance? Build { get; init; }
    public ContainerProvenance? Container { get; init; }
}

/// <summary>
/// Build identity. <c>InformationalVersion</c> typically includes the git SHA when SourceLink
/// is configured; that's what makes a summary diffable across rebuilds.
/// </summary>
public sealed record BuildProvenance(
    string? AssemblyName,
    string? AssemblyVersion,
    string? InformationalVersion,
    string? GitSha,
    string? TargetFramework);

/// <summary>Kubernetes / container metadata captured from downward-API env vars.</summary>
public sealed record ContainerProvenance(
    string? Image,
    string? Namespace,
    string? PodName,
    string? NodeName);

/// <summary>What the investigation surfaced. Symbol refs are module+methodFullName so they survive rebuilds.</summary>
public sealed record InvestigationFindings(
    long TotalSamples,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<HotspotSummary> TopHotspots,
    IReadOnlyDictionary<string, double>? KeyMetrics = null);

/// <summary>
/// Stable, comparable reference to a method observed in a sample. <c>methodFullName</c>
/// includes namespace+type+method; <c>module</c> is the owning managed module. Together they
/// survive rebuilds where line numbers shift but symbols don't.
/// </summary>
public sealed record HotspotSummary(
    SymbolRef Symbol,
    long InclusiveSamples,
    long ExclusiveSamples,
    double InclusivePercent,
    double ExclusivePercent,
    SourceLocation? Source = null,
    MethodIdentity? Identity = null);

public sealed record SymbolRef(string Module, string MethodFullName);

/// <summary>
/// Source-level resolution for a hotspot. Populated lazily for top-N frames only — the
/// sampler skips deep stacks to keep cost predictable. <c>SourceLink</c> is the SourceLink
/// HTTP URL embedded in the PDB when available, ready to paste into a PR comment.
/// </summary>
public sealed record SourceLocation(string? File, int? StartLine, string? SourceLink);

/// <summary>
/// Canonical, machine-readable identity of a managed method observed in a diagnostic
/// trace (issue #18 — handoff with <c>dotnet-assembly-mcp</c>). The <c>(ModuleVersionId,
/// MetadataToken)</c> pair round-trips exactly to a single <c>MethodDefinition</c> in the
/// PE metadata regardless of name mangling, generic instantiations, or compiler-synthesized
/// closure names. All other fields are display aids.
/// </summary>
/// <param name="ModuleName">Simple module file name (e.g. <c>MyApp.dll</c>).</param>
/// <param name="ModulePath">Absolute path on disk when known by the sidecar.</param>
/// <param name="ModuleVersionId">PE module MVID — stable across copies of the same binary.</param>
/// <param name="MetadataToken">IL method-def metadata token (table 0x06).</param>
/// <param name="TypeFullName">Declaring type FQN (namespace + nested type chain).</param>
/// <param name="MethodName">Bare method name (no signature).</param>
/// <param name="GenericArity">Number of generic method parameters; 0 for non-generic methods.</param>
public sealed record MethodIdentity(
    string? ModuleName,
    string? ModulePath,
    Guid? ModuleVersionId,
    int? MetadataToken,
    string? TypeFullName,
    string MethodName,
    int GenericArity)
{
    /// <summary>
    /// Closed generic instantiation captured at sample time, when the producer can extract
    /// the type-arg sequence structurally from the runtime trace (issue #21 — coordinates
    /// with <c>dotnet-assembly-mcp</c>'s closed-signature resolution). Null when the method
    /// is non-generic OR when the producer couldn't recover the instantiation (e.g. native
    /// frames, NativeAOT, malformed FullMethodName) — consumers fall back to the open def.
    /// Type-arg strings are CLR reflection-style full names without assembly qualification
    /// (e.g. <c>System.Collections.Generic.List`1[System.Int32]</c>; nested types with
    /// <c>+</c>; arrays <c>T[]</c>/<c>T[,]</c>).
    /// </summary>
    public GenericInstantiation? GenericTypeArguments { get; init; }
}

/// <summary>Closed generic instantiation for a single <see cref="MethodIdentity"/>.
/// <see cref="Type"/> carries the declaring type's type-args (length matches the open
/// type's generic arity); <see cref="Method"/> carries the method's own type-args (length
/// matches <see cref="MethodIdentity.GenericArity"/>). Either list may be empty when only
/// the other axis is generic. Both lists are never null when this record is emitted —
/// emit the parent <see cref="MethodIdentity.GenericTypeArguments"/> as null instead.</summary>
public sealed record GenericInstantiation(
    IReadOnlyList<string> Type,
    IReadOnlyList<string> Method);

/// <summary>Optional declaration that this investigation targets / proposes a fix.</summary>
public sealed record InvestigationFixTarget(
    string? CommitSha = null,
    string? PullRequestUrl = null,
    string? Description = null);
