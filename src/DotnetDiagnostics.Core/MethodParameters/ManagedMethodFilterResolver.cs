using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.MethodParameters;

internal sealed class ManagedMethodFilterResolver(MvidReader mvidReader)
{
    public IReadOnlyList<ResolvedMethodBinding> Resolve(int processId, IReadOnlyList<MethodFilter> filters)
    {
        var bindings = new List<ResolvedMethodBinding>();
        foreach (var filter in filters)
        {
            bindings.AddRange(ResolveFilter(processId, filter));
        }

        return bindings;
    }

    private List<ResolvedMethodBinding> ResolveFilter(int processId, MethodFilter filter)
    {
        var moduleCandidates = DiscoverModulePaths(processId, filter.ModuleName);
        if (moduleCandidates.Length == 0)
        {
            throw new ArgumentException(
                $"No loaded managed module named '{filter.ModuleName}' was discovered in pid {processId}.",
                nameof(filter));
        }

        var selectedModules = new List<(string Path, Guid? Mvid)>();
        foreach (var modulePath in moduleCandidates)
        {
            selectedModules.Add((modulePath, mvidReader.TryRead(modulePath)));
        }

        if (!string.IsNullOrWhiteSpace(filter.ModuleVersionId))
        {
            if (!Guid.TryParse(filter.ModuleVersionId, out var requestedMvid))
            {
                throw new ArgumentException(
                    $"moduleVersionId '{filter.ModuleVersionId}' is not a valid GUID.",
                    nameof(filter));
            }

            selectedModules = selectedModules
                .Where(candidate => candidate.Mvid == requestedMvid)
                .ToList();

            if (selectedModules.Count == 0)
            {
                throw new ArgumentException(
                    $"No loaded copy of '{filter.ModuleName}' matched moduleVersionId '{filter.ModuleVersionId}'.",
                    nameof(filter));
            }
        }
        else
        {
            var distinctMvids = selectedModules
                .Select(candidate => candidate.Mvid)
                .Where(mvid => mvid.HasValue)
                .Select(mvid => mvid!.Value)
                .Distinct()
                .ToArray();
            if (distinctMvids.Length > 1)
            {
                var candidates = string.Join(", ", distinctMvids.Select(mvid => mvid.ToString("D")));
                throw new ArgumentException(
                    $"Filter '{filter.ModuleName}!{filter.TypeName}.{filter.MethodName}' matched multiple loaded moduleVersionId values ({candidates}). Pass moduleVersionId explicitly.",
                    nameof(filter));
            }
        }

        var results = new List<ResolvedMethodBinding>();
        foreach (var (modulePath, _) in selectedModules)
        {
            results.AddRange(ResolveFromAssembly(modulePath, filter));
        }

        if (results.Count == 0)
        {
            throw new ArgumentException(
                $"No method in loaded module '{filter.ModuleName}' matched filter '{filter.TypeName}.{filter.MethodName}'.",
                nameof(filter));
        }

        return results;
    }

    private static IReadOnlyList<ResolvedMethodBinding> ResolveFromAssembly(string assemblyPath, MethodFilter filter)
    {
        var loadContext = new ProbeLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(filter.TypeName, throwOnError: false, ignoreCase: false);
            if (type is null)
            {
                return Array.Empty<ResolvedMethodBinding>();
            }

            var candidates = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(method => string.Equals(method.Name, filter.MethodName, StringComparison.Ordinal))
                .Where(method => !filter.GenericArity.HasValue || method.GetGenericArguments().Length == filter.GenericArity.Value)
                .Where(method => SignatureMatches(method, filter.Signature))
                .Select(method =>
                {
                    var identity = new ResolvedMethodIdentity(
                        Path.GetFileName(assemblyPath),
                        method.Module.ModuleVersionId.ToString("D"),
                        type.FullName ?? filter.TypeName,
                        method.Name,
                        method.GetGenericArguments().Length,
                        method.MetadataToken,
                        method.GetParameters().Select(parameter => ToDisplayName(parameter.ParameterType)).ToArray());
                    return new ResolvedMethodBinding(
                        filter,
                        assemblyPath,
                        identity,
                        new MethodDescription
                        {
                            ModuleName = identity.ModuleName,
                            TypeName = identity.TypeName,
                            MethodName = identity.MethodName,
                        });
                })
                .ToList();

            return candidates;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static bool SignatureMatches(MethodInfo method, IReadOnlyList<string>? signature)
    {
        if (signature is null || signature.Count == 0)
        {
            return true;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != signature.Count)
        {
            return false;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (!string.Equals(ToDisplayName(parameters[i].ParameterType), signature[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ToDisplayName(Type type)
    {
        if (type.IsByRef)
        {
            return ToDisplayName(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return ToDisplayName(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            var commas = new string(',', type.GetArrayRank() - 1);
            return $"{ToDisplayName(type.GetElementType()!)}[{commas}]";
        }

        return type.FullName ?? type.Name;
    }

    private static string[] DiscoverModulePaths(int processId, string moduleName)
    {
        if (OperatingSystem.IsLinux())
        {
            return DiscoverModulePathsFromProcMaps(processId, moduleName);
        }

        using var process = Process.GetProcessById(processId);
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in process.Modules)
        {
            try
            {
                if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(module.FileName))
                {
                    matches.Add(module.FileName);
                }
            }
            finally
            {
                module.Dispose();
            }
        }

        return matches.ToArray();
    }

    private static string[] DiscoverModulePathsFromProcMaps(int processId, string moduleName)
    {
        var mapsPath = $"/proc/{processId}/maps";
        if (!File.Exists(mapsPath))
        {
            return Array.Empty<string>();
        }

        var matches = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(mapsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var path = TryExtractMappedPath(line);
            if (path is null)
            {
                continue;
            }

            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(path), moduleName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(path);
            }
        }

        return matches.ToArray();
    }

    private static string? TryExtractMappedPath(string line)
    {
        var slashIndex = line.IndexOf('/');
        if (slashIndex < 0)
        {
            return null;
        }

        var path = line[slashIndex..].Trim();
        return File.Exists(path) ? path : null;
    }

    private sealed class ProbeLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolvedPath is null ? null : LoadFromAssemblyPath(resolvedPath);
        }
    }
}
