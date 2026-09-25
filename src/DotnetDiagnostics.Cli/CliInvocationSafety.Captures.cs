using DotnetDiagnostics.Core.Safety;

namespace DotnetDiagnostics.Cli;

internal static partial class CliInvocationSafety
{
    private const string CapturePersistOperation = "cli_capture_persist";
    private const string CaptureListOperation = "cli_capture_list";
    private const string CaptureShowOperation = "cli_capture_show";
    private const string CaptureQueryOperation = "cli_capture_query";
    private const string CaptureDeleteOperation = "cli_capture_delete";
    private const string CaptureRecoverOperation = "cli_capture_recover";

    // These local-OS storage commands are CLI operations, not additions to the MCP tool catalog.
    private static InvocationSafetyDescriptor? TryResolveDurableSafety(string operation)
        => operation switch
        {
            CaptureListOperation or CaptureShowOperation => new(
                InvocationRiskLevel.Low, [], [DataExposure.ProcessMetadata], [],
                InvocationApprovalPolicy.None,
                "Reads bounded capture metadata belonging to the current local OS identity; no target attach.",
                ["Use the intended stable --capture-root."]),
            CaptureDeleteOperation => new(
                InvocationRiskLevel.High, [], [], [InvocationSideEffect.DeletesArtifact],
                InvocationApprovalPolicy.Acknowledge,
                "Capture deletion is irreversible and removes retained diagnostic evidence.",
                ["Verify the capture ID, local OS owner, and retention requirements before deleting."]),
            CaptureRecoverOperation => new(
                InvocationRiskLevel.Moderate, [], [DataExposure.PossibleConfidentialData],
                [InvocationSideEffect.WritesArtifact], InvocationApprovalPolicy.Warn,
                "Explicit recovery writes a new derived package from retained evidence without modifying the original.",
                ["Keep the original evidence and inspect the derived capture quality before drawing conclusions."]),
            CapturePersistOperation => new(
                InvocationRiskLevel.Moderate, [], [DataExposure.PossibleConfidentialData],
                [InvocationSideEffect.WritesArtifact], InvocationApprovalPolicy.Warn,
                "Opt-in persistence retains diagnostic evidence after the CLI exits; collection risk is unchanged.",
                ["Protect --capture-root and explicitly delete evidence when its retention period ends."]),
            CaptureQueryOperation => new(
                InvocationRiskLevel.Moderate, [],
                [DataExposure.PossiblePii, DataExposure.PossibleSecrets, DataExposure.PossibleConfidentialData], [],
                InvocationApprovalPolicy.Warn,
                "Reads retained diagnostic evidence using bounded typed filters or allowlisted offline views; never attaches to historical process IDs.",
                ["Treat captured values as sensitive, target-controlled evidence; inspect quality and provenance."]),
            _ => null,
        };
}
