using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Threads;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Tests;

internal static class LiveCompatibilityEvidence
{
    internal const string SamplePath = "/sample/MultiVersionSample.dll";

    internal static TargetRuntimeEvidence ReadTarget(int pid, int expectedMajor)
    {
        using var process = Process.GetProcessById(pid);
        var started = process.StartTime.ToUniversalTime();
        var info = ProcessInfoReflection.TryGet(new DiagnosticsClient(pid))
            ?? throw new InvalidOperationException("Diagnostic IPC process metadata is unavailable; no version fallback is permitted.");
        var arguments = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var modules = File.ReadLines($"/proc/{pid}/maps")
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1])
            .Where(path => path.EndsWith("/libcoreclr.so", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        using var current = Process.GetProcessById(pid);
        return ValidateTarget(pid, expectedMajor, info, arguments, modules, started, current.StartTime.ToUniversalTime());
    }

    internal static TargetRuntimeEvidence ValidateTarget(
        int pid, int expectedMajor, ProcessInfoSnapshot info, string[] arguments,
        string[] modules, DateTime started, DateTime currentStart)
    {
        Require(pid == 1 && info.ProcessId == (ulong)pid, "IPC response must identify the owned target PID1.");
        Require(started == currentStart, "Target process identity changed during IPC inspection.");
        Require(arguments.Length == 3 && arguments[1] == SamplePath && arguments[2] == "--live-compatibility",
            "Observed target command line is not the owned compatibility fixture.");
        Require(info.CommandLine.Contains(SamplePath, StringComparison.Ordinal) &&
            info.CommandLine.Contains("--live-compatibility", StringComparison.Ordinal),
            "IPC command line does not corroborate the observed fixture.");
        Require(info.OperatingSystem.Equals("Linux", StringComparison.OrdinalIgnoreCase) &&
            info.ProcessArchitecture.Equals("x64", StringComparison.OrdinalIgnoreCase),
            "IPC target must be Linux x64.");
        var actual = ParseProductVersion(info.ClrProductVersionString);
        Require(actual.Major == expectedMajor && actual.Major is 8 or 9 or 10,
            $"Actual IPC runtime {actual} does not match slot major {expectedMajor}.");
        Require(modules.Length == 1, "Expected exactly one distinct mapped CoreCLR module.");
        var expectedPath = $"/usr/share/dotnet/shared/Microsoft.NETCore.App/{actual}/libcoreclr.so";
        Require(modules[0] == expectedPath, "IPC product version does not match the target's mapped CoreCLR module.");
        return new TargetRuntimeEvidence(pid, started, info.ClrProductVersionString, actual.ToString(),
            info.CommandLine, modules[0], "diagnostic-ipc-process-info+procfs");
    }

    internal static Version ParseProductVersion(string value)
    {
        if (!Version.TryParse(value.Split('+')[0], out var version) || version.Major == 0 || version.Build < 0)
            throw new InvalidOperationException($"Missing/invalid diagnostic IPC runtime version: {value}");
        return version;
    }

    internal static void VerifyUnchanged(TargetRuntimeEvidence evidence)
    {
        using var process = Process.GetProcessById(evidence.ProcessId);
        Require(process.StartTime.ToUniversalTime() == evidence.StartedAtUtc,
            "Target process identity changed during the live capture.");
    }

    internal static (Guid Mvid, int Token) ReadFixtureIdentity(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var fixture = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
            .Single(type => reader.GetString(type.Name) == "CompatibilityFixture");
        var method = fixture.GetMethods().Single(handle =>
            reader.GetString(reader.GetMethodDefinition(handle).Name) == "ClosedGenericHold");
        Require(reader.GetMethodDefinition(method).GetGenericParameters().Count == 1, "Expected generic fixture definition.");
        return (reader.GetGuid(reader.GetModuleDefinition().Mvid), MetadataTokens.GetToken(method));
    }

    internal static ManagedStackFrame SelectGenericFrame(IEnumerable<ManagedStackFrame> frames, Guid mvid, int token)
    {
        return frames.Single(frame =>
        {
            if (frame.Identity?.ModuleVersionId != mvid || frame.Identity.MetadataToken != token ||
                frame.TypeFullName != "CompatibilityFixture")
                return false;
            var parsed = ClrMdMethodInstantiationEnricher.ParseClosedSignature(frame.DisplayName);
            return parsed.TypeFullName == "CompatibilityFixture" && parsed.MethodName == "ClosedGenericHold" &&
                parsed.GenericTypeArguments is { Type.Count: 0, Method.Count: 1 } args &&
                args.Method[0] == "System.Int32";
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record TargetRuntimeEvidence(
    int ProcessId, DateTime StartedAtUtc, string IpcProductVersion, string ActualVersion,
    string CommandLine, string MappedCoreClrPath, string Source);
