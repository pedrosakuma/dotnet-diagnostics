using DotnetDiagnosticsMcp.Core;

namespace DotnetDiagnosticsMcp.Core.Tools.Dispatch;

/// <summary>
/// Tiny shared helper for tools that branch on a string discriminator (<c>kind=</c>,
/// <c>view=</c>, <c>source=</c> …). Returns a structured failure envelope when the
/// discriminator is unrecognized, instead of throwing — matches the
/// <c>"InvalidArgument"</c> shape already used across the diagnostic tool surface
/// (see <see cref="DiagnosticError"/>).
/// </summary>
/// <remarks>
/// <para>Matching is <b>case-sensitive ordinal</b>. Tool discriminators in this codebase
/// (<c>view=summary</c>, <c>kind=cpu-sample</c>, <c>source=process</c>, …) are stable
/// machine identifiers; a case-insensitive default would let typos like <c>SUMMARY</c>
/// silently succeed and mask client bugs. The case-sensitive contract is enforced by
/// <see cref="TryValidate{T}"/>'s lookup.</para>
/// </remarks>
public static class DiscriminatorDispatch
{
    /// <summary>
    /// Validates that <paramref name="value"/> appears in <paramref name="allowed"/>. On
    /// success returns the trimmed canonical value; on failure returns a
    /// <see cref="DiagnosticResult{T}"/> failure envelope whose error <c>Kind</c> is
    /// <c>"InvalidArgument"</c>, listing the allowed values so the caller (typically an
    /// LLM) can retry without re-reading the tool description.
    /// </summary>
    /// <param name="value">The user-supplied discriminator value.</param>
    /// <param name="allowed">The allowed value set. Order is preserved when rendered.</param>
    /// <param name="parameterName">Name of the parameter for the error envelope (e.g. <c>"kind"</c>).</param>
    /// <param name="canonical">Set to the matched value on success; ignored on failure.</param>
    /// <param name="failure">Set to the failure envelope on miss; <c>null</c> on success.</param>
    /// <typeparam name="T">Payload type of the surrounding tool's <see cref="DiagnosticResult{T}"/>.</typeparam>
    /// <returns><c>true</c> when the value was accepted; <c>false</c> when a failure envelope was produced.</returns>
    public static bool TryValidate<T>(
        string? value,
        IReadOnlyList<string> allowed,
        string parameterName,
        out string canonical,
        out DiagnosticResult<T>? failure)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            canonical = string.Empty;
            failure = BuildFailure<T>(value, allowed, parameterName, "value is required");
            return false;
        }

        var trimmed = value.Trim();
        for (int i = 0; i < allowed.Count; i++)
        {
            // Ordinal comparison: discriminators are machine-stable identifiers, see the
            // class remarks for the deliberate case-sensitivity decision.
            if (string.Equals(allowed[i], trimmed, StringComparison.Ordinal))
            {
                canonical = allowed[i];
                failure = null;
                return true;
            }
        }

        canonical = string.Empty;
        failure = BuildFailure<T>(value, allowed, parameterName, "value is not in the allowed set");
        return false;
    }

    private static DiagnosticResult<T> BuildFailure<T>(
        string? value,
        IReadOnlyList<string> allowed,
        string parameterName,
        string reason)
    {
        var allowedRendered = allowed.Count == 0 ? "(none)" : string.Join(", ", allowed);
        var presented = value is null ? "(null)" : $"'{value}'";
        var message = $"Argument '{parameterName}' {reason}. Presented {presented}; allowed: {allowedRendered}.";
        return DiagnosticResult.Fail<T>(
            message,
            new DiagnosticError("InvalidArgument", message, parameterName));
    }
}
