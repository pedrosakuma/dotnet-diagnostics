using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>Invocation-scoped bridge; callbacks never perform synchronous storage operations.</summary>
internal interface ICaptureObservationSink
{
    /// <summary>
    /// Copies and admits a bounded observation without blocking. Rejections must be accounted
    /// by the sink; false never means an observation was successfully persisted.
    /// </summary>
    bool TryAppend(CaptureObservation observation);

    /// <summary>Reports transport loss when known; null remains unknown rather than zero.</summary>
    void ReportSourceLoss(string source, long? count);

    /// <summary>
    /// Announces retained compatibility material. Implementations must bound references and
    /// deduplicate by handle ID, since a delegating custom store can announce the same handle.
    /// </summary>
    void ArtifactRegistered(DiagnosticHandle handle, object artifact);

    /// <summary>Creates a child observation route before its callbacks are started.</summary>
    ICaptureObservationSink CreateChild(string kind, string name) => this;

    /// <summary>Records structured child completion without changing the caller's result.</summary>
    void ReportCompletion(DiagnosticError? error, bool cancelled, object? data = null) { }
}
