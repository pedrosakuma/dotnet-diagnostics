namespace DotnetDiagnostics.Core.Exceptions;

internal sealed record CrashGuardFinalEvidence(
    bool UnhandledObserved, CrashGuardExceptionEvent? FinalException, bool InferredFromExit)
{
    internal static CrashGuardFinalEvidence Resolve(
        bool processExited, int? exitCode, bool explicitCrashEventObserved,
        CrashGuardExceptionEvent? explicitUnhandled, CrashGuardExceptionEvent? lastObserved)
    {
        if (explicitCrashEventObserved || explicitUnhandled is not null)
            return new(true, explicitUnhandled ?? (processExited ? lastObserved : null), false);

        if (processExited && exitCode.GetValueOrDefault(-1) != 0 && lastObserved is not null)
            return new(true, lastObserved with { IsUnhandled = true }, true);

        return new(false, processExited ? lastObserved : null, false);
    }
}
