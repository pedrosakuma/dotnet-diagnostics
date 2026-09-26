namespace DotnetDiagnostics.TestSupport;

/// <summary>Opt-in scalar probe evidence. Never retains URLs, headers, bodies, or exception messages.</summary>
public sealed class HttpReadinessDiagnostics
{
    public int? LastStatusCode { get; private set; }
    public string LastProbeOutcome { get; private set; } = "NotStarted";
    public string Completion { get; private set; } = "Pending";

    internal void Reset()
    {
        LastStatusCode = null;
        LastProbeOutcome = "NotStarted";
        Completion = "Pending";
    }
    internal void Started() => LastProbeOutcome = "RequestStarted";
    internal void Response(int status)
    {
        LastStatusCode = status;
        LastProbeOutcome = "HttpResponse";
    }
    internal void TransportFailure() => LastProbeOutcome = "TransportFailure";
    internal void Succeeded() => Completion = "Succeeded";
    internal void DeadlineExpired() => Completion = "DeadlineExpired";
    internal void Cancelled(bool caller) => Completion = caller ? "CallerCancelled" : "HandlerCancelled";
}
