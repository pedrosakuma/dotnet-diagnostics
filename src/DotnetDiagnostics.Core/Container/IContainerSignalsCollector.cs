namespace DotnetDiagnostics.Core.Container;

/// <summary>
/// Collects a snapshot of the kernel-side container/cgroup signals for a target process. The
/// implementation is OS-specific: Linux reads cgroup v2 files; Windows is a stub returning
/// <see cref="ContainerSignals.InContainer"/>=false until job-object signals are wired up.
/// </summary>
public interface IContainerSignalsCollector
{
    Task<ContainerSignals> CollectAsync(int processId, CancellationToken cancellationToken = default);
}
