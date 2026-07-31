using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;

namespace DotnetDiagnostics.Cli;

internal enum CliSafetyPreflightDisposition
{
    Proceed,
    Explained,
    Rejected,
}

internal static class CliSafetyPreflight
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task<CliSafetyPreflightDisposition> RunAsync(
        CliOptions options,
        IDiagnosticHandleStore? handles,
        CliExecutionContext context,
        bool interactive,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        string? artifactRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var request = CliInvocationSafety.CreateRequest(options, handles);
        var safety = CliInvocationSafety.ResolveForPreflight(request);
        var artifactPath = ResolveArtifactPath(options, safety, artifactRoot);

        if (options.ExplainRisk)
        {
            await WriteExplanationAsync(
                options,
                request.Operation,
                safety,
                artifactPath,
                stdout).ConfigureAwait(false);
            return CliSafetyPreflightDisposition.Explained;
        }

        if (IsDumpPreview(options))
        {
            await stderr.WriteLineAsync(
                $"SAFETY preview [{RiskName(safety.RiskLevel)}] dump: no dump will be written; add --confirm and --acknowledge-risk {RiskName(safety.RiskLevel)} to execute. Use --explain-risk for details.")
                .ConfigureAwait(false);
            return CliSafetyPreflightDisposition.Proceed;
        }

        if (safety.RiskLevel == InvocationRiskLevel.Low)
        {
            return CliSafetyPreflightDisposition.Proceed;
        }

        if (safety.RiskLevel == InvocationRiskLevel.Moderate)
        {
            await stderr.WriteLineAsync(
                $"SAFETY warning [moderate] {options.Command}: {safety.Reason} Use --explain-risk for details.")
                .ConfigureAwait(false);
            return CliSafetyPreflightDisposition.Proceed;
        }

        await WriteDetailedWarningAsync(
            options.Command!,
            safety,
            artifactPath,
            stderr).ConfigureAwait(false);

        var expectedAcknowledgement = RiskName(safety.RiskLevel);
        if (context == CliExecutionContext.Session && interactive)
        {
            await stderr.WriteAsync(
                $"Type '{expectedAcknowledgement}' to continue, or press Enter to cancel: ")
                .ConfigureAwait(false);
            await stderr.FlushAsync(cancellationToken).ConfigureAwait(false);
            var response = await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(response?.Trim(), expectedAcknowledgement, StringComparison.OrdinalIgnoreCase))
            {
                return CliSafetyPreflightDisposition.Proceed;
            }

            await stderr.WriteLineAsync("SAFETY cancelled; the operation was not executed.").ConfigureAwait(false);
            return CliSafetyPreflightDisposition.Rejected;
        }

        if (string.Equals(
                options.AcknowledgeRisk?.Trim(),
                expectedAcknowledgement,
                StringComparison.OrdinalIgnoreCase))
        {
            return CliSafetyPreflightDisposition.Proceed;
        }

        var mismatch = options.AcknowledgeRisk is null
            ? "Acknowledgement required."
            : "Acknowledgement does not match the resolved risk.";
        await stderr.WriteLineAsync(
            $"{mismatch} Re-run with --acknowledge-risk {expectedAcknowledgement}; the operation was not executed.")
            .ConfigureAwait(false);
        return CliSafetyPreflightDisposition.Rejected;
    }

    private static bool IsDumpPreview(CliOptions options)
        => string.Equals(options.Command, "dump", StringComparison.Ordinal) && !options.Confirm;

    private static async Task WriteExplanationAsync(
        CliOptions options,
        string operation,
        InvocationSafetyDescriptor safety,
        string? artifactPath,
        TextWriter stdout)
    {
        var acknowledgement = safety.RiskLevel >= InvocationRiskLevel.High
            ? $"--acknowledge-risk {RiskName(safety.RiskLevel)}"
            : null;
        var explanation = new CliSafetyExplanation(
            operation,
            safety,
            artifactPath,
            acknowledgement,
            IsDumpPreview(options) ? "--confirm" : null,
            Executed: false);

        if (options.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(explanation, JsonOptions)).ConfigureAwait(false);
            return;
        }

        await stdout.WriteLineAsync($"operation: {operation}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"riskLevel: {RiskName(safety.RiskLevel)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"approvalPolicy: {EnumName(safety.ApprovalPolicy)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"reason: {safety.Reason}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"targetImpact: {JoinNames(safety.TargetImpact)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"dataExposure: {JoinNames(safety.DataExposure)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"sideEffects: {JoinNames(safety.SideEffects)}").ConfigureAwait(false);
        if (artifactPath is not null)
        {
            await stdout.WriteLineAsync($"artifactPath: {artifactPath}").ConfigureAwait(false);
        }

        if (acknowledgement is not null)
        {
            await stdout.WriteLineAsync($"acknowledgement: {acknowledgement}").ConfigureAwait(false);
        }

        if (IsDumpPreview(options))
        {
            await stdout.WriteLineAsync("additionalExecutionFlag: --confirm").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync("executed: false").ConfigureAwait(false);
    }

    private static async Task WriteDetailedWarningAsync(
        string command,
        InvocationSafetyDescriptor safety,
        string? artifactPath,
        TextWriter stderr)
    {
        await stderr.WriteLineAsync($"SAFETY {RiskName(safety.RiskLevel)} operation '{command}'").ConfigureAwait(false);
        await stderr.WriteLineAsync($"reason: {safety.Reason}").ConfigureAwait(false);
        await stderr.WriteLineAsync($"targetImpact: {JoinNames(safety.TargetImpact)}").ConfigureAwait(false);
        await stderr.WriteLineAsync($"dataExposure: {JoinNames(safety.DataExposure)}").ConfigureAwait(false);
        await stderr.WriteLineAsync($"sideEffects: {JoinNames(safety.SideEffects)}").ConfigureAwait(false);
        if (artifactPath is not null)
        {
            await stderr.WriteLineAsync($"artifactPath: {artifactPath}").ConfigureAwait(false);
        }
    }

    private static string? ResolveArtifactPath(
        CliOptions options,
        InvocationSafetyDescriptor safety,
        string? artifactRoot)
    {
        if (!string.IsNullOrWhiteSpace(options.SavePath))
        {
            return TryGetFullPath(options.SavePath);
        }

        if (string.Equals(options.Command, "get-bytes", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(options.OutDir))
        {
            return TryGetFullPath(options.OutDir);
        }

        if (string.Equals(options.Command, "dump", StringComparison.Ordinal)
            || safety.SideEffects.Contains(InvocationSideEffect.WritesArtifact)
            || safety.SideEffects.Contains(InvocationSideEffect.ExportsRawBytes))
        {
            return artifactRoot is null ? null : TryGetFullPath(artifactRoot);
        }

        return null;
    }

    private static string TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string JoinNames<T>(IEnumerable<T> values)
        where T : struct, Enum
    {
        var names = values.Select(EnumName).ToArray();
        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    private static string RiskName(InvocationRiskLevel value) => EnumName(value);

    private static string EnumName<T>(T value)
        where T : struct, Enum
        => JsonSerializer.Serialize(value, JsonOptions).Trim('"');

    private sealed record CliSafetyExplanation(
        string Operation,
        InvocationSafetyDescriptor Safety,
        string? ArtifactPath,
        string? Acknowledgement,
        string? AdditionalExecutionFlag,
        bool Executed);
}
