using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Versioned bidirectional JSON Lines protocol for <c>dotnet-diagnostics-cli stream --protocol jsonl</c>.
/// Supports one or more concurrently-running live signal kinds (<c>counters</c>, <c>gc</c>) composed
/// behind a single <see cref="ComposedDiagnosticSession"/> per <c>start</c> request (#1099), plus a
/// one-shot <c>capture</c> request/response pair for point-in-time kinds (<c>cpu</c>, <c>heap</c>,
/// <c>thread-snapshot</c>) that does not open a live session. The single-kind counters wire shape
/// from #1091/#1092/#1093 remains
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
    private static readonly HashSet<string> CaptureKinds = new(StringComparer.Ordinal)
    {
        "cpu",
        "heap",
        DiagnosticOperationCatalog.ThreadSnapshotCliKind,
    };

    /// <summary>Heap sources accepted by a <c>capture</c> request with <c>kind="heap"</c>.</summary>
    private static readonly HashSet<string> HeapCaptureSources = new(StringComparer.Ordinal)
    {
        DiagnosticOperationCatalog.HeapSources.Live,
        DiagnosticOperationCatalog.HeapSources.GcDump,
        DiagnosticOperationCatalog.HeapSources.Dump,
    };

    /// <summary>
    /// The 7 always-available + 4 opt-in heap-snapshot views (<c>static-fields</c>, <c>delegate-targets</c>,
    /// <c>retention-paths</c>, <c>retained-exceptions</c>) exposed through a <c>query</c> request
    /// against a <c>heap-snapshot</c>-kind handle (issue #1116). Deliberately excludes the
    /// address-targeted <c>object</c>/<c>gcroot</c>/<c>objsize</c>/<c>duplicate-strings</c> views
    /// (<see cref="HeapSnapshotQueryDispatcher"/>'s <c>ServerOnlyView</c> set) and <c>top-types</c>
    /// (already returned inline by the <c>capture</c> response) — both deferred to a future
    /// address-targeted drilldown feature.
    /// </summary>
    private static readonly HashSet<string> HeapQueryViews = new(StringComparer.Ordinal)
    {
        "roots-by-kind",
        "finalizer-queue",
        "fragmentation",
        "gchandles",
        "async",
        "timers",
        "alc",
        "static-fields",
        "delegate-targets",
        "retention-paths",
        "retained-exceptions",
    };

    /// <summary>
    /// The 4 richer thread-snapshot views plus <c>thread-statics</c> (issue #1126; requires a non-empty
    /// <c>typeFilter</c> and re-opens the handle's origin via ClrMD) exposed through a <c>query</c> request against a
    /// <c>thread-snapshot</c>-kind handle (issue #1116). Deliberately excludes
    /// <c>threads-summary</c>/<c>top-blocked</c>/<c>stack</c>/<c>lock-graph</c>/<c>async-stalls</c>
    /// (already covered inline by the <c>capture</c> response's <c>threads</c>/<c>locks</c>
    /// projection, or address/thread-id-targeted) — deferred to a future drilldown feature.
    /// </summary>
    private static readonly HashSet<string> ThreadQueryViews = new(StringComparer.Ordinal)
    {
        "deadlocks",
        "unique-stacks",
        "wait-chains",
        "threadpool",
        "thread-statics",
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

                        case "query":
                            await HandleQueryAsync(document.RootElement, services, writer, cancellationToken)
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

        // `dumpFile` is a mutually-exclusive alternative to `processId` for a 'heap' capture with
        // `source="dump"` and for a `thread-snapshot` capture — both dispatch to the same offline
        // Core use cases the one-shot CLI's `--dump-file` option already serves
        // (HeapInspectionUseCases.InspectDump / SamplerUseCases.CollectThreadSnapshot). 'cpu' and a
        // 'heap' capture with `source="live"`/`"gcdump"` never accept a dump file; they always
        // require a live `processId`.
        var dumpFile = TryGetString(root, "dumpFile", out var dumpFileValue) && !string.IsNullOrWhiteSpace(dumpFileValue)
            ? dumpFileValue
            : null;

        string? heapSource = null;
        if (kind == "heap")
        {
            if (!TryGetString(root, "source", out heapSource) || !HeapCaptureSources.Contains(heapSource))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'source' must be 'live', 'gcdump', or 'dump' for a 'heap' capture.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        var processIdPresent = root.TryGetProperty("processId", out var processIdElement)
            && processIdElement.ValueKind != JsonValueKind.Null;

        int? processId = null;
        if (kind == "heap" && heapSource == DiagnosticOperationCatalog.HeapSources.Dump)
        {
            // `source="dump"` already discriminates the offline path; `processId` is not just
            // optional here, it doesn't apply at all — a dump has no PID to attach to.
            if (dumpFile is null)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "A non-empty string 'dumpFile' is required for a 'heap' capture with source=\"dump\".",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (processIdPresent)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'processId' is not supported for a 'heap' capture with source=\"dump\"; use 'dumpFile' only.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
        else if (kind == DiagnosticOperationCatalog.ThreadSnapshotCliKind)
        {
            // Unlike heap, thread-snapshot has no separate discriminator field — `processId` vs
            // `dumpFile` presence alone selects the live or offline path, so exactly one is required.
            if (dumpFile is not null && processIdPresent)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'processId' and 'dumpFile' are mutually exclusive for a 'thread-snapshot' capture.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (dumpFile is null)
            {
                if (!TryGetInt32(root, "processId", out var threadSnapshotPid) || threadSnapshotPid <= 0)
                {
                    await writer.WriteAsync(new
                    {
                        type = "error",
                        requestId,
                        code = "invalid_capture",
                        message = "Either a positive integer 'processId' or a non-empty string 'dumpFile' is required for a 'thread-snapshot' capture.",
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                processId = threadSnapshotPid;
            }
        }
        else
        {
            // 'cpu', or 'heap' with source "live"/"gcdump": always a live attach by processId.
            if (dumpFile is not null)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = kind == "heap"
                        ? $"'dumpFile' is only supported for a 'heap' capture with source=\"dump\" (got source=\"{heapSource}\")."
                        : $"'dumpFile' is not supported for a '{kind}' capture.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!TryGetInt32(root, "processId", out var requiredPid) || requiredPid <= 0)
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
            processId = requiredPid;
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

        var topTypes = 20;
        if (kind == "heap")
        {
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

        // Opt-in heap enrichments (issue #1116): all default `false` — threaded straight through to
        // HeapInspectionUseCases.InspectDump/InspectLiveHeap's existing parameters of the same name,
        // which previously were hardcoded `false` at both call sites below.
        var includeStaticFields = false;
        var includeDelegateTargets = false;
        var includeRetentionPaths = false;
        var includeRetainedExceptions = false;
        if (kind == "heap")
        {
            if (!TryGetOptionalBool(root, "includeStaticFields", out includeStaticFields))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'includeStaticFields' must be a boolean.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!TryGetOptionalBool(root, "includeDelegateTargets", out includeDelegateTargets))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'includeDelegateTargets' must be a boolean.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!TryGetOptionalBool(root, "includeRetentionPaths", out includeRetentionPaths))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'includeRetentionPaths' must be a boolean.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!TryGetOptionalBool(root, "includeRetainedExceptions", out includeRetainedExceptions))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_capture",
                    message = "'includeRetainedExceptions' must be a boolean.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        var maxFramesPerThread = 64;
        if (kind == DiagnosticOperationCatalog.ThreadSnapshotCliKind
            && root.TryGetProperty("maxFramesPerThread", out var maxFramesElement)
            && (!maxFramesElement.TryGetInt32(out maxFramesPerThread)
                || maxFramesPerThread < 1
                || maxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_capture",
                message = $"'maxFramesPerThread' must be between 1 and {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap}.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var acknowledgeRisk = TryGetString(root, "acknowledgeRisk", out var acknowledgeRiskValue)
            ? acknowledgeRiskValue
            : null;

        // CPU capture reuses the same shared Core safety registry entry as `collect --kind cpu`.
        // A `heap` capture instead reuses the `inspect-heap` entry: `live`/`gcdump` sources are High
        // risk/Acknowledge (see InvocationSafetyRegistry.InspectHeapProfile), but `dump` resolves to
        // the same Moderate risk/Warn profile the CLI's own `inspect-heap --source dump` gets — no
        // `acknowledgeRisk` is required for it. A `thread-snapshot` capture falls through to the
        // generic `collect`-kind branch below: a live ClrMD attach resolves to the `live` High-risk
        // profile (see InvocationSafetyRegistry.CollectThreadSnapshot), while a dump-sourced one
        // (`dumpFile` set, `processId` absent) resolves to its Moderate/Warn `dump` profile, keyed
        // off the same `dumpFile`/`dumpFilePath` argument the one-shot CLI's `--dump-file` sets.
        var safetyOptions = kind == "heap"
            ? new CliOptions { Command = "inspect-heap", Sources = [heapSource!], Pid = processId, DumpFile = dumpFile, AcknowledgeRisk = acknowledgeRisk }
            : new CliOptions { Command = "collect", Kind = kind, Pid = processId, DumpFile = dumpFile, AcknowledgeRisk = acknowledgeRisk };
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
                await HandleHeapCaptureAsync(
                    requestId, services, writer, processId, dumpFile, heapSource!, topTypes,
                    includeStaticFields, includeDelegateTargets, includeRetentionPaths, includeRetainedExceptions, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (kind == DiagnosticOperationCatalog.ThreadSnapshotCliKind)
            {
                await HandleThreadSnapshotCaptureAsync(requestId, services, writer, processId, dumpFile, maxFramesPerThread, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var sampler = services.GetRequiredService<ICpuSampler>();
            var result = await sampler.SampleAsync(
                processId!.Value,
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
    /// keeps what a VS Code panel needs and drops MCP-only noise fields. A <c>dump</c> source reads
    /// an offline <see cref="DumpInspection"/> — no <c>processId</c>/suspend-duration/GC-dump-status
    /// fields, since there's no live attach — while <c>live</c>/<c>gcdump</c> keep the existing
    /// <see cref="LiveHeapInspection"/> projection (top types by bytes, suspend duration/GC-dump
    /// status, warnings, quality notes).
    /// </summary>
    private static async Task HandleHeapCaptureAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        int? processId,
        string? dumpFile,
        string source,
        int topTypes,
        bool includeStaticFields,
        bool includeDelegateTargets,
        bool includeRetentionPaths,
        bool includeRetainedExceptions,
        CancellationToken cancellationToken)
    {
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();

        if (source == DiagnosticOperationCatalog.HeapSources.Dump)
        {
            var dumpInspector = services.GetRequiredService<IDumpInspector>();
            var dumpAllowlist = services.GetRequiredService<SymbolServerAllowlist>();
            var dumpResult = await HeapInspectionUseCases.InspectDump(
                dumpInspector, handles, dumpAllowlist,
                principalAllowsSymbolsRemote: true,
                dumpFile!, topTypes, includeRetentionPaths: includeRetentionPaths, retentionPathLimit: 8,
                includeStaticFields: includeStaticFields, includeDelegateTargets: includeDelegateTargets, includeDuplicateStrings: false,
                includeRetainedExceptions: includeRetainedExceptions,
                symbolPath: null, deprecation: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (dumpResult.IsError)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "capture_failed",
                    message = dumpResult.Error!.Message,
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var dumpData = dumpResult.Data;
            await writer.WriteAsync(new
            {
                type = "capture",
                requestId,
                kind = "heap",
                source,
                dumpFile,
                result = new
                {
                    summary = dumpResult.Summary,
                    data = dumpData is null
                        ? null
                        : new
                        {
                            handle = dumpData.Handle,
                            filePath = dumpData.FilePath,
                            fileSizeBytes = dumpData.FileSizeBytes,
                            runtime = dumpData.Runtime,
                            heap = dumpData.Heap,
                            topTypesByBytes = dumpData.TopTypesByBytes,
                            topTypesByInstances = dumpData.TopTypesByInstances,
                            warnings = dumpData.Warnings,
                            quality = dumpData.Quality,
                        },
                },
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var resolver = services.GetRequiredService<IProcessContextResolver>();

        DiagnosticResult<LiveHeapInspection> result;
        if (source == DiagnosticOperationCatalog.HeapSources.GcDump)
        {
            var collector = services.GetRequiredService<IGcDumpHeapSnapshotCollector>();
            result = await HeapInspectionUseCases.InspectGcDump(
                collector, handles, resolver, processId!.Value, topTypes, timeout: null, exportTrace: false, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var inspector = services.GetRequiredService<IDumpInspector>();
            var allowlist = services.GetRequiredService<SymbolServerAllowlist>();
            result = await HeapInspectionUseCases.InspectLiveHeap(
                inspector, handles, resolver, allowlist,
                principalAllowsSymbolsRemote: true,
                processId!.Value, topTypes, includeRetentionPaths: includeRetentionPaths, retentionPathLimit: 8,
                includeStaticFields: includeStaticFields, includeDelegateTargets: includeDelegateTargets, includeDuplicateStrings: false,
                includeRetainedExceptions: includeRetainedExceptions,
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
                        handle = data.Handle,
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

    /// <summary>
    /// Dispatches a <c>thread-snapshot</c> capture to the same Core use case as the CLI's one-shot
    /// <c>collect --kind thread-snapshot</c> (<see cref="CliCommands"/>), reusing its exact DI
    /// resolution and call pattern. <paramref name="processId"/> and <paramref name="dumpFile"/> are
    /// mutually exclusive (validated by the caller): a live ClrMD attach is used when
    /// <paramref name="processId"/> is given, and an offline dump read (mirroring the CLI's
    /// <c>--dump-file</c> option) is used when <paramref name="dumpFile"/> is given. Either origin
    /// projects the same <see cref="ThreadSnapshotQueryResult"/> shape — only <c>data.Origin</c>
    /// differs ("live" vs "dump") — trimmed to what a VS Code panel needs (per-thread
    /// id/state/wait-reason/top frames, contended locks, thread pool summary), dropping MCP-only
    /// drilldown/pagination noise fields.
    /// </summary>
    private static async Task HandleThreadSnapshotCaptureAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        int? processId,
        string? dumpFile,
        int maxFramesPerThread,
        CancellationToken cancellationToken)
    {
        var result = await SamplerUseCases.CollectThreadSnapshot(
            services.GetRequiredService<IThreadSnapshotInspector>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            services.GetRequiredService<SymbolServerAllowlist>(),
            principalAllowsSymbolsRemote: false,
            processId,
            dumpFilePath: dumpFile,
            maxFramesPerThread,
            includeRuntimeFrames: false,
            includeNativeFrames: false,
            symbolPath: null,
            depth: SamplingDepth.Detail,
            cancellationToken).ConfigureAwait(false);

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
            kind = DiagnosticOperationCatalog.ThreadSnapshotCliKind,
            processId,
            dumpFile,
            result = new
            {
                summary = result.Summary,
                data = data is null
                    ? null
                    : new
                    {
                        handle = data.Handle,
                        processId = data.ProcessId,
                        origin = data.Origin,
                        capturedAt = data.CapturedAt,
                        walkDuration = data.WalkDuration,
                        totalThreads = data.TotalThreads,
                        omittedThreads = data.OmittedThreads,
                        totalLocks = data.TotalLocks,
                        omittedLocks = data.OmittedLocks,
                        threads = data.Threads?.Select(static thread => new
                        {
                            managedThreadId = thread.ManagedThreadId,
                            osThreadId = thread.OSThreadId,
                            state = thread.State,
                            isAlive = thread.IsAlive,
                            isBackground = thread.IsBackground,
                            isGc = thread.IsGc,
                            isThreadpoolWorker = thread.IsThreadpoolWorker,
                            lockCount = thread.LockCount,
                            currentExceptionType = thread.CurrentExceptionType,
                            isLikelyBlocked = thread.IsLikelyBlocked,
                            inferredWaitReason = thread.InferredWaitReason,
                            frames = thread.Frames.Select(static frame => frame.DisplayName).ToArray(),
                        }),
                        locks = data.Locks?.Select(static lockState => new
                        {
                            objectTypeFullName = lockState.ObjectTypeFullName,
                            ownerManagedThreadId = lockState.OwnerManagedThreadId,
                            waitingThreadCount = lockState.WaitingThreadCount,
                            isContended = lockState.IsContended,
                        }),
                    },
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a <c>query</c> request against a handle already registered by a prior <c>heap</c>
    /// or <c>thread-snapshot</c> <c>capture</c> on this same connection (issue #1116). The handle
    /// store is in-process memory, so this only ever sees handles produced earlier in this same
    /// <c>stream</c> child process — a fresh CLI process (or one started after this one exits) would
    /// not see them. Mirrors the MCP <c>query_snapshot</c> tool's dispatch-by-handle-kind precedent
    /// (<c>QuerySnapshotTool</c> in <c>DotnetDiagnostics.Mcp</c>): the handle's recorded kind selects
    /// <see cref="HeapSnapshotQueryDispatcher"/> or <see cref="ThreadSnapshotQueryDispatcher"/>, both
    /// of which render purely from the already-captured artifact — no re-attach, no re-suspend, no
    /// new ClrMD walk. Restricted to the 10 heap views and 4 thread views enumerated in
    /// <see cref="HeapQueryViews"/>/<see cref="ThreadQueryViews"/>; the address-targeted
    /// <c>object</c>/<c>gcroot</c>/<c>objsize</c>/<c>duplicate-strings</c>/<c>resolve-address</c>/
    /// <c>frame-vars</c> views are explicitly out of scope for this request type.
    /// </summary>
    private static async Task HandleQueryAsync(
        JsonElement root,
        IServiceProvider services,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(root, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            await WriteErrorAsync(writer, "invalid_query", "A non-empty string 'requestId' is required.").ConfigureAwait(false);
            return;
        }

        if (!TryGetString(root, "handle", out var handle) || string.IsNullOrWhiteSpace(handle))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "A non-empty string 'handle' is required.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!TryGetString(root, "view", out var view) || string.IsNullOrWhiteSpace(view))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "A non-empty string 'view' is required.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var topN = 50;
        if (root.TryGetProperty("topN", out var topNElement)
            && topNElement.ValueKind != JsonValueKind.Null
            && (!topNElement.TryGetInt32(out topN) || topN is < 1 or > 500))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "'topN' must be between 1 and 500.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string? typeFilter = null;
        if (root.TryGetProperty("typeFilter", out var typeFilterElement)
            && typeFilterElement.ValueKind != JsonValueKind.Null)
        {
            if (typeFilterElement.ValueKind != JsonValueKind.String)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'typeFilter' must be a string.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            typeFilter = typeFilterElement.GetString();
        }

        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var lookup = handles.LookupWithKind(handle);
        if (lookup.Status != DiagnosticHandleLookupStatus.Found || lookup.Lookup is not { } found)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unknown_handle",
                message = $"Handle '{handle}' is unknown, expired, or was evicted. Capture a fresh snapshot and retry.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (found.Kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            await HandleHeapQueryAsync(requestId, writer, handle, view, topN, found.Artifact, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (found.Kind == SamplerUseCases.ThreadSnapshotKind)
        {
            await HandleThreadQueryAsync(requestId, services, writer, handle, view, topN, typeFilter, found.Artifact, cancellationToken).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(new
        {
            type = "error",
            requestId,
            code = "unsupported_handle_kind",
            message = $"Handle '{handle}' was captured as '{found.Kind}', which has no `query` views defined.",
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders a heap-snapshot <c>query</c> view by calling <see cref="HeapSnapshotQueryDispatcher.Dispatch"/>
    /// directly against the artifact already registered at capture time — the exact same dispatcher
    /// the MCP <c>query_snapshot</c> tool uses, so ranking/projection logic is never duplicated here.
    /// </summary>
    private static async Task HandleHeapQueryAsync(
        string requestId,
        ProtocolWriter writer,
        string handle,
        string view,
        int topN,
        object artifact,
        CancellationToken cancellationToken)
    {
        var normalizedView = view.Trim().ToLowerInvariant();
        if (!HeapQueryViews.Contains(normalizedView))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_query_view",
                message = $"View '{view}' is not a supported heap query view. Supported views: {string.Join(", ", HeapQueryViews.OrderBy(static v => v, StringComparer.Ordinal))}.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var snapshot = (HeapSnapshotArtifact)artifact;
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, handle, normalizedView, topN, rankBy: null, typeFullName: null);

        // Defense-in-depth: HeapQueryViews never contains a ServerOnly/unknown name, so these two
        // branches are unreachable in practice, but keep the friendly error rather than trust that
        // invariant blindly if the dispatcher's view sets ever change out from under us.
        if (outcome.ServerOnlyView || outcome.UnknownView)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_query_view",
                message = $"View '{view}' is not supported by the `query` request.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var result = outcome.Result!;
        if (result.IsError)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = QueryErrorCode(result.Error!.Kind),
                message = result.Error.Message,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle,
            view = normalizedView,
            result = TrimHeapQueryResult(result.Data!),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders a thread-snapshot <c>query</c> view by calling <see cref="ThreadSnapshotQueryDispatcher.Dispatch"/>
    /// directly against the artifact already registered at capture time, mirroring
    /// <see cref="HandleHeapQueryAsync"/>. <c>framesToHash</c>/<c>minCount</c> match the CLI
    /// <c>session</c> REPL's own defaults for <c>unique-stacks</c>.
    /// </summary>
    private static async Task HandleThreadQueryAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        string handle,
        string view,
        int topN,
        string? typeFilter,
        object artifact,
        CancellationToken cancellationToken)
    {
        var normalizedView = view.Trim().ToLowerInvariant();
        if (!ThreadQueryViews.Contains(normalizedView))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_query_view",
                message = $"View '{view}' is not a supported thread query view. Supported views: {string.Join(", ", ThreadQueryViews.OrderBy(static v => v, StringComparer.Ordinal))}.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var snapshot = (ThreadSnapshotArtifact)artifact;
        if (normalizedView == "thread-statics")
        {
            await HandleThreadStaticsQueryAsync(requestId, services, writer, handle, snapshot, typeFilter, topN, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var result = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, handle, normalizedView, threadId: null, topN, framesToHash: 20, minCount: 1);

        if (result.IsError)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = QueryErrorCode(result.Error!.Kind),
                message = result.Error.Message,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle,
            view = normalizedView,
            result = TrimThreadQueryResult(result.Data!),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the <c>thread-statics</c> view: re-opens the snapshot origin (live pid or dump) via
    /// <see cref="IThreadStaticFieldResolver"/>, the same resolver the CLI <c>session</c> REPL uses.
    /// Requires <paramref name="typeFilter"/> as the EXACT full type name.
    /// </summary>
    private static async Task HandleThreadStaticsQueryAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        string handle,
        ThreadSnapshotArtifact snapshot,
        string? typeFilter,
        int topN,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(typeFilter))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "A non-empty string 'typeFilter' (exact full type name) is required for view 'thread-statics'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var resolver = services.GetRequiredService<IThreadStaticFieldResolver>();
        ThreadStaticFieldsResult threadStatics;
        try
        {
            threadStatics = await resolver.ResolveAsync(
                snapshot, typeFilter, includeSensitiveValues: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "query_failed",
                message = $"thread-statics: {ex.Message}",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var bounded = BoundThreadStatics(threadStatics, topN);
        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle,
            view = "thread-statics",
            result = new
            {
                handle,
                view = "thread-statics",
                origin = snapshot.Origin.ToString().ToLowerInvariant(),
                processId = snapshot.ProcessId,
                capturedAt = snapshot.CapturedAt,
                threadStatics = bounded.ThreadStatics,
                totalThreads = bounded.TotalThreads,
                omittedThreads = bounded.OmittedThreads,
                notes = bounded.Notes,
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record BoundedThreadStatics(
        ThreadStaticFieldsResult ThreadStatics, int TotalThreads, int OmittedThreads, IReadOnlyList<string> Notes);

    /// <summary>
    /// Caps the per-thread matrix of a <c>thread-statics</c> result to <paramref name="topN"/> threads
    /// (the validated <c>topN</c>, default 50, max 500) so one frame cannot grow with thread count;
    /// truncation is reported through <c>totalThreads</c>/<c>omittedThreads</c>/<c>notes</c>.
    /// </summary>
    internal static BoundedThreadStatics BoundThreadStatics(ThreadStaticFieldsResult full, int topN)
    {
        var total = full.Threads.Count;
        if (total <= topN)
        {
            return new BoundedThreadStatics(full, total, 0, []);
        }

        var omitted = total - topN;
        var bounded = full with { Threads = [.. full.Threads.Take(topN)] };
        return new BoundedThreadStatics(
            bounded,
            total,
            omitted,
            [$"thread-statics: returned {topN} of {total} thread(s) (topN cap); {omitted} omitted. Raise 'topN' (max 500) to see more."]);
    }

    /// <summary>
    /// Projects a <see cref="HeapSnapshotQueryResult"/> for the wire, dropping MCP-only
    /// pagination/drilldown-chaining fields that aren't meaningful to a one-shot panel: <c>Address</c>
    /// (echoes an address-targeted query we never issue), <c>TopTypes</c>/<c>RankBy</c> (only for
    /// <c>top-types</c>, already returned inline by the `capture` response),
    /// <c>FilterTypeFullName</c>/<c>TotalRetentionPaths</c>/<c>OmittedRetentionPaths</c>/<c>RetentionFrameLimit</c>
    /// (retention-path pagination noise — the trimmed shape just returns the bounded page),
    /// <c>ObjectDetails</c>/<c>GcRoot</c>/<c>ObjectSize</c>/<c>DuplicateStrings</c> (address-targeted
    /// views outside this request type's scope).
    /// </summary>
    private static object TrimHeapQueryResult(HeapSnapshotQueryResult data) => new
    {
        handle = data.Handle,
        view = data.View,
        origin = data.Origin,
        processId = data.ProcessId,
        capturedAt = data.CapturedAt,
        quality = data.Quality,
        retentionPaths = data.RetentionPaths,
        rootsByKind = data.RootsByKind,
        finalizableObjects = data.FinalizableObjects,
        segments = data.Segments,
        staticFields = data.StaticFields,
        delegateTargets = data.DelegateTargets,
        gcHandles = data.GcHandles,
        asyncOperations = data.AsyncOperations,
        sortedBy = data.SortedBy,
        timers = data.Timers,
        assemblyLoadContexts = data.AssemblyLoadContexts,
        retainedExceptions = data.RetainedExceptions,
    };

    /// <summary>
    /// Projects a <see cref="ThreadSnapshotQueryResult"/> for the wire, dropping MCP-only
    /// pagination/drilldown-chaining fields: every <c>*Offset</c>/<c>*Cursor</c> field (the 4 in-scope
    /// views never page), <c>Threads</c>/<c>Thread</c>/<c>Locks</c>/<c>ThreadId</c>/<c>CandidateThreads</c>
    /// (populated by views outside this request type's scope), <c>ResolvedAddresses</c>/<c>FrameVariables</c>
    /// (address/thread-id-targeted views outside scope), <c>FramesPerThreadLimit</c> (only relevant to
    /// <c>Threads</c>/<c>Thread</c>).
    /// </summary>
    private static object TrimThreadQueryResult(ThreadSnapshotQueryResult data) => new
    {
        handle = data.Handle,
        view = data.View,
        origin = data.Origin,
        processId = data.ProcessId,
        capturedAt = data.CapturedAt,
        walkDuration = data.WalkDuration,
        deadlocks = data.Deadlocks,
        uniqueStacks = data.UniqueStacks,
        waitChains = data.WaitChains,
        threadPool = data.ThreadPool,
    };

    /// <summary>
    /// Converts a Core <see cref="DiagnosticError.Kind"/> (PascalCase, e.g. <c>"ViewNotCaptured"</c>)
    /// to a snake_case wire error code (e.g. <c>"view_not_captured"</c>), matching the rest of this
    /// protocol's error-code convention. A generic converter (rather than a lookup table) so any
    /// future error kind the dispatchers introduce is forward-compatible without a CLI code change.
    /// </summary>
    private static string QueryErrorCode(string diagnosticErrorKind)
    {
        var builder = new System.Text.StringBuilder(diagnosticErrorKind.Length * 2);
        for (var i = 0; i < diagnosticErrorKind.Length; i++)
        {
            var c = diagnosticErrorKind[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses an optional boolean field: absent or JSON <c>null</c> defaults <paramref name="value"/>
    /// to <c>false</c> and returns <c>true</c> (valid); present with a non-boolean JSON value returns
    /// <c>false</c> (invalid) so the caller can surface a validation error instead of silently
    /// coercing. Used by the 3 opt-in heap capture flags (issue #1116).
    /// </summary>
    private static bool TryGetOptionalBool(JsonElement root, string name, out bool value)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            value = false;
            return true;
        }

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
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
uses {"type":"capture","requestId":...,"kind":"heap","processId":...,"source":"live"|"gcdump"|"dump",
"topTypes":20,"acknowledgeRisk":"high"}: "source" is required ("live" attaches via ptrace and
suspends the target; "gcdump" uses EventPipe and induces a blocking Gen2 GC; "dump" reads an
existing dump file offline and requires "dumpFile" instead of "processId"); "topTypes" is
optional (default 20, range 1-500). "live"/"gcdump" are High risk/Acknowledge (unlike cpu's
Moderate risk), so the request is rejected with code "capture_safety_rejected" unless
"acknowledgeRisk" equals the resolved risk name (currently "high"); "dump" is Moderate risk/Warn
(like cpu) and never requires "acknowledgeRisk". A thread-snapshot capture uses
{"type":"capture","requestId":...,"kind":"thread-snapshot","processId":...,"maxFramesPerThread":64}
for a live ClrMD attach (High risk/Acknowledge), or the same shape with "dumpFile" instead of
"processId" to read an existing dump file offline (Moderate risk/Warn, no acknowledgeRisk needed).
"processId" and "dumpFile" are mutually exclusive for "heap" source="dump" and "thread-snapshot"
requests — exactly one is required. A heap capture also accepts the optional booleans
"includeStaticFields", "includeDelegateTargets", "includeRetentionPaths" and
"includeRetainedExceptions" (all default false; they enable the matching query views and are
ignored for source="gcdump"). A {"type":"query","requestId":...,"handle":...,"view":...} request
reads a view of a prior capture's handle: heap views are roots-by-kind, finalizer-queue,
fragmentation, gchandles, async, timers, alc, static-fields, delegate-targets, retention-paths and
retained-exceptions; thread-snapshot views are deadlocks, unique-stacks, wait-chains, threadpool and
thread-statics. The optional "typeFilter" string is the exact full type name that thread-statics
requires (otherwise error code "invalid_query"); it is ignored by every other view.
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
