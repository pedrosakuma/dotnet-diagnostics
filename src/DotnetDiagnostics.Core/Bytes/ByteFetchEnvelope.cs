namespace DotnetDiagnostics.Core.Bytes;

public sealed record ByteFetchEnvelope
{
    public required string Kind { get; init; }
    public required string Asset { get; init; }
    public required string Identifier { get; init; }
    public required string SourcePath { get; init; }
    public required long TotalSize { get; init; }
    public required string Sha256 { get; init; }
    public required long Offset { get; init; }
    public required int ChunkSize { get; init; }
    public required string Base64Chunk { get; init; }
    public long? NextOffset { get; init; }
    public string? CompanionPdbPath { get; init; }
    public bool? PdbIsEmbedded { get; init; }
    public int? ProcessId { get; init; }
}
