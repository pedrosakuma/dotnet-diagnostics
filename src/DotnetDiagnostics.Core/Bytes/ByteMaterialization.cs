namespace DotnetDiagnostics.Core.Bytes;

/// <summary>
/// Pointer artifact emitted when a byte-fetch artifact (a managed module PE/PDB, or a dump file) is
/// <b>materialised to disk</b> by the standalone CLI's <c>get-bytes --out</c> command (issue #288).
/// </summary>
/// <remarks>
/// This is deliberately distinct from <see cref="ByteFetchEnvelope"/>: the MCP <c>get_bytes</c> tool
/// streams base64 chunks for a sibling MCP to reassemble through the orchestrator proxy, whereas the
/// one-shot CLI loops that stream itself, writes the whole artifact to a local file, and returns this
/// pointer (path + size + integrity hash) rather than inline bytes.
/// </remarks>
public sealed record ByteMaterialization
{
    /// <summary>Artifact kind: <c>module</c> or <c>dump</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Concrete asset streamed: <c>pe</c>, <c>pdb</c> or <c>dump</c>.</summary>
    public required string Asset { get; init; }

    /// <summary>Source identifier: the module MVID (GUID 'D') for modules, or the dump path for dumps.</summary>
    public required string Identifier { get; init; }

    /// <summary>Absolute path the bytes were read from on the target host.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Absolute path the bytes were materialised to on the local filesystem.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Total number of bytes written.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the materialised artifact (verified against the source).</summary>
    public required string Sha256 { get; init; }

    /// <summary>Process id the module was read from (module kind only).</summary>
    public int? ProcessId { get; init; }

    /// <summary>Sibling PDB path discovered next to a module PE, when present.</summary>
    public string? CompanionPdbPath { get; init; }

    /// <summary>True when a materialised PDB came from an embedded portable PDB inside the PE.</summary>
    public bool? PdbIsEmbedded { get; init; }
}
