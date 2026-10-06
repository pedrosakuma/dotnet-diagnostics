using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Versioned bidirectional JSON Lines protocol for <c>dotnet-diagnostics-cli stream --protocol jsonl</c>.
/// Supports one or more concurrently-running live signal kinds (<c>counters</c>, <c>gc</c>) composed
/// behind a single <see cref="ComposedDiagnosticSession"/> per <c>start</c> request (#1099), plus a
/// one-shot <c>capture</c> request/response pair for point-in-time kinds (currently <c>cpu</c>) that
/// does not open a live session. The single-kind counters wire shape from #1091/#1092/#1093 remains
/// valid: a <c>start</c> message without a <c>kinds</c> array is treated as a single implicit
/// <c>counters</c> kind using its top-level fields, so existing clients (the VS Code extension) do
/// not need changes.
/// </summary>
internal static class CliStreamingProtocol
{
    private const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Live signal kinds known to the <c>start</c>/<c>observation</c> flow.</summary>
    private static readonly IReadOnlyDictionary<string, KindDescriptor> LiveKinds =
        new Dictionary<string, KindDescriptor>(StringComparer.Ordinal)
        {
            ["counters"] = new KindDescriptor(
                "counters",
                TryParseCounterOptions,
                static (services, processId, options) => services.GetRequiredService<ICounterSessionFactory>()
                    .CreateSession(processId, (CounterSessionOptions)options),
                AttachCounterForwarding),
            ["gc"] = new KindDescriptor(
                "gc",
                TryParseGcOptions,
                static (services, processId, options) => services.GetRequiredService<IGcSessionFactory>()
                    .CreateSession(processId, (GcSessionOptions)options),
                AttachGcForwarding),
        };

    /// <summary>Point-in-time kinds known to the <c>capture</c> request/response flow.</summary>
    private static readonly HashSet<string> CaptureKinds = new(StringComparer.Ordinal) { "cpu", "heap" };

