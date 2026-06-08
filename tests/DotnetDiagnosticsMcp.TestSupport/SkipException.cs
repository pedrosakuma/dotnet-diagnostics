namespace DotnetDiagnosticsMcp.TestSupport;

/// <summary>
/// Thrown by the shared live-test harness to abort a test with an explanatory reason
/// (in lieu of pulling in a separate Skippable package). xunit 2.x has no dynamic skip,
/// so this surfaces as a failure with a clear message when the environment is misconfigured
/// (e.g. the sample binary was never built) — which never happens on a correctly built CI run.
/// </summary>
public sealed class SkipException : Exception
{
    private SkipException(string reason)
        : base(reason)
    {
    }

    public static SkipException ForReason(string reason) => new(reason);
}
