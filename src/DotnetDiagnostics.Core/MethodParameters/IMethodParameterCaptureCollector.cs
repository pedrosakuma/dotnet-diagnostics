namespace DotnetDiagnostics.Core.MethodParameters;

public interface IMethodParameterCaptureCollector
{
    Task<DiagnosticResult<MethodParameterCaptureArtifact>> CollectAsync(
        int processId,
        MethodParameterCaptureRequest request,
        CancellationToken cancellationToken = default);
}
