using System.Reflection;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnosticsMcp.Core.Internal;

/// <summary>
/// Bridges to the internal <c>DiagnosticsClient.GetProcessInfo()</c> method via reflection.
/// The public NuGet surface does not expose process metadata directly; this helper lets us read it
/// without forking the library. If reflection fails (API removed/renamed), callers should fall back
/// to <see cref="System.Diagnostics.Process"/>.
/// </summary>
internal static class ProcessInfoReflection
{
    private static readonly MethodInfo? GetProcessInfoMethod =
        typeof(DiagnosticsClient).GetMethod(
            "GetProcessInfo",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

    public static ProcessInfoSnapshot? TryGet(DiagnosticsClient client)
    {
        if (GetProcessInfoMethod is null)
        {
            return null;
        }

        try
        {
            var raw = GetProcessInfoMethod.Invoke(client, parameters: null);
            if (raw is null)
            {
                return null;
            }

            var type = raw.GetType();
            return new ProcessInfoSnapshot(
                ProcessId: GetPropertyValue<ulong>(raw, type, "ProcessId"),
                CommandLine: GetPropertyValue<string>(raw, type, "CommandLine") ?? string.Empty,
                OperatingSystem: GetPropertyValue<string>(raw, type, "OperatingSystem") ?? string.Empty,
                ProcessArchitecture: GetPropertyValue<string>(raw, type, "ProcessArchitecture") ?? string.Empty,
                ClrProductVersionString: GetPropertyValue<string>(raw, type, "ClrProductVersionString") ?? string.Empty,
                ManagedEntrypointAssemblyName: GetPropertyValue<string>(raw, type, "ManagedEntrypointAssemblyName"),
                PortableRuntimeIdentifier: GetPropertyValue<string>(raw, type, "PortableRuntimeIdentifier") ?? string.Empty);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static T? GetPropertyValue<T>(object instance, Type type, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null)
        {
            return default;
        }

        var value = prop.GetValue(instance);
        if (value is null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed record ProcessInfoSnapshot(
    ulong ProcessId,
    string CommandLine,
    string OperatingSystem,
    string ProcessArchitecture,
    string ClrProductVersionString,
    string? ManagedEntrypointAssemblyName,
    string PortableRuntimeIdentifier);