    /// <summary>Heap sources accepted by a <c>capture</c> request with <c>kind="heap"</c>.</summary>
    private static readonly HashSet<string> HeapCaptureSources = new(StringComparer.Ordinal)
    {
        DiagnosticOperationCatalog.HeapSources.Live,
        DiagnosticOperationCatalog.HeapSources.GcDump,
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<IHost> buildHost,
        CancellationToken cancellationToken)
    {
        if (args.Count == 2 && args[1] is "-h" or "--help")
        {
            await stdout.WriteLineAsync(Help).ConfigureAwait(false);
            return 0;
        }

        if (args.Count != 3 || args[1] != "--protocol" || args[2] != "jsonl")
        {
            await stderr.WriteLineAsync("Usage: dotnet-diagnostics-cli stream --protocol jsonl").ConfigureAwait(false);
            return 2;
        }

        using var host = buildHost();
        var services = host.Services;
        using var writer = new ProtocolWriter(stdout);
        ActiveSession? activeSession = null;
        var negotiated = false;

        try
        {
            while (true)
            {
                var line = await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    if (activeSession is not null)
                    {
                        await StopAndDrainAsync(activeSession).ConfigureAwait(false);
                        activeSession = null;
                    }

                    return 0;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    await writer.WriteAsync(new
                    {
                        type = "error",
                        code = "malformed_json",
                        message = ex.Message,
                    }, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                using (document)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !TryGetString(document.RootElement, "type", out var type))
                    {
                        await WriteErrorAsync(writer, "invalid_message", "Each message must be a JSON object with a string 'type'.")
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (!negotiated)
                    {
                        if (!string.Equals(type, "hello", StringComparison.Ordinal)
                            || !TryGetInt32(document.RootElement, "protocolVersion", out var requestedVersion))
                        {
                            await WriteErrorAsync(writer, "handshake_required", "Send a hello message with protocolVersion before other commands.")
                                .ConfigureAwait(false);
                            continue;
                        }

                        if (requestedVersion != ProtocolVersion)
                        {
                            await writer.WriteAsync(new
                            {
                                type = "error",
                                code = "protocol_version_unsupported",
                                message = $"Protocol version {requestedVersion} is not supported.",
                                supportedVersions = new[] { ProtocolVersion },
                            }, cancellationToken).ConfigureAwait(false);
                            return 2;
                        }

                        negotiated = true;
                        await writer.WriteAsync(new
                        {
                            type = "hello",
                            protocolVersion = ProtocolVersion,
                        }, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    switch (type)
                    {
                        case "hello":
                            await WriteErrorAsync(writer, "already_negotiated", "Protocol negotiation has already completed.")
                                .ConfigureAwait(false);
                            break;

                        case "start":
                            activeSession = await HandleStartAsync(
                                document.RootElement, services, writer, activeSession, stdin, stdout, stderr, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case "stop":
                        case "cancel":
                            if (activeSession is null
                                || !TryGetString(document.RootElement, "sessionId", out var requestedSessionId)
                                || !string.Equals(requestedSessionId, activeSession.SessionId, StringComparison.Ordinal))
                            {
                                await WriteErrorAsync(writer, "session_not_found", "The requested session is not active.")
                                    .ConfigureAwait(false);
                                break;
                            }

                            await StopAndDrainAsync(activeSession).ConfigureAwait(false);
                            activeSession = null;
                            break;

                        case "capture":
                            await HandleCaptureAsync(document.RootElement, services, writer, stdin, stdout, stderr, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        default:
                            await WriteErrorAsync(writer, "unknown_message_type", $"Unknown message type '{type}'.")
                                .ConfigureAwait(false);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeSession is not null)
            {
                await StopAndDrainAsync(activeSession).ConfigureAwait(false);
            }

            await stderr.WriteLineAsync("dotnet-diagnostics-cli stream: cancelled; active session stopped.")
                .ConfigureAwait(false);
            return 130;
        }
        finally
        {
            if (activeSession is not null)
            {
                await StopAndDrainAsync(activeSession).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ActiveSession?> HandleStartAsync(
        JsonElement root,
        IServiceProvider services,
        ProtocolWriter writer,
        ActiveSession? activeSession,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (activeSession is not null && activeSession.Session.Completion.IsCompleted)
        {
            await activeSession.TerminalTask.ConfigureAwait(false);
            activeSession = null;
        }

        if (!TryReadStartRequest(root, out var request, out var validationError))
        {
            await WriteErrorAsync(writer, "invalid_start", validationError!).ConfigureAwait(false);
            return activeSession;
        }

        if (activeSession is not null)
        {
            await WriteErrorAsync(writer, "session_active", "Stop the active session before starting another.")
                .ConfigureAwait(false);
            return activeSession;
        }

        // Every requested kind must clear the shared safety registry before any session is created:
        // a rejected kind must not leave sibling kinds half-started.
        foreach (var kindRequest in request!.Kinds)
        {
            var safetyOptions = new CliOptions
            {
                Command = "stream",
                Kind = kindRequest.Kind,
                Pid = request.ProcessId,
            };
            var safety = await CliSafetyPreflight.RunAsync(
                safetyOptions,
                handles: null,
                CliExecutionContext.OneShot,
                interactive: false,
                stdin,
                stdout,
                stderr,
                artifactRoot: null,
                cancellationToken).ConfigureAwait(false);
            if (safety != CliSafetyPreflightDisposition.Proceed)
            {
                await WriteErrorAsync(writer, "safety_rejected", $"The live '{kindRequest.Kind}' session was not started.")
                    .ConfigureAwait(false);
                return activeSession;
            }
        }

        // The composed dispatch queue is shared by every requested kind, so it must be sized to the
        // largest per-kind observationCapacity requested, not Core's 256-event default: otherwise a
        // legacy single-kind client requesting a larger capacity would silently regress to 256.
        var eventCapacity = request.Kinds.Max(k => GetObservationCapacity(k.Options!));
        var composed = new ComposedDiagnosticSession(request.ProcessId, eventCapacity);
        var candidate = new ActiveSession(composed, Guid.NewGuid().ToString("N"), request.Kinds.Select(k => k.Kind).ToArray(), writer);
        try
        {
            foreach (var kindRequest in request.Kinds)
            {
                var descriptor = LiveKinds[kindRequest.Kind];
                candidate.Subscriptions.Add(descriptor.AttachForwarding(composed, writer, candidate.SessionId, candidate.EventsGate));
                composed.AddSession(descriptor.CreateChildSession(services, request.ProcessId, kindRequest.Options!));
            }

            try
            {
                await composed.StartAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(new
                {
                    type = "started",
                    requestId = request.RequestId,
                    sessionId = candidate.SessionId,
                    processId = composed.ProcessId,
                    kinds = candidate.Kinds,
                }, cancellationToken).ConfigureAwait(false);
                candidate.AllowEvents();
                candidate.StartTerminalPump();
                return candidate;
            }
            catch
            {
                // Suppress (rather than allow) queued observations: a session that never sent
                // "started" must not leak observation frames for a sessionId the client doesn't
                // know about. Suppressing still releases any handler blocked on the gate so the
                // composed session's drain below cannot hang.
                candidate.SuppressEvents();
                foreach (var subscription in candidate.Subscriptions)
                {
                    subscription.Dispose();
                }

                await composed.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId = request.RequestId,
                code = "start_failed",
                message = ex.Message,
            }, cancellationToken).ConfigureAwait(false);
            return activeSession;
        }
    }

    private static async Task HandleCaptureAsync(
        JsonElement root,
        IServiceProvider services,
        ProtocolWriter writer,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(root, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            await WriteErrorAsync(writer, "invalid_capture", "A non-empty string 'requestId' is required.").ConfigureAwait(false);
            return;
        }

        if (!TryGetString(root, "kind", out var kind) || !CaptureKinds.Contains(kind))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_capture_kind",
                message = $"Capture kind '{kind}' is not supported.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!TryGetInt32(root, "processId", out var processId) || processId <= 0)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_capture",
                message = "A positive integer 'processId' is required.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var durationSeconds = 10;
        if (root.TryGetProperty("durationSeconds", out var durationElement)
            && (!durationElement.TryGetInt32(out durationSeconds) || durationSeconds < 1 || durationSeconds > 300))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_capture",
                message = "'durationSeconds' must be between 1 and 300.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var topN = 25;
        if (root.TryGetProperty("topN", out var topNElement)
            && (!topNElement.TryGetInt32(out topN) || topN is < 1 or > 500))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_capture",
                message = "'topN' must be between 1 and 500.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string? heapSource = null;
        var topTypes = 20;
        if (kind == "heap")
        {
            if (!TryGetString(root, "source", out heapSource) || !HeapCaptureSources.Contains(heapSource))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'source' must be 'live' or 'gcdump' for a 'heap' capture.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("topTypes", out var topTypesElement)
                && (!topTypesElement.TryGetInt32(out topTypes) || topTypes is < 1 or > 500))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'topTypes' must be between 1 and 500.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        var acknowledgeRisk = TryGetString(root, "acknowledgeRisk", out var acknowledgeRiskValue)
            ? acknowledgeRiskValue
            : null;

        // CPU capture reuses the same shared Core safety registry entry as `collect --kind cpu`.
        // A `heap` capture instead reuses the `inspect-heap` entry, which is High risk/Acknowledge
        // for both `live` and `gcdump` sources (see InvocationSafetyRegistry.InspectHeapProfile),
        // unlike CPU sampling's Moderate risk — so it requires a matching `acknowledgeRisk`.
        var safetyOptions = kind == "heap"
            ? new CliOptions { Command = "inspect-heap", Sources = [heapSource!], Pid = processId, AcknowledgeRisk = acknowledgeRisk }
            : new CliOptions { Command = "collect", Kind = kind, Pid = processId, AcknowledgeRisk = acknowledgeRisk };
        var safety = await CliSafetyPreflight.RunAsync(
            safetyOptions,
            handles: null,
            CliExecutionContext.OneShot,
            interactive: false,
            stdin,
            stdout,
            stderr,
            artifactRoot: null,
            cancellationToken).ConfigureAwait(false);
        if (safety != CliSafetyPreflightDisposition.Proceed)
        {
            var resolvedRisk = CliInvocationSafety.Resolve(safetyOptions).RiskLevel;
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "capture_safety_rejected",
                message = resolvedRisk >= InvocationRiskLevel.High
                    ? $"The '{kind}' capture was not started. Re-send with acknowledgeRisk=\"{EnumName(resolvedRisk)}\" after showing the risk explanation to the user."
                    : $"The '{kind}' capture was not started.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            if (kind == "heap")
            {
                await HandleHeapCaptureAsync(requestId, services, writer, processId, heapSource!, topTypes, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var sampler = services.GetRequiredService<ICpuSampler>();
            var result = await sampler.SampleAsync(
                processId,
                TimeSpan.FromSeconds(durationSeconds),
                topN,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(new
            {
                type = "capture",
                requestId,
                kind,
                processId,
                result = result.Summary,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "capture_failed",
                message = ex.Message,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches a <c>heap</c> capture to the same Core use cases as the CLI's one-shot
    /// <c>inspect-heap</c> command (<see cref="CliCommands"/>), mirroring its exact DI resolution
    /// and call pattern, then writes a trimmed <see cref="DiagnosticResult{T}"/> projection that
    /// keeps what a VS Code panel needs (top types by bytes, suspend duration/GC-dump status,
    /// warnings, quality notes) and drops MCP-only noise fields.
    /// </summary>
    private static async Task HandleHeapCaptureAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        int processId,
        string source,
        int topTypes,
        CancellationToken cancellationToken)
    {
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();

        DiagnosticResult<LiveHeapInspection> result;
        if (source == DiagnosticOperationCatalog.HeapSources.GcDump)
        {
            var collector = services.GetRequiredService<IGcDumpHeapSnapshotCollector>();
            result = await HeapInspectionUseCases.InspectGcDump(
                collector, handles, resolver, processId, topTypes, timeout: null, exportTrace: false, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var inspector = services.GetRequiredService<IDumpInspector>();
            var allowlist = services.GetRequiredService<SymbolServerAllowlist>();
            result = await HeapInspectionUseCases.InspectLiveHeap(
                inspector, handles, resolver, allowlist,
                principalAllowsSymbolsRemote: true,
                processId, topTypes, includeRetentionPaths: false, retentionPathLimit: 8,
                includeStaticFields: false, includeDelegateTargets: false, includeDuplicateStrings: false,
                symbolPath: null, deprecation: null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result.IsError)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "capture_failed",
                message = result.Error!.Message,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var data = result.Data;
        await writer.WriteAsync(new
        {
            type = "capture",
            requestId,
            kind = "heap",
            processId,
            source,
            result = new
            {
                summary = result.Summary,
                data = data is null
                    ? null
                    : new
                    {
                        processId = data.ProcessId,
                        suspendDuration = data.SuspendDuration,
                        runtime = data.Runtime,
                        heap = data.Heap,
                        topTypesByBytes = data.TopTypesByBytes,
                        topTypesByInstances = data.TopTypesByInstances,
                        warnings = data.Warnings,
                        quality = data.Quality,
                        gcDumpStatus = data.GcDumpStatus,
                    },
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string EnumName(InvocationRiskLevel value) => value.ToString().ToLowerInvariant();

    private static async Task StopAndDrainAsync(ActiveSession activeSession)
    {
        await activeSession.Session.StopAsync().ConfigureAwait(false);
        await activeSession.TerminalTask.ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(ProtocolWriter writer, string code, string message) =>
        await writer.WriteAsync(new { type = "error", code, message }, CancellationToken.None).ConfigureAwait(false);

    private static bool TryReadStartRequest(JsonElement root, out StartRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (!TryGetString(root, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            error = "A non-empty string 'requestId' is required.";
            return false;
        }

        if (!TryGetInt32(root, "processId", out var processId) || processId <= 0)
        {
            error = "A positive integer 'processId' is required.";
            return false;
        }

        if (!root.TryGetProperty("kinds", out var kindsElement))
        {
            // Backward-compatible (#1091/#1092/#1093) shape: no 'kinds' array means one implicit
            // 'counters' kind using this message's own top-level fields.
            if (!TryParseCounterOptions(root, out var legacyOptions, out var legacyError))
            {
                error = legacyError;
                return false;
            }

            request = new StartRequest(requestId, processId, [new KindRequest("counters", legacyOptions)]);
            return true;
        }

        if (kindsElement.ValueKind != JsonValueKind.Array || kindsElement.GetArrayLength() == 0)
        {
            error = "'kinds' must be a non-empty array of kind requests.";
            return false;
        }

        if (kindsElement.GetArrayLength() > LiveKinds.Count)
        {
            error = $"'kinds' must contain at most {LiveKinds.Count} entries.";
            return false;
        }

        var kinds = new List<KindRequest>();
        var seenKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kindElement in kindsElement.EnumerateArray())
        {
            if (kindElement.ValueKind != JsonValueKind.Object
                || !TryGetString(kindElement, "kind", out var kindName)
                || !LiveKinds.TryGetValue(kindName, out var descriptor))
            {
                error = $"Each entry in 'kinds' must have a recognized 'kind' (one of: {string.Join(", ", LiveKinds.Keys)}).";
                return false;
            }

            if (!seenKinds.Add(kindName))
            {
                error = $"'kinds' must not repeat kind '{kindName}'.";
                return false;
            }

            if (!descriptor.ParseOptions(kindElement, out var options, out var kindError))
            {
                error = $"kind '{kindName}': {kindError}";
                return false;
            }

            kinds.Add(new KindRequest(kindName, options));
        }

        request = new StartRequest(requestId, processId, kinds);
        return true;
    }

    private static bool TryParseCounterOptions(JsonElement root, out object? options, out string? error)
    {
        options = null;
        error = null;
        var intervalSeconds = 1;
        if (root.TryGetProperty("intervalSeconds", out var interval)
            && (!interval.TryGetInt32(out intervalSeconds) || intervalSeconds < 1))
        {
            error = "'intervalSeconds' must be a positive integer.";
            return false;
        }

        var observationCapacity = 256;
        if (root.TryGetProperty("observationCapacity", out var capacity)
            && (!capacity.TryGetInt32(out observationCapacity)
                || observationCapacity is < 1 or > CounterSessionOptions.MaxAllowedObservationCapacity))
        {
            error = $"'observationCapacity' must be between 1 and {CounterSessionOptions.MaxAllowedObservationCapacity}.";
            return false;
        }

        IReadOnlyList<string>? providers = null;
        if (root.TryGetProperty("providers", out var providerArray))
        {
            if (providerArray.ValueKind != JsonValueKind.Array
                || providerArray.GetArrayLength() > 64
                || providerArray.EnumerateArray().Any(provider =>
                    provider.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(provider.GetString())
                    || provider.GetString()!.Length > 256))
            {
                error = "'providers' must contain at most 64 non-empty strings of at most 256 characters.";
                return false;
            }

            providers = providerArray.EnumerateArray().Select(provider => provider.GetString()!).ToArray();
        }

        options = new CounterSessionOptions
        {
            Providers = providers,
            IntervalSeconds = intervalSeconds,
            ObservationCapacity = observationCapacity,
        };
        return true;
    }

    private static bool TryParseGcOptions(JsonElement root, out object? options, out string? error)
    {
        options = null;
        error = null;
        var observationCapacity = 256;
        if (root.TryGetProperty("observationCapacity", out var capacity)
            && (!capacity.TryGetInt32(out observationCapacity)
                || observationCapacity is < 1 or > GcSessionOptions.MaxAllowedObservationCapacity))
        {
            error = $"'observationCapacity' must be between 1 and {GcSessionOptions.MaxAllowedObservationCapacity}.";
            return false;
        }

        options = new GcSessionOptions { ObservationCapacity = observationCapacity };
        return true;
    }

    private static IDisposable AttachCounterForwarding(
        ComposedDiagnosticSession composed, ProtocolWriter writer, string sessionId, EventGate eventsGate)
        => composed.Attach<CounterObservation>(async (observation, handlerCancellationToken) =>
        {
            await eventsGate.Task.WaitAsync(handlerCancellationToken).ConfigureAwait(false);
            if (eventsGate.Suppressed)
            {
                return;
            }

            await writer.WriteAsync(new
            {
                type = "observation",
                kind = "counters",
                sessionId,
                observation.Sequence,
                observation.Timestamp,
                counter = observation.Counter,
            }, handlerCancellationToken).ConfigureAwait(false);
        });

    private static IDisposable AttachGcForwarding(
        ComposedDiagnosticSession composed, ProtocolWriter writer, string sessionId, EventGate eventsGate)
        => composed.Attach<GcPauseObservation>(async (observation, handlerCancellationToken) =>
        {
            await eventsGate.Task.WaitAsync(handlerCancellationToken).ConfigureAwait(false);
            if (eventsGate.Suppressed)
            {
                return;
            }

            await writer.WriteAsync(new
            {
                type = "observation",
                kind = "gc",
                sessionId,
                observation.Sequence,
                observation.Timestamp,
                collection = observation.Collection,
            }, handlerCancellationToken).ConfigureAwait(false);
        });

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } result)
        {
            value = result;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out value)
            ? true
            : SetDefault(out value);

    private static bool SetDefault(out int value)
    {
        value = 0;
        return false;
    }

    private const string Help =
"""
Usage: dotnet-diagnostics-cli stream --protocol jsonl

Runs the versioned bidirectional JSON Lines protocol over stdin/stdout.
Stdout contains protocol frames only; diagnostics and startup messages use stderr.
Send {"type":"hello","protocolVersion":1} before start/stop/capture commands.
Start requires requestId and processId. Send either the legacy single-kind shape
(optional providers/intervalSeconds/observationCapacity, implying kind "counters") or
{"kinds":[{"kind":"counters",...},{"kind":"gc",...}]} to run several live kinds in one
composed session. Send stop or cancel with the returned sessionId. Send
{"type":"capture","requestId":...,"kind":"cpu","processId":...,"durationSeconds":10,"topN":25}
for a one-shot point-in-time capture that does not open a live session. A heap snapshot capture
uses {"type":"capture","requestId":...,"kind":"heap","processId":...,"source":"live"|"gcdump",
"topTypes":20,"acknowledgeRisk":"high"}: "source" is required ("live" attaches via ptrace and
suspends the target; "gcdump" uses EventPipe and induces a blocking Gen2 GC); "topTypes" is
optional (default 20, range 1-500). Both heap sources are High risk/Acknowledge (unlike cpu's
Moderate risk), so the request is rejected with code "capture_safety_rejected" unless
"acknowledgeRisk" equals the resolved risk name (currently "high").
""";

    private sealed record StartRequest(string RequestId, int ProcessId, IReadOnlyList<KindRequest> Kinds);

    private sealed record KindRequest(string Kind, object? Options);

    private delegate bool TryParseKindOptions(JsonElement element, out object? options, out string? error);

    private sealed record KindDescriptor(
        string Kind,
        TryParseKindOptions ParseOptions,
        Func<IServiceProvider, int, object, IDiagnosticSession> CreateChildSession,
        Func<ComposedDiagnosticSession, ProtocolWriter, string, EventGate, IDisposable> AttachForwarding);

    private static int GetObservationCapacity(object options) => options switch
    {
        CounterSessionOptions counters => counters.ObservationCapacity,
        GcSessionOptions gc => gc.ObservationCapacity,
        _ => 256,
    };

    /// <summary>
    /// Gates observation forwarding until the composed session has either announced <c>started</c>
    /// (<see cref="Allow"/>) or failed before announcing it (<see cref="Suppress"/>). A suppressed
    /// gate still releases any handler blocked on <see cref="Task"/>, but <see cref="Suppressed"/>
    /// tells the handler to drop the observation instead of writing a frame for a sessionId the
    /// client was never told about.
    /// </summary>
    private sealed class EventGate
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Suppressed { get; private set; }

        public Task Task => _tcs.Task;

        public void Allow() => _tcs.TrySetResult();

        public void Suppress()
        {
            Suppressed = true;
            _tcs.TrySetResult();
        }
    }

    private sealed class ActiveSession(
        ComposedDiagnosticSession session, string sessionId, IReadOnlyList<string> kinds, ProtocolWriter writer)
    {
        // Matches the Core session's own shutdown budget (see EventPipeDiagnosticSessionBase.ShutdownWaitBudget)
        // so a stuck stdout pipe cannot make stop/cancel/EOF handling hang indefinitely.
        private static readonly TimeSpan TerminalWriteBudget = TimeSpan.FromSeconds(5);

        public EventGate EventsGate { get; } = new();
        public List<IDisposable> Subscriptions { get; } = [];

        public ComposedDiagnosticSession Session { get; } = session;
        public string SessionId { get; } = sessionId;
        public IReadOnlyList<string> Kinds { get; } = kinds;
        public Task TerminalTask { get; private set; } = Task.CompletedTask;

        public void AllowEvents() => EventsGate.Allow();

        public void SuppressEvents() => EventsGate.Suppress();

        public void StartTerminalPump() => TerminalTask = PublishTerminalWhenCompleteAsync();

        private async Task PublishTerminalWhenCompleteAsync()
        {
            var completion = await Session.Completion.ConfigureAwait(false);
            // The session has already finished dispatching; this flush is best-effort and bounded so a
            // wedged stdout consumer cannot block the CLI's stop/cancel/EOF handling forever.
            try
            {
                await EventsGate.Task.WaitAsync(TerminalWriteBudget).ConfigureAwait(false);
                await writer.WriteAsync(new
                {
                    type = "terminal",
                    sessionId = SessionId,
                    kinds = Kinds,
                    completion.Status,
                    completion.StartedAt,
                    completion.EndedAt,
                    completion.EventPipeEventsLost,
                    completion.DroppedObservations,
                    error = completion.Error?.Message,
                }, CancellationToken.None).WaitAsync(TerminalWriteBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Best-effort: the terminal frame could not be delivered within budget. The caller's
                // overall stop/cancel/EOF handling must still complete.
            }
            finally
            {
                foreach (var subscription in Subscriptions)
                {
                    subscription.Dispose();
                }
            }
        }
    }

    private sealed class ProtocolWriter(TextWriter stdout) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(object frame, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(frame, frame.GetType(), JsonOptions);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stdout.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }
}
