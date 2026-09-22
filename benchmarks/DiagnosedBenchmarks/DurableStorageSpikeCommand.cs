using System.Text.Json;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike;

namespace DiagnosedBenchmarks;

internal static class DurableStorageSpikeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static int Run(string[] args)
    {
        var registry = new DurableStorageAdapterRegistry();
        var command = args.Length == 0 ? "help" : args[0];
        try
        {
            switch (command)
            {
                case "help":
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "describe":
                    Console.WriteLine(JsonSerializer.Serialize(
                        DurableStorageExperimentFoundation.Describe(registry),
                        JsonOptions));
                    return 0;
                case "validate-manifest":
                    return ValidateManifest(args[1..], registry);
                default:
                    Console.Error.WriteLine($"Unknown durable-capture-spike command '{command}'.");
                    PrintHelp(Console.Error);
                    return 2;
            }
        }
        catch (DurableStorageExperimentException exception)
        {
            Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
            return 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"InvalidManifestJson: {exception.Message}");
            return 2;
        }
    }

    private static int ValidateManifest(string[] args, DurableStorageAdapterRegistry registry)
    {
        var manifest = ReadOption(args, "--manifest");
        var repositoryRoot = ReadOption(args, "--repository-root");
        if (manifest is null || repositoryRoot is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "validate-manifest requires --manifest <path> and --repository-root <path>.");
        }

        var result = DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            manifest,
            registry);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal)
                && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("durable-capture-spike commands:");
        writer.WriteLine("  help");
        writer.WriteLine("  describe");
        writer.WriteLine("  validate-manifest --manifest <path> --repository-root <path>");
        writer.WriteLine();
        writer.WriteLine("Execution is intentionally closed; no adapter or campaign command is registered.");
    }
}
