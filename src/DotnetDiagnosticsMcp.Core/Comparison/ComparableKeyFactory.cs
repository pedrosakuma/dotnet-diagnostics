using System.Globalization;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;

namespace DotnetDiagnosticsMcp.Core.Comparison;

internal static class ComparableKeyFactory
{
    public static ComparableKey ForType(string kind, TypeIdentity? identity, string typeFullName, string? moduleName)
    {
        var effectiveModule = identity?.ModuleName ?? moduleName;
        return new ComparableKey(
            Kind: kind,
            StableId: StableTypeId(effectiveModule, typeFullName),
            ExactId: ExactId(identity?.ModuleVersionId, identity?.MetadataToken),
            Module: effectiveModule,
            TypeName: typeFullName);
    }

    public static ComparableKey ForMethod(string kind, SymbolRef symbol, MethodIdentity? identity)
    {
        var effectiveModule = identity?.ModuleName ?? symbol.Module;
        var effectiveMethod = identity?.ClosedSignature ?? symbol.MethodFullName;
        return new ComparableKey(
            Kind: kind,
            StableId: StableMethodId(effectiveModule, effectiveMethod),
            ExactId: ExactId(identity?.ModuleVersionId, identity?.MetadataToken),
            Module: effectiveModule,
            TypeName: identity?.TypeFullName,
            MethodName: identity?.MethodName ?? symbol.MethodFullName,
            GenericSignature: identity?.ClosedSignature);
    }

    private static string StableTypeId(string? moduleName, string typeFullName)
        => string.IsNullOrWhiteSpace(moduleName)
            ? typeFullName
            : string.Concat(moduleName, "!", typeFullName);

    private static string StableMethodId(string? moduleName, string methodFullName)
        => string.IsNullOrWhiteSpace(moduleName)
            ? methodFullName
            : string.Concat(moduleName, "!", methodFullName);

    private static string? ExactId(Guid? moduleVersionId, int? metadataToken)
        => moduleVersionId is Guid mvid && metadataToken is int token
            ? string.Concat(mvid.ToString("D", CultureInfo.InvariantCulture), ":0x", token.ToString("X8", CultureInfo.InvariantCulture))
            : null;
}
