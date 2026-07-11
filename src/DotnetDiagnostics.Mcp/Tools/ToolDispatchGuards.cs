using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Mcp.Security;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class ToolDispatchGuards
{
    public static bool TryValidateDiscriminator<TResult>(
        string? value,
        IReadOnlyList<string> allowedValues,
        string parameterName,
        out string canonicalValue,
        out DiagnosticResult<TResult>? failure)
        where TResult : class
        => DiscriminatorDispatch.TryValidate<TResult>(value, allowedValues, parameterName, out canonicalValue, out failure);

    public static bool RequireScope<TResult>(
        BearerPrincipal? principal,
        string requiredScope,
        Func<string> messageFactory,
        out DiagnosticResult<TResult>? failure,
        NextActionHint? hint = null,
        string errorKind = "Forbidden",
        string? errorTarget = null,
        bool defaultErrorTargetToScope = true,
        Func<string>? errorDetailFactory = null)
        where TResult : class
        => Require(
            principal is null || principal.HasScope(requiredScope),
            messageFactory,
            errorDetailFactory,
            out failure,
            hint,
            errorKind,
            defaultErrorTargetToScope ? errorTarget ?? requiredScope : errorTarget);

    public static bool RequireExplicitScope<TResult>(
        BearerPrincipal? principal,
        string requiredScope,
        Func<string> messageFactory,
        out DiagnosticResult<TResult>? failure,
        NextActionHint? hint = null,
        string errorKind = "Forbidden",
        string? errorTarget = null,
        bool defaultErrorTargetToScope = true,
        Func<string>? errorDetailFactory = null)
        where TResult : class
        => Require(
            principal is null || principal.HasExplicitScope(requiredScope),
            messageFactory,
            errorDetailFactory,
            out failure,
            hint,
            errorKind,
            defaultErrorTargetToScope ? errorTarget ?? requiredScope : errorTarget);

    private static bool Require<TResult>(
        bool condition,
        Func<string> messageFactory,
        Func<string>? errorDetailFactory,
        out DiagnosticResult<TResult>? failure,
        NextActionHint? hint,
        string errorKind,
        string? errorTarget)
        where TResult : class
    {
        if (condition)
        {
            failure = null;
            return true;
        }

        var message = messageFactory();
        var detail = errorDetailFactory?.Invoke() ?? message;
        failure = hint is null
            ? DiagnosticResult.Fail<TResult>(message, new DiagnosticError(errorKind, detail, errorTarget))
            : DiagnosticResult.Fail<TResult>(message, new DiagnosticError(errorKind, detail, errorTarget), hint);
        return false;
    }
}
