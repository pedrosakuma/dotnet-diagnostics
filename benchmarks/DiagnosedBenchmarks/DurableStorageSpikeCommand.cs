using System.Text.Json;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

namespace DiagnosedBenchmarks;

internal static class DurableStorageSpikeCommand
{
    private static readonly string[] MonitoredCommands =
    [
        "monitored-plan",
        "monitored-host-facts",
        "monitored-runtime-proof",
        "monitored-validate",
        "monitored-run",
        "monitored-worker",
        "monitored-component-evidence",
        "prevalidation-plan",
        "prevalidation-validate",
        "prevalidation-run",
        "prevalidation-inspect",
    ];

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
                case "monitored-describe":
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            schema = MonitoredProtocolVersions.ManifestSchema,
                            protocolRevision = 4,
                            executionGate = "resolved-manifest-and-parent-authorization-required",
                            candidates = MonitoredAdapterRegistry.Describe(),
                            commands = MonitoredCommands,
                        },
                        JsonOptions));
                    return 0;
                case "monitored-plan":
                    return CreateMonitoredPlan(args[1..]);
                case "monitored-host-facts":
                    return ReadMonitoredHostFacts(args[1..]);
                case "monitored-runtime-proof":
                    return CreateRuntimeProof(args[1..]);
                case "monitored-validate":
                    return ValidateMonitoredManifest(args[1..]);
                case "monitored-run":
                    return RunMonitoredCampaign(args[1..]);
                case "monitored-worker":
                    return RunMonitoredWorker(args[1..]);
                case "monitored-component-evidence":
                    return CreateMonitorComponentEvidence(args[1..]);
                case "prevalidation-plan":
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        schema = PrevalidationProtocol.PlanSchema,
                        scope = PrevalidationProtocol.Scope,
                        addendumCommit = PrevalidationProtocol.AddendumCommit,
                        addendumSha256 = PrevalidationProtocol.AddendumSha256,
                        protocolSha256 = MonitoredProtocolVersions.SuccessorProtocolSha256,
                        historicalReportSha256 = PrevalidationProtocol.HistoricalReportSha256,
                        probes = PrevalidationProtocol.Plan(),
                        bounds = PrevalidationProtocol.Bounds(),
                        derivedSuiteIdentityBound = PrevalidationLayout.DeriveSuiteIdentityBound(),
                        runtimeEnvironmentSha256 = PrevalidationProtocol.RuntimeEnvironmentHash(),
                        summarySchema = PrevalidationProtocol.SummarySchema,
                        contextSummaryFieldMapSha256 = PrevalidationProtocol.ContextSummaryFieldMapSha256,
                    }, JsonOptions));
                    return 0;
                case "prevalidation-validate":
                case "prevalidation-run":
                    return RunPrevalidation(command, args[1..]);
                case "prevalidation-worker":
                    return MonitoredWorkerExecutor.RunAsync(RequireOption(args, "--descriptor"), prevalidation: true)
                        .GetAwaiter().GetResult();
                case "prevalidation-harness":
                    return PrevalidationExecutor.RunHarnessAsync(RequireOption(args, "--descriptor"))
                        .GetAwaiter().GetResult();
                case "prevalidation-admission":
                    Console.WriteLine(JsonSerializer.Serialize(PrevalidationProtocol.Validate(
                        RequireOption(args, "--repository-root"), RequireOption(args, "--manifest")),
                        PrevalidationProtocol.Json));
                    return 0;
                case "prevalidation-inspect":
                    Console.WriteLine(JsonSerializer.Serialize(PrevalidationReportValidation.Validate(
                        RequireOption(args, "--manifest")), PrevalidationProtocol.Json));
                    return 0;
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

    private static string RequireOption(string[] args, string name)
        => ReadOption(args, name) ?? throw new DurableStorageExperimentException(
            "MissingArgument", $"The prevalidation route requires {name}.");

    private static int RunPrevalidation(string command, string[] args)
    {
        var root = RequireOption(args, "--repository-root");
        var manifest = RequireOption(args, "--manifest");
        if (command == "prevalidation-run")
        {
            return PrevalidationExecutor.RunAsync(root, manifest, CancellationToken.None).GetAwaiter().GetResult();
        }
        var validated = PrevalidationAdmission.ValidateAsync(root, manifest, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scope = PrevalidationProtocol.Scope,
            validated.ManifestSha256,
            stage = "prevalidation-component-proof-and-authorization",
            campaignAdmissionGranted = false,
            entries = 8,
            derivedSuiteIdentityBound = PrevalidationLayout.DeriveSuiteIdentityBound(),
        }, JsonOptions));
        return 0;
    }

    private static int CreateMonitoredPlan(string[] args)
    {
        var protocol = ReadOption(args, "--protocol");
        var repositoryRoot = ReadOption(args, "--repository-root");
        var output = ReadOption(args, "--output");
        if (protocol is null || repositoryRoot is null || output is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-plan requires --protocol <repository-relative-path>, --repository-root <path>, and --output <path>.");
        }
        var plan = MonitoredExecutionPlanner.CreatePlan(repositoryRoot, protocol);
        MonitoredFile.WriteNewJson(Path.GetFullPath(output), plan);
        Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
        return 0;
    }

    private static int ReadMonitoredHostFacts(string[] args)
    {
        var artifactRoot = ReadOption(args, "--artifact-root");
        if (artifactRoot is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-host-facts requires --artifact-root <existing-private-root>.");
        }
        var facts = MonitoredHostFactsReader.Read(Path.GetFullPath(artifactRoot));
        Console.WriteLine(JsonSerializer.Serialize(facts, JsonOptions));
        return 0;
    }

    private static int ValidateMonitoredManifest(string[] args)
    {
        var manifest = ReadOption(args, "--manifest");
        var repositoryRoot = ReadOption(args, "--repository-root");
        if (manifest is null || repositoryRoot is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-validate requires --manifest <path> and --repository-root <path>.");
        }

        var validation = MonitoredRunManifestValidator.Validate(
            repositoryRoot,
            manifest,
            requireAuthorization: true);
        Console.WriteLine(JsonSerializer.Serialize(validation.Summary, JsonOptions));
        return 0;
    }

    private static int CreateRuntimeProof(string[] args)
    {
        var output = ReadOption(args, "--output");
        if (output is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-runtime-proof requires --output <path>.");
        }
        var proof = LinuxRuntimeMemoryClassifier.DiscoverCurrentProcessProof();
        MonitoredFile.WriteNewJson(Path.GetFullPath(output), proof);
        Console.WriteLine(JsonSerializer.Serialize(proof, JsonOptions));
        return 0;
    }

    private static int RunMonitoredCampaign(string[] args)
    {
        var manifest = ReadOption(args, "--manifest");
        var repositoryRoot = ReadOption(args, "--repository-root");
        if (manifest is null || repositoryRoot is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-run requires --manifest <path> and --repository-root <path>.");
        }
        return MonitoredCampaignRunner.RunAsync(
                repositoryRoot,
                manifest,
                CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static int RunMonitoredWorker(string[] args)
    {
        var descriptor = ReadOption(args, "--descriptor");
        if (descriptor is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-worker requires --descriptor <path>.");
        }
        return MonitoredWorkerExecutor.RunAsync(descriptor).GetAwaiter().GetResult();
    }

    private static int CreateMonitorComponentEvidence(string[] args)
    {
        var output = ReadOption(args, "--output");
        var runnerCommit = ReadOption(args, "--runner-commit");
        var monitorCommit = ReadOption(args, "--monitor-commit");
        var attribution = ReadOption(args, "--attribution");
        if (output is null || runnerCommit is null || monitorCommit is null || attribution is null)
        {
            throw new DurableStorageExperimentException(
                "MissingArgument",
                "monitored-component-evidence requires --output <path>, --runner-commit <sha>, --monitor-commit <sha>, and --attribution <path>.");
        }
        var evidence = MonitoredComponentEvidenceGenerator.GenerateAsync(
                Path.GetFullPath(output),
                runnerCommit,
                monitorCommit,
                Path.GetFullPath(attribution),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
        return 0;
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
        writer.WriteLine("  monitored-describe");
        writer.WriteLine("  monitored-plan --protocol <path> --repository-root <path> --output <path>");
        writer.WriteLine("  monitored-host-facts --artifact-root <existing-private-root>");
        writer.WriteLine("  monitored-runtime-proof --output <path>");
        writer.WriteLine("  monitored-validate --manifest <path> --repository-root <path>");
        writer.WriteLine("  monitored-run --manifest <path> --repository-root <path>");
        writer.WriteLine("  monitored-worker --descriptor <path>");
        writer.WriteLine("  monitored-component-evidence --output <path> --runner-commit <sha> --monitor-commit <sha> --attribution <path>");
        writer.WriteLine("  prevalidation-plan");
        writer.WriteLine("  prevalidation-validate --manifest <path> --repository-root <path>");
        writer.WriteLine("  prevalidation-run --manifest <path> --repository-root <path>");
        writer.WriteLine("  prevalidation-inspect --manifest <path>");
        writer.WriteLine();
        writer.WriteLine("Revision-3 execution remains closed. Monitored revision-4 execution requires a fully resolved manifest and immutable parent authorization receipt.");
        writer.WriteLine("Prevalidation is a separate eight-entry, unscored authorization. It never grants campaign admission.");
    }
}
