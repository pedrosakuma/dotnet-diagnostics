using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.EventSources;
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
/// <c>thread-snapshot</c>, <c>exceptions</c>, <c>crash-guard</c>) that does not open a live session. The single-kind counters wire shape
/// from #1091/#1092/#1093 remains
/// valid: a <c>start</c> message without a <c>kinds</c> array is treated as a single implicit
/// <c>counters</c> kind using its top-level fields, so existing clients (the VS Code extension) do
/// not need changes.
/// </summary>
internal static partial class CliStreamingProtocol
{
    private const int ProtocolVersion = 2;
    private const string HeapGrowthView = "growth";
    private const int MaxControlLineBytes =
        CpuHotspotComparison.MaximumSerializedCaptureSetBytes * 2 + 64 * 1024;
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
            ["event_source"] = new KindDescriptor(
                "event_source",
                TryParseEventSourceOptions,
                static (services, processId, options) => services.GetRequiredService<IEventSourceSessionFactory>()
                    .CreateSession(processId, ((EventSourceStartOptions)options).SessionOptions),
                AttachEventSourceForwarding),
        };

    /// <summary>Point-in-time kinds known to the <c>capture</c> request/response flow.</summary>
    private static readonly HashSet<string> CaptureKinds = new(StringComparer.Ordinal)
    {
        "cpu",
        "heap",
        "exceptions",
        "crash-guard",
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
    /// <c>objsize</c>/<c>duplicate-strings</c> views and <c>top-types</c> (already returned inline by the
    /// <c>capture</c> response). The address-targeted <c>object</c>/<c>gcroot</c> views are allowlisted
    /// separately in <see cref="CliStreamingQueryNavigation.ObjectReadViews"/> because, unlike these
    /// projection views, they re-open the snapshot origin with ClrMD and need a safety acknowledgement (#1168).
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
        "type-instances",
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

    private static async Task HandleExceptionCaptureAsync(
        string requestId,
        string kind,
        IServiceProvider services,
        ProtocolWriter writer,
        int processId,
        int durationSeconds,
        int maxRecent,
        int topN,
        CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        object payload;
        DiagnosticError? error;
        if (kind == "exceptions")
        {
            var result = await EventCollectionUseCases.CollectExceptions(
                services.GetRequiredService<IExceptionCollector>(), resolver, handles,
                processId, durationSeconds, maxRecent, SamplingDepth.Detail, cancellationToken).ConfigureAwait(false);
            error = result.Error;
            payload = result.Data is { } data
                ? ProjectExceptionCapture(data, result.Summary, topN)
                : result;
        }
        else
        {
            var result = await EventCollectionUseCases.CollectCrashGuard(
                services.GetRequiredService<ICrashGuardCollector>(), resolver, handles,
                processId, durationSeconds, maxRecent, SamplingDepth.Detail, cancellationToken).ConfigureAwait(false);
            error = result.Error;
            payload = result.Data is { } data
                ? ProjectCrashGuardCapture(data, result.Summary, topN)
                : result;
        }

        if (error is not null)
        {
            await writer.WriteAsync(new
            {
                type = "error", requestId, code = "capture_failed", message = error.Message,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(new
        {
            type = "capture", requestId, kind, processId, result = payload,
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static object ProjectExceptionCapture(ExceptionSnapshot snapshot, string summary, int topN)
    {
        var notes = ExceptionCaptureNotes(snapshot.TotalExceptions, snapshot.Recent.Count, snapshot.RecentCap, snapshot.ByType.Count, topN);
        notes.Add("Managed exception events do not include stack frames. Use crash-guard for available exception stacks.");
        return new
        {
            summary,
            data = new
            {
                snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, snapshot.TotalExceptions,
                byType = snapshot.ByType.Take(topN).ToArray(),
                snapshot.Recent, snapshot.RecentCap,
            },
            notes,
        };
    }

    internal static object ProjectCrashGuardCapture(CrashGuardSnapshot snapshot, string summary, int topN)
    {
        var notes = ExceptionCaptureNotes(snapshot.TotalExceptions, snapshot.Exceptions.Count, snapshot.RecentCap, snapshot.ByType.Count, topN);
        notes.AddRange(snapshot.Notes);
        return new
        {
            summary,
            data = new
            {
                snapshot.ProcessId, snapshot.StartedAt, snapshot.Duration, snapshot.TotalExceptions,
                byType = snapshot.ByType.Take(topN).ToArray(),
                recent = snapshot.Exceptions,
                snapshot.RecentCap, snapshot.ProcessExited, snapshot.ExitCode,
                snapshot.UnhandledExceptionObserved, snapshot.Observation,
                snapshot.FinalException,
            },
            notes,
        };
    }

    private static List<string> ExceptionCaptureNotes(int total, int retained, int cap, int typeCount, int topN)
    {
        var notes = new List<string>
        {
            "Messages, message counts, and first/last timestamps describe retained events only, not the entire exception stream. Exception messages can contain sensitive application data.",
        };
        if (total > retained)
            notes.Add($"Retained {retained} event(s) with maxRecent={cap}; {total - retained} event(s) omitted. Type counts cover all observed events.");
        if (typeCount > topN)
            notes.Add($"Showing topN={topN} exception types; {typeCount - topN} lower-ranked type(s) omitted.");
        return notes;
    }

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
        var investigationEvidence = new CliInvestigationEvidence();
        await using var joint = new JointCaptureSlot(services, writer, stderr, investigationEvidence);
        var cpuHandleWindow = new CliStreamingCpuHandleWindow();
        ActiveSession? activeSession = null;
        var negotiated = false;

        try
        {
            while (true)
            {
                var inputLine = await ReadProtocolLineAsync(stdin, cancellationToken).ConfigureAwait(false);
                if (inputLine.Oversized)
                {
                    await WriteErrorAsync(writer, "frame_too_large", $"Control frames are limited to {MaxControlLineBytes} UTF-8 bytes.")
                        .ConfigureAwait(false);
                    continue;
                }

                var line = inputLine.Value;
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
                            cliVersion = CliBuildVersion.Current,
                            supportedLiveKinds = LiveKinds.Keys.Order(StringComparer.Ordinal).ToArray(),
                            heapGrowth = ProjectHeapGrowthCapability(services),
                            requestActivities = new
                            {
                                version = 1, captureKind = "request-activities",
                                queryViews = JointQueryViews,
                                maxRequests = 100, maxActivities = 200, maxQueryRows = 100,
                            },
                            cpuHandleComparison = new
                            {
                                version = 1,
                                queryView = "diff",
                                baselineHandleField = "baselineHandle",
                                retainedHandleLimit = CliStreamingCpuHandleWindow.RetainedHandleLimit,
                                maxTopMovers = CpuSampleHandleComparison.MaxTopMovers,
                            },
                            requestLatencyComparison = new
                            {
                                version = 1,
                                messageType = "compare",
                                kind = "request-latency",
                                inputSchema = RequestLatencyWindowSet.SchemaV1,
                                resultSchema = RequestLatencyComparisonResult.SchemaV1,
                                maxWindowsPerSide = RequestLatencyComparison.MaximumWindowsPerSide,
                                maxRetainedSamplesPerWindow =
                                    RequestLatencyComparison.MaximumRetainedSamplesPerWindow,
                            },
                            cpuHotspotComparison = new
                            {
                                version = 1,
                                messageType = "compare",
                                kind = "cpu-hotspot",
                                inputSchema = CpuHotspotCaptureSet.SchemaV1,
                                resultSchema = CpuHotspotComparisonResult.SchemaV1,
                                maxCapturesPerSide = CpuHotspotComparison.MaximumCapturesPerSide,
                                maxMethodsPerCapture = CpuHotspotCaptureProjection.MaximumMethodsPerCapture,
                            },
                            capabilities = new
                            {
                                investigationEvidence = investigationEvidence.Capability,
                            },
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
                            if (joint.IsRunning)
                            {
                                await WriteErrorAsync(writer, "capture_active", "Cancel the active joint capture before starting a live session.").ConfigureAwait(false);
                                break;
                            }
                            activeSession = await HandleStartAsync(
                                document.RootElement, services, writer, activeSession, stdin, stdout, stderr, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case "stop":
                        case "cancel":
                            if (type == "cancel" && TryGetString(document.RootElement, "requestId", out var cancelId))
                            {
                                await joint.CancelAsync(cancelId).ConfigureAwait(false);
                                break;
                            }
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
                            if (TryGetString(document.RootElement, "kind", out var captureKind) && captureKind == "request-activities")
                            {
                                await joint.StartAsync(document.RootElement, activeSession is not null, cancellationToken).ConfigureAwait(false);
                                break;
                            }
                            await joint.ClearAsync().ConfigureAwait(false);
                            await HandleCaptureAsync(
                                document.RootElement, services, writer, cpuHandleWindow,
                                stdin, stdout, stderr, investigationEvidence, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case "query":
                            if (JointCaptureSlot.IsJointQuery(document.RootElement))
                            {
                                await joint.QueryAsync(document.RootElement).ConfigureAwait(false);
                                break;
                            }
                            await HandleQueryAsync(document.RootElement, services, writer, stdin, stdout, stderr, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case "export":
                            await writer.WriteAsync(
                                await CliStreamingExport.HandleAsync(document.RootElement, services, cancellationToken)
                                    .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                            break;

                        case "compare":
                            await HandleComparisonAsync(
                                document.RootElement,
                                writer,
                                cancellationToken).ConfigureAwait(false);
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

    private static async Task HandleComparisonAsync(
        JsonElement root,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(root, "requestId", out var requestId)
            || string.IsNullOrWhiteSpace(requestId))
        {
            await WriteErrorAsync(
                writer,
                "invalid_comparison",
                "A non-empty string 'requestId' is required.").ConfigureAwait(false);
            return;
        }
        if (!TryGetString(root, "kind", out var kind)
            || kind is not ("request-latency" or "cpu-hotspot"))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_comparison_kind",
                message = "Comparison kind must be 'request-latency' or 'cpu-hotspot'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (!root.TryGetProperty("baseline", out var baselineElement)
            || baselineElement.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("candidate", out var candidateElement)
            || candidateElement.ValueKind != JsonValueKind.Object)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_comparison",
                message = "Object properties 'baseline' and 'candidate' are required.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            object result;
            if (string.Equals(kind, "request-latency", StringComparison.Ordinal))
            {
                EnsureComparisonInputSize(
                    baselineElement,
                    "Baseline request-latency window set",
                    RequestLatencyComparison.MaximumSerializedWindowSetBytes);
                EnsureComparisonInputSize(
                    candidateElement,
                    "Candidate request-latency window set",
                    RequestLatencyComparison.MaximumSerializedWindowSetBytes);
                var baseline = baselineElement.Deserialize(
                    RequestLatencyComparisonJsonContext.Default.RequestLatencyWindowSet)
                    ?? throw new InvalidDataException("Baseline request-latency window set was empty.");
                var candidate = candidateElement.Deserialize(
                    RequestLatencyComparisonJsonContext.Default.RequestLatencyWindowSet)
                    ?? throw new InvalidDataException("Candidate request-latency window set was empty.");
                result = RequestLatencyComparison.Compare(baseline, candidate);
            }
            else
            {
                EnsureComparisonInputSize(
                    baselineElement,
                    "Baseline CPU-hotspot capture set",
                    CpuHotspotComparison.MaximumSerializedCaptureSetBytes);
                EnsureComparisonInputSize(
                    candidateElement,
                    "Candidate CPU-hotspot capture set",
                    CpuHotspotComparison.MaximumSerializedCaptureSetBytes);
                var baseline = baselineElement.Deserialize(
                    CpuHotspotComparisonJsonContext.Default.CpuHotspotCaptureSet)
                    ?? throw new InvalidDataException("Baseline CPU-hotspot capture set was empty.");
                var candidate = candidateElement.Deserialize(
                    CpuHotspotComparisonJsonContext.Default.CpuHotspotCaptureSet)
                    ?? throw new InvalidDataException("Candidate CPU-hotspot capture set was empty.");
                result = CpuHotspotComparison.Compare(baseline, candidate);
            }

            await writer.WriteAsync(new
            {
                type = "comparison",
                requestId,
                kind,
                result,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidDataException)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_comparison",
                message = exception.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void EnsureComparisonInputSize(JsonElement element, string description, int maximumBytes)
    {
        var serializedBytes = Encoding.UTF8.GetByteCount(element.GetRawText());
        if (serializedBytes > maximumBytes)
        {
            throw new InvalidDataException(
                $"{description} exceeds the {maximumBytes}-byte limit.");
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
            if (kindRequest.Options is EventSourceStartOptions eventSourceOptions)
            {
                var allowlist = services.GetRequiredService<EventSourceAllowlist>();
                safetyOptions = safetyOptions with
                {
                    Providers = eventSourceOptions.SessionOptions.Providers.Select(provider => provider.ProviderName).ToArray(),
                    UnsafeProvider = eventSourceOptions.SessionOptions.Providers.Any(
                        provider => !allowlist.IsAllowed(provider.ProviderName)),
                    AcknowledgeRisk = eventSourceOptions.AcknowledgeRisk,
                };
            }
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

        // Preserve the existing larger-capacity behavior for older live kinds. EventSource's
        // shared dispatch queue has a separate hard count budget regardless of other kinds.
        var eventCapacity = GetComposedEventCapacity(
            request.Kinds.Max(k => GetObservationCapacity(k.Options!)),
            request.Kinds.Any(kind => kind.Kind == "event_source"));
        var dispatchByteCapacity = request.Kinds.Any(kind => kind.Kind == "event_source")
            ? EventSourceSessionOptions.MaxPendingRetainedBytes
            : long.MaxValue;
        var composed = new ComposedDiagnosticSession(request.ProcessId, eventCapacity, dispatchByteCapacity);
        var candidate = new ActiveSession(composed, Guid.NewGuid().ToString("N"), request.Kinds.Select(k => k.Kind).ToArray(), writer);
        try
        {
            foreach (var kindRequest in request.Kinds)
            {
                var descriptor = LiveKinds[kindRequest.Kind];
                candidate.Subscriptions.Add(descriptor.AttachForwarding(composed, writer, candidate.SessionId, candidate.EventsGate));
                var childSession = descriptor.CreateChildSession(services, request.ProcessId, kindRequest.Options!);
                if (childSession is EventSourceSession eventSession)
                {
                    candidate.EventSourceSession = eventSession;
                }
                composed.AddSession(childSession);
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
        CliStreamingCpuHandleWindow cpuHandleWindow,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CliInvestigationEvidence investigationEvidence,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(root, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            await WriteErrorAsync(writer, "invalid_capture", "A non-empty string 'requestId' is required.").ConfigureAwait(false);
            return;
        }

        if (!TryGetString(root, "kind", out var kind)
            || !(CaptureKinds.Contains(kind) || CliStreamingCaptureViews.Kinds.Contains(kind)))
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

        if (root.TryGetProperty("exportTrace", out var traceElement)
            && traceElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            await writer.WriteAsync(new
            {
                type = "error", requestId, code = "invalid_capture",
                message = "'exportTrace' must be a boolean and is supported only for CPU captures.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        var exportTrace = traceElement.ValueKind == JsonValueKind.True;
        if (exportTrace && kind != "cpu")
        {
            await writer.WriteAsync(new
            {
                type = "error", requestId, code = "invalid_capture",
                message = "'exportTrace' is supported only for CPU captures.",
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
            && (durationElement.ValueKind != JsonValueKind.Number
                || !durationElement.TryGetInt32(out durationSeconds) || durationSeconds < 1 || durationSeconds > 300))
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

        CliOptions? structuredOptions = null;
        if (CliStreamingCaptureViews.Kinds.Contains(kind))
        {
            try
            {
                structuredOptions = CliStreamingCaptureOptions.Parse(root, kind, processId!.Value, durationSeconds);
            }
            catch (ArgumentException ex)
            {
                await writer.WriteAsync(new
                {
                    type = "error", requestId, code = "invalid_capture", message = ex.Message,
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        var topN = 25;
        if (root.TryGetProperty("topN", out var topNElement)
            && (topNElement.ValueKind != JsonValueKind.Number
                || !topNElement.TryGetInt32(out topN) || topN is < 1 or > 500))
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
        var maxRecent = 100;
        if (kind is "exceptions" or "crash-guard"
            && root.TryGetProperty("maxRecent", out var recentElement)
            && (recentElement.ValueKind != JsonValueKind.Number
                || !recentElement.TryGetInt32(out maxRecent) || maxRecent is < 1 or > 500))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_capture",
                message = "'maxRecent' must be an integer between 1 and 500.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

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
        var safetyOptions = structuredOptions ?? (kind == "heap"
            ? new CliOptions { Command = "inspect-heap", Sources = [heapSource!], Pid = processId, DumpFile = dumpFile, AcknowledgeRisk = acknowledgeRisk }
            : new CliOptions { Command = "collect", Kind = kind, Pid = processId, DumpFile = dumpFile, AcknowledgeRisk = acknowledgeRisk, ExportTrace = exportTrace });
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
            var processStartedAtBefore = processId is { } liveProcessId
                ? CliInvestigationEvidence.ReadProcessStart(liveProcessId)
                : null;
            if (kind is "exceptions" or "crash-guard")
            {
                await HandleExceptionCaptureAsync(
                    requestId, kind, services, writer, processId!.Value, durationSeconds, maxRecent, topN, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

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

            if (structuredOptions is not null)
            {
                var capture = await CliCommands.RunAsync(services, structuredOptions, cancellationToken).ConfigureAwait(false);
                if (capture.IsError || capture.Cancelled)
                {
                    var envelope = JsonSerializer.SerializeToElement(capture.Envelope, JsonOptions);
                    var message = envelope.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object
                        && failure.TryGetProperty("message", out var detail)
                            ? detail.GetString() : envelope.GetProperty("summary").GetString();
                    await writer.WriteAsync(new
                    {
                        type = "error", requestId, code = "capture_failed", message,
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                var view = CliStreamingCaptureViews.Project(
                    capture,
                    structuredOptions,
                    services.GetRequiredService<IDiagnosticHandleStore>());
                var descriptor = investigationEvidence.Create(
                    kind,
                    processId!.Value,
                    processStartedAtBefore,
                    CliInvestigationEvidence.ReadProcessStart(processId.Value),
                    view.Provenance.StartedAt,
                    view.Provenance.StartedAt is { } startedAt && view.Provenance.ReportedDurationSeconds is { } reportedDuration
                        ? startedAt.AddSeconds(reportedDuration)
                        : null,
                    view.Provenance.StartedAt is not null && view.Provenance.ReportedDurationSeconds is not null
                        ? "reported"
                        : "requested",
                    "normal",
                    view.Backend,
                    view.Evidence.Source,
                    view.Evidence.Limitations.Select(static limitation => limitation.Detail).ToArray(),
                    view.Handle,
                    view.HandleExpiresAt);
                var frame = new Dictionary<string, object?>
                {
                    ["type"] = "capture",
                    ["requestId"] = requestId,
                    ["kind"] = kind,
                    ["processId"] = processId,
                    ["result"] = view,
                };
                if (descriptor is not null)
                {
                    frame["investigationEvidence"] = descriptor;
                }
                await writer.WriteAsync(new
                    Dictionary<string, object?>(frame), cancellationToken).ConfigureAwait(false);
                return;
            }

            var sampler = services.GetRequiredService<ICpuSampler>();
            var result = await sampler.SampleAsync(
                processId!.Value,
                TimeSpan.FromSeconds(durationSeconds),
                topN,
                exportTrace: exportTrace,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var artifact = CpuHotspotCaptureProjection.EnsureProfile(result.Artifact);
            var handles = services.GetRequiredService<IDiagnosticHandleStore>();
            var exportHandle = handles.RegisterWithMetadata(
                processId.Value, "cpu-sample", artifact, TimeSpan.FromMinutes(20),
                evictWhenProcessExits: false, origin: HandleOrigin.Live, producingTool: "collect_sample");
            cpuHandleWindow.Track(handles, exportHandle.Id);
            var cpuDescriptor = investigationEvidence.Create(
                kind,
                processId.Value,
                processStartedAtBefore,
                CliInvestigationEvidence.ReadProcessStart(processId.Value),
                result.Summary.StartedAt,
                result.Summary.StartedAt + result.Summary.Duration,
                "reported",
                "normal",
                "eventpipe",
                "cpu-sampler",
                [
                    "CPU sample windows use host UTC start plus configured duration; they do not prove provider readiness or uninterrupted target coverage.",
                    "Stack, module, method, and source-path data can contain sensitive application information.",
                ],
                exportHandle.Id,
                exportHandle.ExpiresAt);
            await writer.WriteAsync(new
            {
                type = "capture",
                requestId,
                kind,
                processId,
                result = result.Summary,
                exportHandle = exportHandle.Id,
                hotspotVariability = artifact.HotspotVariability,
                investigationEvidence = cpuDescriptor,
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
    /// Dispatches a <c>query</c> request against a handle already registered by a prior <c>cpu</c>,
    /// <c>heap</c> or <c>thread-snapshot</c> <c>capture</c> on this same connection (issue #1116). The handle
    /// store is in-process memory, so this only ever sees handles produced earlier in this same
    /// <c>stream</c> child process — a fresh CLI process (or one started after this one exits) would
    /// not see them. Mirrors the MCP <c>query_snapshot</c> tool's dispatch-by-handle-kind precedent
    /// (<c>QuerySnapshotTool</c> in <c>DotnetDiagnostics.Mcp</c>): the handle's recorded kind selects
    /// <see cref="CpuSampleQueryDispatcher"/>, <see cref="HeapSnapshotQueryDispatcher"/> or
    /// <see cref="ThreadSnapshotQueryDispatcher"/>, which render purely from the already-captured
    /// artifact — no re-sampling, re-attach, re-suspend, or new ClrMD walk. Restricted to the CPU,
    /// 10 heap and 4 thread views enumerated by their dispatchers and
    /// <see cref="HeapQueryViews"/>/<see cref="ThreadQueryViews"/>; the address-targeted
    /// <c>objsize</c>/<c>duplicate-strings</c>/<c>resolve-address</c>/<c>frame-vars</c> views are
    /// explicitly out of scope for this request type; <c>object</c>/<c>gcroot</c> are served only with a
    /// valid hexadecimal <c>address</c> and a matching <c>acknowledgeRisk</c> (#1168).
    /// </summary>
    private static async Task HandleQueryAsync(
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

        var normalizedView = view.Trim().ToLowerInvariant();
        var isHeapGrowth = normalizedView == HeapGrowthView;
        var isCpuDiff = normalizedView == "diff";
        string? baselineHandle = null;
        var hasBaselineHandle = root.TryGetProperty("baselineHandle", out var baselineElement)
            && baselineElement.ValueKind != JsonValueKind.Null;
        if (hasBaselineHandle)
        {
            if (baselineElement.ValueKind != JsonValueKind.String
                || !IsValidProtocolHandle(baselineElement.GetString()))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'baselineHandle' must be a non-empty string of at most 256 characters without control characters.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            baselineHandle = baselineElement.GetString();
        }

        if ((isHeapGrowth || isCpuDiff) && baselineHandle is null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = isHeapGrowth
                    ? "View 'growth' requires a non-empty 'baselineHandle' naming the earlier heap snapshot."
                    : "CPU view 'diff' requires a non-empty string 'baselineHandle'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!isHeapGrowth && !isCpuDiff && baselineHandle is not null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "'baselineHandle' is supported only for views 'growth' and 'diff'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var growthRankBy = HeapGrowthDiff.RankByBytes;
        if (isHeapGrowth && root.TryGetProperty("rankBy", out var rankByElement)
            && rankByElement.ValueKind != JsonValueKind.Null)
        {
            var requestedRank = rankByElement.ValueKind == JsonValueKind.String
                ? rankByElement.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (rankByElement.ValueKind != JsonValueKind.String
                || requestedRank is not (HeapGrowthDiff.RankByBytes or HeapGrowthDiff.RankByInstances))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'rankBy' must be 'bytes' or 'instances' for view 'growth'.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            growthRankBy = requestedRank;
        }

        var minDeltaPct = 5.0;
        if (isHeapGrowth && root.TryGetProperty("minDeltaPct", out var minDeltaElement)
            && minDeltaElement.ValueKind != JsonValueKind.Null
            && (!minDeltaElement.TryGetDouble(out minDeltaPct)
                || !double.IsFinite(minDeltaPct)
                || minDeltaPct < 0))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "'minDeltaPct' must be a finite number greater than or equal to zero for view 'growth'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var topN = isHeapGrowth ? 25 : 50;
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

        if (normalizedView == "diff" && topN > CpuSampleHandleComparison.MaxTopMovers)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = $"'topN' must be between 1 and {CpuSampleHandleComparison.MaxTopMovers} for CPU view 'diff'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string? typeFilter = null;
        if (root.TryGetProperty("typeFilter", out var typeFilterElement)
            && typeFilterElement.ValueKind != JsonValueKind.Null)
        {
            if (typeFilterElement.ValueKind != JsonValueKind.String
                || typeFilterElement.GetString() is not { Length: <= 512 } requestedTypeFilter
                || requestedTypeFilter.Any(char.IsControl))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'typeFilter' must be a string of at most 512 characters without control characters.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            typeFilter = requestedTypeFilter;
        }

        if (isHeapGrowth && typeFilter is not null && string.IsNullOrWhiteSpace(typeFilter))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "When supplied for view 'growth', 'typeFilter' must be the non-empty exact full name of one objective type.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!TryParseHeapGrowthAttempts(root, isHeapGrowth, out var comparisonAttempts, out var comparisonError))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = comparisonError,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (comparisonAttempts.Count > 0 && typeFilter is null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "'comparisonPairs' requires a non-empty exact objective 'typeFilter'; legacy unfiltered growth remains a single-pair response.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string? rootMethodFilter = null;
        if (root.TryGetProperty("rootMethodFilter", out var rootMethodFilterElement)
            && rootMethodFilterElement.ValueKind != JsonValueKind.Null)
        {
            if (rootMethodFilterElement.ValueKind != JsonValueKind.String
                || rootMethodFilterElement.GetString() is not { Length: <= 512 } filter
                || filter.Any(char.IsControl))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'rootMethodFilter' must be a string of at most 512 characters without control characters.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            rootMethodFilter = filter;
        }

        if (!CliStreamingQueryNavigation.TryReadTypeIdentity(root, out var typeIdentity))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = CliStreamingQueryNavigation.TypeIdentityFormatMessage,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string? cursor = null;
        if (root.TryGetProperty("cursor", out var cursorElement)
            && cursorElement.ValueKind != JsonValueKind.Null)
        {
            if (cursorElement.ValueKind != JsonValueKind.String
                || cursorElement.GetString() is not { Length: > 0 and <= 256 } cursorValue
                || cursorValue.Any(char.IsControl))
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'cursor' must be a non-empty string of at most 256 characters without control characters.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            cursor = cursorValue;
        }

        // #1168: `address` is a strict 0x-hex string (never a JSON number); validated before any handle lookup.
        var addressStatus = CliStreamingQueryNavigation.TryReadAddress(root, out var address);
        if (addressStatus == CliStreamingQueryNavigation.AddressStatus.Invalid)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = CliStreamingQueryNavigation.AddressFormatMessage,
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var acknowledgeRisk = TryGetString(root, "acknowledgeRisk", out var acknowledgeRiskValue)
            ? acknowledgeRiskValue
            : null;

        if (!CliStreamingQueryNavigation.TryReadArrayRange(root, view, out var objectOptions))
        {
            await writer.WriteAsync(new { type = "error", requestId, code = "invalid_query",
                message = "Array ranges are supported only for view 'object': arrayStartIndex must be a non-negative Int32 and arrayCount an integer from 1 to 8." },
                CancellationToken.None).ConfigureAwait(false);
            return;
        }
        var includeSensitiveValues = false;
        if (root.TryGetProperty("includeSensitiveValues", out var sensitive))
        {
            if (sensitive.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !string.Equals(view.Trim(), "object", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteAsync(new { type = "error", requestId, code = "invalid_query",
                    message = "'includeSensitiveValues' must be a boolean and is supported only for view 'object'." },
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }
            includeSensitiveValues = sensitive.GetBoolean();
            if (includeSensitiveValues && (!root.TryGetProperty("acknowledgeSensitiveValues", out var consent)
                || consent.ValueKind != JsonValueKind.True))
            {
                await writer.WriteAsync(new { type = "error", requestId, code = "sensitive_values_rejected",
                    message = "Revealing object values requires acknowledgeSensitiveValues=true for this request after explicit user consent. Values can contain secrets or personal data." },
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var lookup = handles.LookupWithKind(handle);
        if (lookup.Status != DiagnosticHandleLookupStatus.Found || lookup.Lookup is not { } found)
        {
            if (isHeapGrowth)
            {
                await WriteGrowthHandleUnavailableAsync(
                    writer,
                    requestId,
                    handle,
                    "current",
                    lookup.Status).ConfigureAwait(false);
            }
            else if (isCpuDiff)
            {
                await WriteHandleUnavailableAsync(
                    writer, requestId, "Current", handle, lookup, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "unknown_handle",
                    message = $"Handle '{handle}' is unknown, expired, or was evicted. Capture a fresh snapshot and retry.",
                }, CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        if (isCpuDiff)
        {
            if (addressStatus == CliStreamingQueryNavigation.AddressStatus.Valid
                || includeSensitiveValues
                || objectOptions is not null)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "CPU view 'diff' does not accept heap address or object-value options.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var baselineLookup = handles.LookupWithKind(baselineHandle!);
            if (baselineLookup.Status != DiagnosticHandleLookupStatus.Found
                || baselineLookup.Lookup is not { } baselineFound)
            {
                await WriteHandleUnavailableAsync(
                    writer, requestId, "Baseline", baselineHandle!, baselineLookup, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (found.Artifact is not CpuSampleTraceArtifact currentCpu
                || baselineFound.Artifact is not CpuSampleTraceArtifact baselineCpu)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = $"CPU view 'diff' requires two 'cpu-sample' artifacts (got '{baselineFound.Kind}' and '{found.Kind}').",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var comparison = CpuSampleHandleComparison.Compare(
                baselineFound.Handle,
                baselineCpu,
                found.Handle,
                currentCpu,
                topN);
            if (comparison.IsError)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = comparison.Error!.Message,
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await writer.WriteAsync(new
            {
                type = "query",
                requestId,
                handle,
                baselineHandle,
                view = normalizedView,
                result = new
                {
                    handle,
                    baselineHandle,
                    view = normalizedView,
                    summary = comparison.Summary,
                    data = comparison.Data,
                },
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (isHeapGrowth)
        {
            if (found.Kind != HeapInspectionUseCases.HeapSnapshotKind)
            {
                await WriteGrowthUnsupportedHandleKindAsync(
                    writer,
                    requestId,
                    handle,
                    "current",
                    found.Kind).ConfigureAwait(false);
                return;
            }

            if (addressStatus == CliStreamingQueryNavigation.AddressStatus.Valid)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'address' is not supported for view 'growth'.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await HandleHeapGrowthQueryAsync(
                requestId,
                writer,
                handles,
                handle,
                found,
                baselineHandle!,
                growthRankBy,
                minDeltaPct,
                topN,
                typeFilter?.Trim(),
                comparisonAttempts,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (found.Kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            await HandleHeapQueryAsync(
                requestId, services, writer, stdin, stdout, stderr, handles, handle, view, topN,
                addressStatus == CliStreamingQueryNavigation.AddressStatus.Valid ? address : null,
                acknowledgeRisk, includeSensitiveValues, objectOptions, typeIdentity, cursor, found.Artifact, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (includeSensitiveValues || objectOptions is not null || typeIdentity is not null || cursor is not null)
        {
            await writer.WriteAsync(new { type = "error", requestId, code = "invalid_query",
                message = "Object-value, array-range, type-identity, and cursor fields require a heap-snapshot handle." },
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (found.Kind == SamplerUseCases.ThreadSnapshotKind)
        {
            if (addressStatus == CliStreamingQueryNavigation.AddressStatus.Valid)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'address' is only accepted for heap-snapshot handles.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await HandleThreadQueryAsync(requestId, services, writer, handle, view, topN, typeFilter, found.Artifact, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (found.Kind is "cpu-sample" or "allocation-sample")
        {
            if (addressStatus == CliStreamingQueryNavigation.AddressStatus.Valid)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = "'address' is only accepted for heap-snapshot handles.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (found.Kind == "allocation-sample")
            {
                if (found.Artifact is not AllocationSampleArtifact { EstimatedBytesTrace: { } byteTrace }
                    || view.Trim().ToLowerInvariant() is not ("call-tree" or "top-methods" or "hot-path" or "by-module" or "by-namespace" or "caller-callee"))
                {
                    await writer.WriteAsync(new { type = "error", requestId, code = "unsupported_query_view",
                        message = "Allocation explorer requires a byte-weighted artifact and supports call-tree, top-methods, hot-path, by-module, by-namespace and caller-callee." },
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                await HandleCpuQueryAsync(requestId, writer, handle, view, topN, rootMethodFilter, byteTrace, cancellationToken,
                    "allocation-tick-estimated-bytes").ConfigureAwait(false);
                return;
            }
            await HandleCpuQueryAsync(requestId, writer, handle, view, topN, rootMethodFilter, found.Artifact, cancellationToken)
                .ConfigureAwait(false);
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

    private static bool IsValidProtocolHandle(string? handle) =>
        !string.IsNullOrWhiteSpace(handle)
        && handle.Length <= 256
        && !handle.Any(char.IsControl);

    private static async Task HandleCpuQueryAsync(
        string requestId,
        ProtocolWriter writer,
        string handle,
        string view,
        int topN,
        string? rootMethodFilter,
        object artifact,
        CancellationToken cancellationToken,
        string weightUnit = "samples")
    {
        var normalizedView = view.Trim().ToLowerInvariant();
        if (!CpuSampleQueryDispatcher.IsKnownView(normalizedView))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_query_view",
                message = $"View '{view}' is not a supported CPU query view. Supported views: {string.Join(", ", CpuSampleQueryDispatcher.SessionViews)}.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var trace = CpuSampleQueryDispatcher.ResolveTrace(artifact);
        if (trace is null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "query_failed",
                message = $"Handle '{handle}' could not be rendered as a CPU sample.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        switch (normalizedView)
        {
            case CpuSampleQueryDispatcher.CallTreeView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderCallTree(
                        trace, handle, rootMethodFilter, CpuSampleQueryDispatcher.MaxProjectedCallTreeDepth,
                        CpuSampleQueryDispatcher.MaxProjectedCallTreeNodes),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.TopMethodsView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderTopMethods(trace, handle, sortBy: null, topN),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.ByModuleView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderByModule(trace, handle, topN), cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.ByNamespaceView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderByNamespace(trace, handle, topN), cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.HotPathView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderHotPath(trace, handle, CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.CallerCalleeView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderCallerCallee(trace, handle, rootMethodFilter, topN),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.TriageView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderTriage(
                        trace, handle, CpuSampleQueryDispatcher.CompactTopN,
                        CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.CodeVersionsView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderCodeVersions(trace, handle, rootMethodFilter, topN),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.InliningView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderInlining(trace, handle, rootMethodFilter, topN),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
            case CpuSampleQueryDispatcher.HotspotVariabilityView:
                await WriteCpuQueryResultAsync(requestId, writer, handle, normalizedView,
                    CpuSampleQueryDispatcher.RenderHotspotVariability(trace, handle),
                    cancellationToken, weightUnit).ConfigureAwait(false);
                break;
        }
    }

    private static async Task WriteCpuQueryResultAsync<T>(
        string requestId,
        ProtocolWriter writer,
        string handle,
        string view,
        DiagnosticResult<T> result,
        CancellationToken cancellationToken,
        string weightUnit = "samples")
    {
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
            view,
            result = new
            {
                handle,
                view,
                weightUnit,
                summary = weightUnit == "samples" ? result.Summary : "Estimated allocation bytes attributed to sampled stacks. Allocation ticks are not object counts; no retained-heap or CPU-time meaning.",
                data = JsonSerializer.SerializeToElement(result.Data, JsonOptions),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders a heap-snapshot <c>query</c> view by calling <see cref="HeapSnapshotQueryDispatcher.Dispatch"/>
    /// directly against the artifact already registered at capture time — the exact same dispatcher
    /// the MCP <c>query_snapshot</c> tool uses, so ranking/projection logic is never duplicated here.
    /// </summary>
    private static async Task HandleHeapQueryAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        IDiagnosticHandleStore handles,
        string handle,
        string view,
        int topN,
        ulong? address,
        string? acknowledgeRisk,
        bool includeSensitiveValues,
        HeapObjectInspectionOptions? objectOptions,
        TypeIdentity? typeIdentity,
        string? cursor,
        object artifact,
        CancellationToken cancellationToken)
    {
        var normalizedView = view.Trim().ToLowerInvariant();
        var isObjectRead = CliStreamingQueryNavigation.ObjectReadViews.Contains(normalizedView);
        if (!HeapQueryViews.Contains(normalizedView) && !isObjectRead)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_query_view",
                message = $"View '{view}' is not a supported heap query view. Supported views: {string.Join(", ", HeapQueryViews.Concat(CliStreamingQueryNavigation.ObjectReadViews).OrderBy(static v => v, StringComparer.Ordinal))}.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var snapshot = (HeapSnapshotArtifact)artifact;
        if (normalizedView == "type-instances")
        {
            await HandleHeapTypeInstancesAsync(
                requestId, services, writer, stdin, stdout, stderr, handles, handle, topN,
                address, acknowledgeRisk, typeIdentity, cursor, snapshot, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (typeIdentity is not null || cursor is not null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "'typeIdentity' and 'cursor' are supported only for view 'type-instances'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (isObjectRead)
        {
            await HandleHeapObjectReadAsync(
                requestId, services, writer, stdin, stdout, stderr, handles, handle, normalizedView, address, acknowledgeRisk, includeSensitiveValues, objectOptions, snapshot, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (address is { } filterAddress)
        {
            if (normalizedView != CliStreamingQueryNavigation.StaticFieldsView)
            {
                await writer.WriteAsync(new
                {
                    type = "error",
                    requestId,
                    code = "invalid_query",
                    message = $"'address' is only accepted for the 'object', 'gcroot' and '{CliStreamingQueryNavigation.StaticFieldsView}' views.",
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            snapshot = CliStreamingQueryNavigation.FilterStaticFields(snapshot, filterAddress);
        }

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
            result = TrimHeapQueryResult(result.Data! with { Address = address }),
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleHeapTypeInstancesAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        IDiagnosticHandleStore handles,
        string handle,
        int topN,
        ulong? address,
        string? acknowledgeRisk,
        TypeIdentity? typeIdentity,
        string? cursor,
        HeapSnapshotArtifact snapshot,
        CancellationToken cancellationToken)
    {
        if (address is not null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "'address' is not accepted for view 'type-instances'; use the returned instance addresses with 'object' or 'gcroot'.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (typeIdentity is null)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = $"A valid 'typeIdentity' is required for view 'type-instances'. {CliStreamingQueryNavigation.TypeIdentityFormatMessage}",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (topN > HeapTypeInstanceQueryOptions.MaxPageSize)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = $"'topN' for view 'type-instances' must be between 1 and {HeapTypeInstanceQueryOptions.MaxPageSize}.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (snapshot.Origin == HeapSnapshotOrigin.GcDump)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "view_unavailable_for_gc_dump",
                message = "View 'type-instances' is unavailable for gcdump handles because they retain aggregate type counts, not stable object addresses.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (!CliStreamingQueryNavigation.TryParseTypeInstanceCursor(cursor, handle, typeIdentity, snapshot, out var afterAddress))
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = "The type-instance cursor is malformed or belongs to a different handle, snapshot, or type.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var safetyOptions = CliStreamingQueryNavigation.CreateObjectReadSafetyOptions(
            snapshot.Origin, handle, "type-instances", acknowledgeRisk);
        var safety = await CliSafetyPreflight.RunAsync(
            safetyOptions,
            handles,
            CliExecutionContext.OneShot,
            interactive: false,
            stdin,
            stdout,
            stderr,
            artifactRoot: null,
            cancellationToken).ConfigureAwait(false);
        if (safety != CliSafetyPreflightDisposition.Proceed)
        {
            var resolvedRisk = CliInvocationSafety.Resolve(safetyOptions, handles).RiskLevel;
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "query_safety_rejected",
                message = resolvedRisk >= InvocationRiskLevel.High
                    ? $"The 'type-instances' query was not run. Re-send with acknowledgeRisk=\"{EnumName(resolvedRisk)}\" after showing the risk explanation to the user."
                    : "The 'type-instances' query was not run.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        HeapTypeInstancesInspection inspection;
        try
        {
            inspection = await services.GetRequiredService<IDumpInspector>().EnumerateTypeInstancesAsync(
                snapshot,
                typeIdentity,
                new HeapTypeInstanceQueryOptions(topN, HeapTypeInstanceQueryOptions.MaxObjectScanCount, afterAddress),
                cancellationToken).ConfigureAwait(false);
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
                code = "query_failed",
                message = $"type-instances: {ex.Message}",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var nextCursor = inspection.NextAfterAddress is { } nextAddress
            ? CliStreamingQueryNavigation.CreateTypeInstanceCursor(handle, typeIdentity, snapshot, nextAddress)
            : null;
        var quality = snapshot.Quality ?? DotnetDiagnostics.Core.Evidence.EvidenceQuality.LegacyUnknown;
        if (!inspection.Complete)
        {
            quality = GcDumpEvidence.WithProjection(
                quality,
                "query-type-instances",
                1,
                $"The page stopped after returning {inspection.Instances.Count} matching instance(s) or scanning {inspection.ScannedObjects:N0} objects; the remaining inventory is unknown until later pages complete.");
        }

        var result = new HeapSnapshotQueryResult(
            handle,
            "type-instances",
            snapshot.Origin.ToString(),
            snapshot.ProcessId,
            snapshot.CapturedAt)
        {
            Quality = quality,
            RequestedTypeIdentity = typeIdentity,
            TypeInstances = inspection.Instances,
            InstancePageSize = inspection.PageSize,
            ScannedObjects = inspection.ScannedObjects,
            InstanceEnumerationComplete = inspection.Complete,
            NextCursor = nextCursor,
            EnumeratedAt = inspection.EnumeratedAt,
            InstanceConsistency = inspection.Consistency,
            InstanceWarnings = inspection.Warnings,
        };

        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle,
            view = "type-instances",
            result = TrimHeapQueryResult(result),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the address-targeted <c>object</c>/<c>gcroot</c> heap views (#1168) through the same
    /// <see cref="IDumpInspector"/> and default preview redaction the one-shot/session CLI uses. Sensitive
    /// previews require separate, explicit per-request consent, independent of live attach risk. These
    /// re-open the snapshot origin with ClrMD (a live attach suspends the target; a dump is re-read), so
    /// a live origin clears the shared <c>query</c> safety profile (High risk/Acknowledge) first, while a dump
    /// origin uses the offline dump-source heap profile (Moderate/Warn, no attach or suspension); a <c>gcdump</c> origin has no ClrMD runtime and is rejected.
    /// </summary>
    private static async Task HandleHeapObjectReadAsync(
        string requestId,
        IServiceProvider services,
        ProtocolWriter writer,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        IDiagnosticHandleStore handles,
        string handle,
        string normalizedView,
        ulong? address,
        string? acknowledgeRisk,
        bool includeSensitiveValues,
        HeapObjectInspectionOptions? objectOptions,
        HeapSnapshotArtifact snapshot,
        CancellationToken cancellationToken)
    {
        if (address is not { } target)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "invalid_query",
                message = $"A valid 'address' is required for view '{normalizedView}'. {CliStreamingQueryNavigation.AddressFormatMessage}",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (snapshot.Origin == HeapSnapshotOrigin.GcDump)
        {
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "unsupported_query_view",
                message = $"View '{normalizedView}' needs a ClrMD runtime, which a gcdump snapshot does not have. Capture a live or dump heap snapshot instead.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var safetyOptions = CliStreamingQueryNavigation.CreateObjectReadSafetyOptions(
            snapshot.Origin, handle, normalizedView, acknowledgeRisk);
        var safety = await CliSafetyPreflight.RunAsync(
            safetyOptions,
            handles,
            CliExecutionContext.OneShot,
            interactive: false,
            stdin,
            stdout,
            stderr,
            artifactRoot: null,
            cancellationToken).ConfigureAwait(false);
        if (safety != CliSafetyPreflightDisposition.Proceed)
        {
            var resolvedRisk = CliInvocationSafety.Resolve(safetyOptions, handles).RiskLevel;
            await writer.WriteAsync(new
            {
                type = "error",
                requestId,
                code = "query_safety_rejected",
                message = resolvedRisk >= InvocationRiskLevel.High
                    ? $"The '{normalizedView}' query was not run. Re-send with acknowledgeRisk=\"{EnumName(resolvedRisk)}\" after showing the risk explanation to the user."
                    : $"The '{normalizedView}' query was not run.",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var inspector = services.GetRequiredService<IDumpInspector>();
        DiagnosticResult<HeapSnapshotQueryResult> result;
        try
        {
            result = normalizedView == "gcroot"
                ? CliCommands.BuildGcRootResult(snapshot, handle, await inspector.InspectGcRootAsync(snapshot, target, cancellationToken).ConfigureAwait(false))
                : CliCommands.BuildObjectResult(snapshot, handle, await (objectOptions is null
                    ? inspector.InspectObjectAsync(snapshot, target, cancellationToken)
                    : inspector.InspectObjectAsync(snapshot, target, objectOptions, cancellationToken)).ConfigureAwait(false),
                    includeSensitiveValues, services.GetService<SensitiveDataRedactor>());
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
                code = "query_failed",
                message = $"{normalizedView}: {ex.Message}",
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle,
            view = normalizedView,
            result = TrimHeapQueryResult(result.Data!, includeSensitiveValues),
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
    /// pagination/drilldown-chaining fields that aren't meaningful to a one-shot panel: <c>TopTypes</c>/<c>RankBy</c> (only for
    /// <c>top-types</c>, already returned inline by the `capture` response),
    /// <c>FilterTypeFullName</c>/<c>TotalRetentionPaths</c>/<c>OmittedRetentionPaths</c>/<c>RetentionFrameLimit</c>
    /// (retention-path pagination noise — the trimmed shape just returns the bounded page),
    /// <c>ObjectSize</c>/<c>DuplicateStrings</c> (address-targeted views outside this request type's
    /// scope). <c>Address</c>/<c>ObjectDetails</c>/<c>GcRoot</c> are projected with hexadecimal-string addresses (#1168).
    /// </summary>
    internal static object TrimHeapQueryResult(HeapSnapshotQueryResult data, bool includeSensitiveValues = false) => new
    {
        handle = data.Handle,
        view = data.View,
        origin = data.Origin,
        processId = data.ProcessId,
        capturedAt = data.CapturedAt,
        quality = data.Quality,
        address = data.Address is { } queryAddress ? FormatAddress(queryAddress) : null,
        objectDetails = data.ObjectDetails is { } objectDetails ? CliStreamingQueryNavigation.ProjectObject(objectDetails) : null,
        sensitiveValuesIncluded = data.ObjectDetails is not null && includeSensitiveValues,
        requestedTypeIdentity = data.RequestedTypeIdentity,
        typeInstances = data.TypeInstances?.Select(CliStreamingQueryNavigation.ProjectTypeInstance),
        instancePageSize = data.InstancePageSize,
        scannedObjects = data.ScannedObjects,
        instanceEnumerationComplete = data.InstanceEnumerationComplete,
        nextCursor = data.NextCursor,
        enumeratedAt = data.EnumeratedAt,
        instanceConsistency = data.InstanceConsistency,
        instanceWarnings = data.InstanceWarnings,
        gcRoot = data.GcRoot is { } gcRoot ? CliStreamingQueryNavigation.ProjectGcRoot(gcRoot) : null,
        retentionPaths = data.RetentionPaths?.Select(ProjectRetentionPath),
        rootsByKind = data.RootsByKind,
        finalizableObjects = data.FinalizableObjects,
        segments = data.Segments?.Select(static segment => new
        {
            logicalHeap = segment.LogicalHeap,
            kind = segment.Kind,
            generation = segment.Generation,
            start = FormatAddress(segment.Start),
            end = FormatAddress(segment.End),
            length = segment.Length,
            committedBytes = segment.CommittedBytes,
            reservedBytes = segment.ReservedBytes,
            usedBytes = segment.UsedBytes,
            freeBytes = segment.FreeBytes,
            objectCount = segment.ObjectCount,
            freeObjectCount = segment.FreeObjectCount,
            freePercent = segment.FreePercent,
            largestObservedMaterialFreeBlockBytes = segment.LargestObservedMaterialFreeBlockBytes,
            observedMaterialFreeBlockCount = segment.ObservedMaterialFreeBlockCount,
            retainedMaterialFreeBlockCount = segment.RetainedMaterialFreeBlockCount,
            omittedMaterialFreeBlockCount = segment.OmittedMaterialFreeBlockCount,
            materialFreeBlockRetentionLimit = segment.MaterialFreeBlockRetentionLimit,
            materialFreeBlockMinimumBytes = segment.MaterialFreeBlockMinimumBytes,
            topObservedMaterialFreeBlocks = segment.TopObservedMaterialFreeBlocks?.Select(static range => new
            {
                start = FormatAddress(range.Start),
                end = FormatAddress(range.End),
                length = range.Length,
            }),
            freeBlockEvidenceState = segment.FreeBlockEvidenceState?.ToString().ToLowerInvariant(),
            freeBlockEvidenceLimitations = segment.FreeBlockEvidenceLimitations,
        }),
        staticFields = data.StaticFields?.Select(static field => new
        {
            containingTypeFullName = field.ContainingTypeFullName,
            moduleName = field.ModuleName,
            fieldName = field.FieldName,
            fieldToken = field.FieldToken,
            valueAddress = FormatAddress(field.ValueAddress),
            valueTypeFullName = field.ValueTypeFullName,
            directlyReferencedBytes = field.DirectlyReferencedBytes,
            appDomainId = field.AppDomainId,
            containingTypeIdentity = field.ContainingTypeIdentity,
        }),
        delegateTargets = data.DelegateTargets,
        gcHandles = data.GcHandles,
        asyncOperations = data.AsyncOperations?.Select(static operation => new
        {
            stateMachineTypeFullName = operation.StateMachineTypeFullName,
            state = operation.State,
            awaiterTypeFullName = operation.AwaiterTypeFullName,
            directSizeBytes = operation.DirectSizeBytes,
            stateMachineAddress = FormatAddress(operation.StateMachineAddress),
            taskAddress = operation.TaskAddress is { } taskAddress ? FormatAddress(taskAddress) : null,
            taskId = operation.TaskId,
            taskTypeFullName = operation.TaskTypeFullName,
            continuationObjectTypeFullName = operation.ContinuationObjectTypeFullName,
            stack = operation.Stack?.Select(static frame => new
            {
                stateMachineTypeFullName = frame.StateMachineTypeFullName,
                state = frame.State,
                awaiterTypeFullName = frame.AwaiterTypeFullName,
                stateMachineAddress = FormatAddress(frame.StateMachineAddress),
                taskAddress = frame.TaskAddress is { } frameTaskAddress ? FormatAddress(frameTaskAddress) : null,
                taskId = frame.TaskId,
                continuationObjectTypeFullName = frame.ContinuationObjectTypeFullName,
            }),
            observedOrder = operation.ObservedOrder,
        }),
        sortedBy = data.SortedBy,
        timers = data.Timers,
        assemblyLoadContexts = data.AssemblyLoadContexts is { } contexts
            ? new
            {
                totalContexts = contexts.TotalContexts,
                collectibleContexts = contexts.CollectibleContexts,
                suspectedLeakedCollectibleContexts = contexts.SuspectedLeakedCollectibleContexts,
                contexts = contexts.Contexts.Select(static context => new
                {
                    address = FormatAddress(context.Address),
                    typeFullName = context.TypeFullName,
                    name = context.Name,
                    isCollectible = context.IsCollectible,
                    isDefault = context.IsDefault,
                    assemblyCount = context.AssemblyCount,
                    assemblies = context.Assemblies,
                    loaderAllocatorHandle = context.LoaderAllocatorHandle is { } loaderAllocatorHandle
                        ? FormatAddress(loaderAllocatorHandle)
                        : null,
                    liveObjectCount = context.LiveObjectCount,
                    liveBytes = context.LiveBytes,
                    suspectedLeak = context.SuspectedLeak,
                    retentionTargetKind = context.RetentionTargetKind,
                    retentionTargetAddress = context.RetentionTargetAddress is { } retentionTargetAddress
                        ? FormatAddress(retentionTargetAddress)
                        : null,
                    retentionTargetTypeFullName = context.RetentionTargetTypeFullName,
                    retentionPath = context.RetentionPath is { } retentionPath
                        ? ProjectRetentionPath(retentionPath)
                        : null,
                    notes = context.Notes,
                }),
                notes = contexts.Notes,
            }
            : null,
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
    internal static object TrimThreadQueryResult(ThreadSnapshotQueryResult data) => new
    {
        handle = data.Handle,
        view = data.View,
        origin = data.Origin,
        processId = data.ProcessId,
        capturedAt = data.CapturedAt,
        walkDuration = data.WalkDuration,
        deadlocks = data.Deadlocks?.Select(static cycle => new
        {
            cycleMembers = cycle.CycleMembers,
            lockChain = cycle.LockChain.Select(static link => new
            {
                waitingThreadId = link.WaitingThreadId,
                ownerThreadId = link.OwnerThreadId,
                lockObjectAddress = FormatAddress(link.LockObjectAddress),
                lockObjectTypeFullName = link.LockObjectTypeFullName,
                lockKind = link.LockKind,
                edgeSource = link.EdgeSource,
                confidence = link.Confidence,
            }),
            recommendedCommands = cycle.RecommendedCommands,
            classification = cycle.Classification,
            confidence = cycle.Confidence,
        }),
        uniqueStacks = data.UniqueStacks?.Select(static group => new
        {
            signatureHash = group.SignatureHash,
            threadCount = group.ThreadCount,
            threadPercentage = group.ThreadPercentage,
            inferredWaitReason = group.InferredWaitReason,
            sampleThreads = group.SampleThreads,
            canonicalFrames = group.CanonicalFrames.Select(static frame => new
            {
                kind = frame.Kind,
                displayName = frame.DisplayName,
                typeFullName = frame.TypeFullName,
                moduleName = frame.ModuleName,
                instructionPointer = FormatAddress(frame.InstructionPointer),
                stackPointer = FormatAddress(frame.StackPointer),
                identity = frame.Identity,
                addressKind = frame.AddressKind,
                rva = frame.Rva is { } rva ? FormatAddress(rva) : null,
                loadBase = frame.LoadBase is { } loadBase ? FormatAddress(loadBase) : null,
                buildId = frame.BuildId,
            }),
        }),
        waitChains = data.WaitChains,
        threadPool = data.ThreadPool is { } threadPool
            ? new
            {
                initialized = threadPool.Initialized,
                usingPortableThreadPool = threadPool.UsingPortableThreadPool,
                usingWindowsThreadPool = threadPool.UsingWindowsThreadPool,
                workers = threadPool.Workers,
                iocp = threadPool.Iocp,
                queues = new
                {
                    globalQueueLength = threadPool.Queues.GlobalQueueLength,
                    globalQueues = threadPool.Queues.GlobalQueues.Select(static queue => new
                    {
                        name = queue.Name,
                        queueLength = queue.QueueLength,
                        queueIndex = queue.QueueIndex,
                        queueAddress = queue.QueueAddress is { } queueAddress
                            ? FormatAddress(queueAddress)
                            : null,
                    }),
                    localQueues = threadPool.Queues.LocalQueues.Select(static queue => new
                    {
                        queueAddress = FormatAddress(queue.QueueAddress),
                        queueLength = queue.QueueLength,
                        managedThreadId = queue.ManagedThreadId,
                        osThreadId = queue.OSThreadId,
                        queueIndex = queue.QueueIndex,
                    }),
                },
                pendingWorkItems = threadPool.PendingWorkItems,
                cpuUtilization = threadPool.CpuUtilization,
                hillClimbing = threadPool.HillClimbing,
                notes = threadPool.Notes,
            }
            : null,
    };

    private static object ProjectRetentionPath(RetentionPath path) => new
    {
        targetTypeFullName = path.TargetTypeFullName,
        targetObjectAddress = FormatAddress(path.TargetObjectAddress),
        chain = path.Chain.Select(static frame => new
        {
            typeFullName = frame.TypeFullName,
            objectAddress = FormatAddress(frame.ObjectAddress),
            rootKind = frame.RootKind,
        }),
        truncated = path.Truncated,
        targetIdentity = path.TargetIdentity,
    };

    private static string FormatAddress(ulong address) =>
        "0x" + address.ToString("x", CultureInfo.InvariantCulture);

    private static Task WriteHandleUnavailableAsync(
        ProtocolWriter writer,
        string requestId,
        string side,
        string handle,
        DiagnosticHandleLookupResult lookup,
        CancellationToken cancellationToken)
    {
        var code = lookup.Status switch
        {
            DiagnosticHandleLookupStatus.Expired => "expired_handle",
            DiagnosticHandleLookupStatus.CapacityEvicted => "evicted_handle",
            _ => "unknown_handle",
        };
        var reason = lookup.Status switch
        {
            DiagnosticHandleLookupStatus.Expired => "expired",
            DiagnosticHandleLookupStatus.CapacityEvicted => "was capacity-evicted",
            _ => "is unknown or was explicitly released",
        };
        return writer.WriteAsync(new
        {
            type = "error",
            requestId,
            code,
            message = $"{side} handle '{handle}' {reason}. Capture a fresh CPU pair and retry.",
        }, cancellationToken);
    }

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

    private static async Task<ProtocolInputLine> ReadProtocolLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var line = new System.Text.StringBuilder();
        var byteCount = 0;
        var oversized = false;
        var readAny = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return !readAny
                    ? new ProtocolInputLine(null, false)
                    : oversized
                        ? new ProtocolInputLine(null, true)
                        : new ProtocolInputLine(line.ToString(), false);
            }

            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    return oversized
                        ? new ProtocolInputLine(null, true)
                        : new ProtocolInputLine(line.ToString().TrimEnd('\r'), false);
                }

                readAny = true;
                if (oversized)
                {
                    continue;
                }

                var characterBytes = System.Text.Encoding.UTF8.GetByteCount(buffer.AsSpan(index, 1));
                if (byteCount + characterBytes > MaxControlLineBytes)
                {
                    oversized = true;
                    line.Clear();
                    continue;
                }

                line.Append(character);
                byteCount += characterBytes;
            }
        }
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

    internal static bool TryParseEventSourceOptions(JsonElement root, out object? options, out string? error)
    {
        options = null;
        error = null;
        if (!root.TryGetProperty("providers", out var providersElement)
            || providersElement.ValueKind != JsonValueKind.Array
            || providersElement.GetArrayLength() is < 1 or > EventSourceSessionOptions.MaxProviderCount)
        {
            error = $"'providers' must be an array containing 1..{EventSourceSessionOptions.MaxProviderCount} provider objects.";
            return false;
        }

        var providers = new List<EventSourceProviderSubscription>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerElement in providersElement.EnumerateArray())
        {
            if (providerElement.ValueKind != JsonValueKind.Object
                || !TryGetString(providerElement, "name", out var name)
                || string.IsNullOrWhiteSpace(name)
                || name.Length > EventSourceSessionOptions.MaxProviderNameLength)
            {
                error = $"Each provider must have a non-empty 'name' of at most {EventSourceSessionOptions.MaxProviderNameLength} characters.";
                return false;
            }

            if (!seenNames.Add(name))
            {
                error = "Provider names must be unique (case-insensitive).";
                return false;
            }

            var eventLevel = 4;
            if (providerElement.TryGetProperty("eventLevel", out var levelElement)
                && (!levelElement.TryGetInt32(out eventLevel) || eventLevel is < 0 or > 5))
            {
                error = "'eventLevel' must be an integer between 0 and 5.";
                return false;
            }

            if (!TryGetString(providerElement, "keywords", out var keywordsText)
                || !long.TryParse(keywordsText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var keywords))
            {
                error = "'keywords' must be a signed 64-bit integer encoded as a decimal string.";
                return false;
            }

            providers.Add(new EventSourceProviderSubscription(name, eventLevel, keywords));
        }

        var observationCapacity = EventSourceSessionOptions.DefaultObservationCapacity;
        if (root.TryGetProperty("observationCapacity", out var capacityElement)
            && (!capacityElement.TryGetInt32(out observationCapacity)
                || observationCapacity is < 1 or > EventSourceSessionOptions.MaxObservationCapacity))
        {
            error = $"'observationCapacity' must be between 1 and {EventSourceSessionOptions.MaxObservationCapacity}.";
            return false;
        }

        string? acknowledgeRisk = null;
        if (root.TryGetProperty("acknowledgeRisk", out var acknowledgementElement))
        {
            if (acknowledgementElement.ValueKind != JsonValueKind.String
                || acknowledgementElement.GetString() is not ("high" or "critical"))
            {
                error = "'acknowledgeRisk' must be 'high' or 'critical' when provided.";
                return false;
            }

            acknowledgeRisk = acknowledgementElement.GetString();
        }

        options = new EventSourceStartOptions(
            new EventSourceSessionOptions { Providers = providers, ObservationCapacity = observationCapacity },
            acknowledgeRisk);
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

    private static IDisposable AttachEventSourceForwarding(
        ComposedDiagnosticSession composed, ProtocolWriter writer, string sessionId, EventGate eventsGate)
        => composed.Attach<EventSourceLiveObservation>(async (observation, handlerCancellationToken) =>
        {
            await eventsGate.Task.WaitAsync(handlerCancellationToken).ConfigureAwait(false);
            if (eventsGate.Suppressed)
            {
                return;
            }

            await writer.WriteEventAsync(new
            {
                type = "observation",
                kind = "event_source",
                sessionId,
                observation.Sequence,
                observation.Timestamp,
                receivedAt = observation.ReceivedAt,
                provider = observation.Provider,
                eventName = observation.EventName,
                eventId = observation.EventId,
                version = observation.Version,
                level = observation.Level,
                threadId = observation.ThreadId,
                activityId = observation.ActivityId,
                relatedActivityId = observation.RelatedActivityId,
                payload = observation.Payload,
                metadataTruncated = observation.MetadataTruncated,
                payloadTruncated = observation.PayloadTruncated,
                payloadFieldsUnavailable = observation.PayloadFieldsUnavailable,
                omittedPayloadFields = observation.OmittedPayloadFields,
                omittedPayloadCharacters = observation.OmittedPayloadCharacters,
                unsupportedPayloadValues = observation.UnsupportedPayloadValues,
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
Send {"type":"hello","protocolVersion":2} before start/stop/capture commands. The hello response
includes the protocol version and CLI informational version.
Start requires requestId and processId. Send either the legacy single-kind shape
(optional providers/intervalSeconds/observationCapacity, implying kind "counters") or
{"kinds":[{"kind":"counters",...},{"kind":"gc",...}]} to run several live kinds in one
composed session. An EventSource example is:
{"type":"start","requestId":"events-1","processId":1234,"kinds":[{"kind":"event_source","providers":[{"name":"Microsoft-Windows-DotNETRuntime","eventLevel":4,"keywords":"0"}],"observationCapacity":256,"acknowledgeRisk":"high"}]}
to collect EventPipe/EventSource events from 1-8 explicit providers. Provider names are unique and
at most 256 characters; eventLevel is 0-5; keywords is a signed 64-bit decimal string. EventSource
frames are bounded to 16 KiB UTF-8 and expose bounded inert-string payloads. Provider payloads,
especially exception messages, may contain sensitive data. Non-allowlisted providers require
acknowledgeRisk="high". No wildcard providers or native Windows ETW are supported.
Send stop or cancel with the returned sessionId. Send
{"type":"capture","requestId":...,"kind":"cpu","processId":...,"durationSeconds":10,"topN":25}
for a one-shot point-in-time capture that does not open a live session. Exception captures use
"kind":"exceptions" or "kind":"crash-guard", with optional "maxRecent" (default 100, range 1-500).
The result includes observed type counts, retained recent events and explicit omission notes.
Only crash-guard includes available managed stacks. Messages and stacks may contain sensitive data.
Start capture before load; writing a request is not an EventPipe readiness acknowledgement.
If hello advertises requestActivities.version=1, use kind "request-activities" with required
processId and 1-20 exact ActivitySource "sources", durationSeconds 1-300. One union session
retains at most 100 pending requests and 200 completed Activities in one replaceable artifact.
Query "requests" or "related-activities" using its handle AND captureId; related-activities
also requires the retained requestRowId. topN is 1-100. Cancel using the capture requestId.
No tags, persistence or privileged attach; incomplete evidence is explicit, not causality.
A heap snapshot capture
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
requires (otherwise error code "invalid_query"); growth also uses it to select one objective type.
If hello advertises heapGrowth.version=1 and heapGrowth.available=true, compare two same-process
heap handles with
{"type":"query","requestId":...,"handle":"<current>","baselineHandle":"<earlier>","view":"growth",
"rankBy":"bytes"|"instances","minDeltaPct":5,"topN":25}. This unfiltered legacy shape is unchanged.
Add "typeFilter":"Exact.Full.Type" for verdict-free objective retained-growth evidence. The optional
"comparisonPairs" array adds at most 7 bounded attempts (8 including the primary pair); completed
entries name baseline/current handles, while failed/aborted entries retain attemptId, outcome and
failureReason without replacement. Entries may separately carry observedGen2Collections and an
allocation object with sampledBytes/sampleEvents/lostOrDropped; allocation never contributes to
retained bytes or instances. The captures must have matching
live/dump/gcdump provenance and chronological order. heapGrowth.storeCapacity reports the effective
shared handle-store capacity; growth requires heapGrowth.minimumStoreCapacity=2. Both remain
ordinary bounded in-memory
entries in the shared configured MemoryDiagnosticHandleStore: this is neither an unbounded history
nor a dedicated two-slot/pinned pair. Handles are not persisted and can expire, be capacity-evicted,
or become unavailable when a live target exits. At query time, live/gcdump pairs require the target
PID to still exist and its current process lifetime to include both capture timestamps; dump pairs
remain offline and do not perform a live-process check.
Heap handles also accept the address views object and gcroot (#1168), which require "address" and
re-open the snapshot origin with ClrMD (a live attach suspends the target). Live reads are High
risk/Acknowledge like query --view object, so a live request is rejected with code
"query_safety_rejected" unless "acknowledgeRisk" equals the resolved risk name (currently "high");
dump reads are offline Moderate/Warn and need no operational acknowledgement.
Object previews are metadata-only by default. Only view="object" supports
"includeSensitiveValues":true with separate "acknowledgeSensitiveValues":true after explicit
consent for that single request; operational risk consent never authorizes raw values. Missing
sensitive consent returns "sensitive_values_rejected". The result's "sensitiveValuesIncluded"
confirms disclosure. Strings are bounded to 256 characters, arrays to 8 elements, fields to 256
entries and value-type depth 3. Do not automatically persist/log/export the revealed previews.
Object array reads accept "arrayStartIndex" (non-negative Int32, default 0) and "arrayCount"
(integer 1-8, default 8). Results confirm arrayStartIndex/arrayRequestedCount and include flattened
fields for value-type elements; one 256-field budget covers the entire range. Indices are
zero-based linear row-major positions, not collection-specific active entries. Each range
requires a fresh request; live risk and sensitive-value acknowledgement remain independent.
A gcdump snapshot is rejected. "address" must be a JSON string
holding a non-zero 0x-prefixed hexadecimal value of at most 16 digits (JSON numbers and decimal
strings are rejected with "invalid_query"); on static-fields it filters the captured fields to
those referencing exactly that object, and every other view rejects it. All addresses in query
results are 0x hexadecimal strings.
""";


    private sealed record StartRequest(string RequestId, int ProcessId, IReadOnlyList<KindRequest> Kinds);

    private sealed record KindRequest(string Kind, object? Options);

    private sealed record ProtocolInputLine(string? Value, bool Oversized);

    private delegate bool TryParseKindOptions(JsonElement element, out object? options, out string? error);

    private sealed record KindDescriptor(
        string Kind,
        TryParseKindOptions ParseOptions,
        Func<IServiceProvider, int, object, IDiagnosticSession> CreateChildSession,
        Func<ComposedDiagnosticSession, ProtocolWriter, string, EventGate, IDisposable> AttachForwarding);

    internal static int GetComposedEventCapacity(int maximumRequestedCapacity, bool includesEventSource) =>
        includesEventSource
            ? Math.Min(maximumRequestedCapacity, EventSourceSessionOptions.MaxObservationCapacity)
            : maximumRequestedCapacity;

    private static int GetObservationCapacity(object options) => options switch
    {
        CounterSessionOptions counters => counters.ObservationCapacity,
        GcSessionOptions gc => gc.ObservationCapacity,
        EventSourceStartOptions events => events.SessionOptions.ObservationCapacity,
        _ => 256,
    };

    internal sealed record EventSourceStartOptions(
        EventSourceSessionOptions SessionOptions,
        string? AcknowledgeRisk);

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
        ComposedDiagnosticSession session, string sessionId, IReadOnlyList<string> kinds, ProtocolWriter writer) : IDisposable
    {
        // Matches the Core session's own shutdown budget (see EventPipeDiagnosticSessionBase.ShutdownWaitBudget)
        // so a stuck stdout pipe cannot make stop/cancel/EOF handling hang indefinitely.
        private static readonly TimeSpan TerminalWriteBudget = TimeSpan.FromSeconds(5);
        private readonly CancellationTokenSource _qualityCancellation = new();
        private Task _qualityTask = Task.CompletedTask;

        public EventGate EventsGate { get; } = new();
        public List<IDisposable> Subscriptions { get; } = [];

        public ComposedDiagnosticSession Session { get; } = session;
        public string SessionId { get; } = sessionId;
        public IReadOnlyList<string> Kinds { get; } = kinds;
        public EventSourceSession? EventSourceSession { get; set; }
        public Task TerminalTask { get; private set; } = Task.CompletedTask;

        public void AllowEvents() => EventsGate.Allow();

        public void SuppressEvents() => EventsGate.Suppress();

        public void Dispose() => _qualityCancellation.Dispose();

        public void StartTerminalPump()
        {
            if (EventSourceSession is not null)
            {
                _qualityTask = PublishQualityWhileActiveAsync(EventSourceSession);
            }

            TerminalTask = PublishTerminalWhenCompleteAsync();
        }

        private async Task PublishQualityWhileActiveAsync(EventSourceSession eventSourceSession)
        {
            try
            {
                while (!Session.Completion.IsCompleted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _qualityCancellation.Token).ConfigureAwait(false);
                    if (Session.Completion.IsCompleted)
                    {
                        break;
                    }

                    await writer.WriteAsync(new
                    {
                        type = "quality",
                        kind = "event_source",
                        sessionId = SessionId,
                        observedAt = DateTimeOffset.UtcNow,
                        eventPipeEventsLost = (long?)null,
                        eventPipeLossAvailable = false,
                        coreQueueDropped = eventSourceSession.DroppedObservations,
                        cliDispatchQueueDropped = Session.DroppedEvents,
                    }, _qualityCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_qualityCancellation.IsCancellationRequested)
            {
            }
        }

        private async Task PublishTerminalWhenCompleteAsync()
        {
            var completion = await Session.Completion.ConfigureAwait(false);
            _qualityCancellation.Cancel();
            try
            {
                await _qualityTask.WaitAsync(TerminalWriteBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            // The session has already finished dispatching; this flush is best-effort and bounded so a
            // wedged stdout consumer cannot block the CLI's stop/cancel/EOF handling forever.
            try
            {
                await EventsGate.Task.WaitAsync(TerminalWriteBudget).ConfigureAwait(false);
                if (EventSourceSession is not null)
                {
                    await writer.WriteAsync(new
                    {
                        type = "quality",
                        kind = "event_source",
                        sessionId = SessionId,
                        observedAt = DateTimeOffset.UtcNow,
                        eventPipeEventsLost = completion.EventPipeEventsLost,
                        eventPipeLossAvailable = completion.EventPipeEventsLost.HasValue,
                        coreQueueDropped = EventSourceSession.DroppedObservations,
                        cliDispatchQueueDropped = Session.DroppedEvents,
                    }, CancellationToken.None).WaitAsync(TerminalWriteBudget).ConfigureAwait(false);
                }

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
                Dispose();
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
            await WriteSerializedAsync(json, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteEventAsync(object frame, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(frame, frame.GetType(), JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > EventSourceSessionOptions.MaxEventFrameBytes)
            {
                throw new InvalidOperationException(
                    $"Live EventSource frame exceeded the {EventSourceSessionOptions.MaxEventFrameBytes}-byte UTF-8 limit.");
            }

            await WriteSerializedAsync(json, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteSerializedAsync(string json, CancellationToken cancellationToken)
        {
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
