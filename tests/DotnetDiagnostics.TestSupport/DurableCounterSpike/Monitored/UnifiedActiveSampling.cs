using System.Text;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record RootSamplingMeasurement(
    int KnownFileCandidates, int ObservedFiles, int LostFilePaths, int LostDirectoryBranches)
{
    internal const int MaximumPerSweep = 4_096;
    internal const int MaximumPerEntry = MaximumPerSweep * 2_048;
    [JsonIgnore] public bool HasLoss => LostFilePaths != 0 || LostDirectoryBranches != 0;
    [JsonIgnore] public bool Accounted => KnownFileCandidates == ObservedFiles + LostFilePaths;

    internal void Validate(int maximum = MaximumPerEntry)
    {
        PrevalidationProtocol.Require(KnownFileCandidates >= 0 && KnownFileCandidates <= maximum
            && ObservedFiles >= 0 && LostFilePaths >= 0
            && (long)ObservedFiles + LostFilePaths <= KnownFileCandidates
            && LostDirectoryBranches >= 0 && LostDirectoryBranches <= maximum,
            "RootSamplingCounterInvalid");
    }

    internal static RootSamplingMeasurement Merge(RootSamplingMeasurement left, RootSamplingMeasurement right,
        int maximum = MaximumPerEntry)
    {
        left.Validate(maximum);
        right.Validate(maximum);
        PrevalidationProtocol.Require((long)left.KnownFileCandidates + right.KnownFileCandidates <= maximum
            && (long)left.LostDirectoryBranches + right.LostDirectoryBranches <= maximum,
            "RootSamplingCounterOverflow");
        var result = new RootSamplingMeasurement(checked(left.KnownFileCandidates + right.KnownFileCandidates),
            checked(left.ObservedFiles + right.ObservedFiles), checked(left.LostFilePaths + right.LostFilePaths),
            checked(left.LostDirectoryBranches + right.LostDirectoryBranches));
        result.Validate(maximum);
        return result;
    }
}

internal sealed record RootSamplingPopulation(int KnownFileCandidates, int ObservedFiles,
    int LostFilePaths, int LostDirectoryBranches, string NamespaceCoverage,
    string UnknownBranchDescendants, string LostBytesAndTypes)
{
    internal static RootSamplingPopulation? From(RootSamplingMeasurement? measurement)
        => measurement is null ? null : new(measurement.KnownFileCandidates, measurement.ObservedFiles,
            measurement.LostFilePaths, measurement.LostDirectoryBranches,
            measurement.HasLoss || !measurement.Accounted ? "partial" : "observed-without-loss-not-a-census",
            measurement.LostDirectoryBranches == 0 ? "none-observed" : "unknown-not-zero", "unknown-not-zero");
}

// One bounded traversal per sweep, shared across non-overlapping charged roots.
// Entries retain the type returned by enumeration, not a later pathname lookup.
internal sealed class ActiveRootTraversal
{
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenNoFollow = 0x20000;
    private int _directories;
    internal RootSamplingMeasurement Measurement { get; private set; } = new(0, 0, 0, 0);
    internal Action<string>? BeforeDirectoryForComponent { get; init; }

    internal static bool IsMissingFile(Exception error)
        => error is FileNotFoundException { HResult: -2147024894 }
            or DirectoryNotFoundException { HResult: -2147024893 };

    internal static bool IsMissingBranch(Exception error)
        => error is DirectoryNotFoundException { HResult: -2147024893 };

    internal static SafeFileHandle OpenFile(string path)
    {
        var descriptor = Open(path, OpenReadOnly | OpenNonBlocking | OpenCloseOnExec | OpenNoFollow);
        var error = Marshal.GetLastPInvokeError();
        if (descriptor >= 0) return new SafeFileHandle(descriptor, ownsHandle: true);
        if (error == 2) throw new FileNotFoundException("Enumerated root path disappeared.", path);
        throw PrevalidationProtocol.Error(error == 13 ? "RootObservationPermissionDenied" : "RootObservationNativeIo",
            $"Root path pin failed with native error {error}.");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Open(string path, int flags);

    internal void Observe(string root, int maximumPathBytes, Func<string, bool> eligible,
        Func<string, bool> observeFile, Action<Exception> hardError)
    {
        var pending = new Stack<(string Path, bool Mutable)>();
        AddDirectory(root, false);
        while (pending.TryPop(out var branch))
        {
            var directory = branch.Path;
            try
            {
                BeforeDirectoryForComponent?.Invoke(directory);
                MonitoredPathRules.RejectLinks(root, directory);
                // Explicitly disable IgnoreInaccessible and recursion. The queue is insertion-capped.
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*",
                    new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0,
                        RecurseSubdirectories = false, ReturnSpecialDirectories = false }))
                {
                    var path = entry.FullName;
                    PrevalidationProtocol.Require(Encoding.UTF8.GetByteCount(path) <= maximumPathBytes,
                        "ObservedPathLimitExceeded");
                    PrevalidationProtocol.Require((entry.Attributes & FileAttributes.ReparsePoint) == 0,
                        "LinkRejected");
                    if (entry is DirectoryInfo)
                    {
                        AddDirectory(path, eligible(path) && (entry.Attributes & FileAttributes.ReadOnly) == 0);
                        continue;
                    }
                    PrevalidationProtocol.Require(Measurement.KnownFileCandidates < RootSamplingMeasurement.MaximumPerSweep,
                        "TrackedPathLimitExceeded");
                    Measurement = Measurement with { KnownFileCandidates = Measurement.KnownFileCandidates + 1 };
                    try
                    {
                        if (observeFile(path))
                            Measurement = Measurement with { ObservedFiles = Measurement.ObservedFiles + 1 };
                    }
                    catch (Exception error) when (IsMissingFile(error) && eligible(path)
                        && (entry.Attributes & FileAttributes.ReadOnly) == 0)
                    {
                        Measurement = Measurement with { LostFilePaths = Measurement.LostFilePaths + 1 };
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException
                        or DurableStorageExperimentException)
                    {
                        hardError(error);
                    }
                }
            }
            catch (Exception error) when (IsMissingBranch(error) && branch.Mutable)
            {
                Measurement = Measurement with { LostDirectoryBranches = Measurement.LostDirectoryBranches + 1 };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or DurableStorageExperimentException)
            {
                hardError(error);
            }
        }

        void AddDirectory(string path, bool mutable)
        {
            PrevalidationProtocol.Require(_directories < RootSamplingMeasurement.MaximumPerSweep,
                "RootDirectoryWorkLimitExceeded");
            PrevalidationProtocol.Require(Encoding.UTF8.GetByteCount(path) <= maximumPathBytes,
                "ObservedPathLimitExceeded");
            _directories++;
            pending.Push((path, mutable));
        }
    }
}
