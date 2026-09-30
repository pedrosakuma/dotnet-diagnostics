namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record ObservedUnlinkedMeasurement(int Identities, int NativeTemporaryIdentities,
    long NativeTemporaryBytes)
{
    internal void Validate(int maximum, int classified)
    {
        PrevalidationProtocol.Require(Identities >= 0 && Identities <= maximum && Identities <= classified
            && NativeTemporaryIdentities >= 0 && NativeTemporaryIdentities <= Identities
            && NativeTemporaryBytes >= 0 && (NativeTemporaryIdentities != 0 || NativeTemporaryBytes == 0),
            "ObservedUnlinkedMeasurementInvalid");
    }

    internal static ObservedUnlinkedMeasurement Merge(ObservedUnlinkedMeasurement left,
        ObservedUnlinkedMeasurement right)
        => new(checked(left.Identities + right.Identities),
            checked(left.NativeTemporaryIdentities + right.NativeTemporaryIdentities),
            Math.Max(left.NativeTemporaryBytes, right.NativeTemporaryBytes));
}

internal static class ObservedUnlinkedDescriptorProof
{
    internal static bool IsLiveOwner(MonitoredProcessIdentity owner)
    {
        try { return LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(owner); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw PrevalidationProtocol.Error("ObservedUnlinkedOwnerProofUnavailable",
                "The exact descriptor owner's live process state could not be verified.");
        }
    }

    internal static DurableStorageNativeObservation ValidateNative(MonitoredProcessIdentity owner,
        PinnedDescriptorSnapshot first, PinnedDescriptorSnapshot second)
    {
        LinuxProcessDescriptorObserver.ValidateCoherent(first, second, owner);
        PrevalidationProtocol.Require(owner.Role is >= MonitoredProcessRole.Harness and <= MonitoredProcessRole.Target
            && IsLiveOwner(owner), "UnexpectedProcessIdentityLoss");
        PrevalidationProtocol.Require(first.Flags >= 0 && second.Flags >= 0 && (first.Flags & 3) != 3
            && !string.IsNullOrWhiteSpace(first.Identity.Value)
            && first.RegularFileMetadata is { Length: >= 0 } one && one.Identity == first.Identity
            && second.RegularFileMetadata is { Length: >= 0 } two && two.Identity == second.Identity,
            "ObservedUnlinkedNativeProofInvalid");
        var a = first.RegularFileMetadata!.Value;
        var b = second.RegularFileMetadata!.Value;
        return a with { Length = Math.Max(a.Length, b.Length) };
    }

    internal static void ValidateUnlinked(MonitoredProcessIdentity owner,
        PinnedDescriptorSnapshot first, PinnedDescriptorSnapshot second)
    {
        var native = ValidateNative(owner, first, second);
        var target = first.Target;
        if (target.EndsWith(" (deleted)", StringComparison.Ordinal)) target = target[..^" (deleted)".Length];
        PrevalidationProtocol.Require(native.LinkCount == 0 && IsNativeTarget(target),
            "ObservedUnlinkedTargetProofInvalid");
    }

    private static bool IsNativeTarget(string target)
        => !string.IsNullOrWhiteSpace(target) && Path.IsPathFullyQualified(target)
            && target.IndexOf('\0') < 0 && target == Path.GetFullPath(target)
            && target != "/" && !target.StartsWith("/memfd:", StringComparison.Ordinal)
            && target != "/proc" && !target.StartsWith("/proc/", StringComparison.Ordinal)
            && target != "/sys" && !target.StartsWith("/sys/", StringComparison.Ordinal)
            && target != "/dev" && !target.StartsWith("/dev/", StringComparison.Ordinal);
}
