namespace DotnetDiagnostics.Core.Captures;

internal static class PortableCaptureProvenance
{
    internal static void Validate(CaptureInfo info)
    {
        var source = info.PortableSource ?? throw Corrupt();
        var origin = source.Origin ?? throw Corrupt();
        if (origin.Format is null || origin.Format == CapturePackage.PortableFormat ||
            !CapturePackage.IsSupportedFormat(origin.Format) || origin.Artifacts is null ||
            source.ImmediateSource is null || source.ArtifactMap is null ||
            source.ArtifactMap.Count != info.Artifacts.Count || origin.Artifacts.Count != info.Artifacts.Count)
            throw Corrupt();
        CapturePackage.ValidateId(origin.CaptureId);
        CapturePackage.ValidateId(source.ImmediateSource.CaptureId);
        if (!CapturePackage.IsSupportedFormat(source.ImmediateSource.Format)) throw Corrupt();
        ValidateHashes(source.OriginMemberHashes);
        ValidateHashes(source.ImmediateSource.MemberHashes);
        var original = new CaptureInfo(origin.CaptureId, origin.OwnerId, origin.Name, origin.GroupId,
            origin.CreatedUtc, CaptureState.Sealed, origin.Artifacts, origin.Quality, origin.DerivedFrom, origin.SourceHashes);
        var features = origin.Format.PackageVersion == 1 ? new[] { "normalized-scalars-v1" } :
            origin.Artifacts.Any(static a => a.SourceArtifactId is not null)
                ? new[] { "normalized-scalars-v1", "artifact-provenance-v1", CapturePackage.RecoveryIdentityFeature }
                : new[] { "normalized-scalars-v1", "artifact-provenance-v1" };
        _ = CapturePackage.ValidateManifest(new(original, origin.Format.PackageVersion, 1, 1, 1,
            origin.Format.WriterVersion, origin.Format.RequiredReaderVersion, features, 512L * 1024 * 1024), origin.CaptureId);
        if (info.Name != origin.Name || info.GroupId != origin.GroupId || info.CreatedUtc != origin.CreatedUtc ||
            info.Quality != origin.Quality || info.DerivedFrom != origin.DerivedFrom ||
            !EqualHashes(info.SourceHashes, origin.SourceHashes)) throw Corrupt();
        var local = new HashSet<string>(StringComparer.Ordinal);
        var entry = new HashSet<string>(StringComparer.Ordinal);
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in source.ArtifactMap)
        {
            if (mapping is null) throw Corrupt();
            CapturePackage.ValidateId(mapping.LocalArtifactId);
            CapturePackage.ValidateId(mapping.EntryArtifactId);
            CapturePackage.ValidateId(mapping.OriginArtifactId);
            if (!local.Add(mapping.LocalArtifactId) || !entry.Add(mapping.EntryArtifactId) ||
                !origins.Add(mapping.OriginArtifactId)) throw Corrupt();
            var destination = info.Artifacts.FirstOrDefault(a => a.ArtifactId == mapping.LocalArtifactId);
            var originalArtifact = origin.Artifacts.FirstOrDefault(a => a.ArtifactId == mapping.OriginArtifactId);
            if (destination is null || originalArtifact is null ||
                destination.Kind != originalArtifact.Kind || destination.Name != originalArtifact.Name ||
                destination.Provenance != originalArtifact.Provenance ||
                destination.SourceArtifactId != originalArtifact.SourceArtifactId) throw Corrupt();
        }
    }

    internal static bool EqualHashes(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right) =>
        left is null ? right is null : right is not null && left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);

    internal static void ValidateHashes(PortableCaptureMemberHashes hashes)
    {
        if (hashes is null || !IsHash(hashes.Manifest) || !IsHash(hashes.Database) || !IsHash(hashes.Seal))
            throw Corrupt();
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CaptureStoreException Corrupt() =>
        CapturePackage.Error(CaptureErrorCode.CorruptPackage, "PortableSource.Invalid: bounded origin or artifact bijection is inconsistent.");
}
