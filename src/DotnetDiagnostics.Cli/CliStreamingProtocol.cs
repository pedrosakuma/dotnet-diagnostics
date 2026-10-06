using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Counters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli;

internal static class CliStreamingProtocol
{
    private const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
        var factory = host.Services.GetRequiredService<ICounterSessionFactory>();
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
                            if (activeSession is not null && activeSession.Session.Completion.IsCompleted)
                            {
                                await activeSession.TerminalTask.ConfigureAwait(false);
                                activeSession = null;
                            }

                            if (!TryReadStartRequest(document.RootElement, out var request, out var validationError))
                            {
                                await WriteErrorAsync(writer, "invalid_start", validationError!).ConfigureAwait(false);
                                break;
                            }

                            if (activeSession is not null)
                            {
                                await WriteErrorAsync(writer, "session_active", "Stop the active session before starting another.")
                                    .ConfigureAwait(false);
                                break;
                            }

                            var safetyOptions = new CliOptions
                            {
                                Command = "stream",
                                Kind = "counters",
                                Pid = request!.ProcessId,
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
                                await WriteErrorAsync(writer, "safety_rejected", "The live counter session was not started.")
                                    .ConfigureAwait(false);
                                break;
                            }

                            try
                            {
                                var session = factory.CreateSession(
                                    request.ProcessId,
                                    request.Options);
                                var candidate = new ActiveSession(session, Guid.NewGuid().ToString("N"), writer);
                                try
                                {
                                    await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
                                    activeSession = candidate;
                                    await writer.WriteAsync(new
                                    {
                                        type = "started",
                                        requestId = request.RequestId,
                                        sessionId = activeSession.SessionId,
                                        processId = session.ProcessId,
                                    }, cancellationToken).ConfigureAwait(false);
                                    candidate.AllowEvents();
                                    candidate.StartTerminalPump();
                                }
                                catch
                                {
                                    candidate.AllowEvents();
                                    await session.DisposeAsync().ConfigureAwait(false);
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
                                    requestId = request!.RequestId,
                                    code = "start_failed",
                                    message = ex.Message,
                                }, cancellationToken).ConfigureAwait(false);
                            }
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

        request = new StartRequest(
            requestId,
            processId,
            new CounterSessionOptions
            {
                Providers = providers,
                IntervalSeconds = intervalSeconds,
                ObservationCapacity = observationCapacity,
            });
        return true;
    }

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
Send {"type":"hello","protocolVersion":1} before start/stop commands.
Start requires requestId and processId; optional fields are providers, intervalSeconds,
and observationCapacity. Send stop or cancel with the returned sessionId.
""";

    private sealed record StartRequest(string RequestId, int ProcessId, CounterSessionOptions Options);

    private sealed class ActiveSession(CounterSession session, string sessionId, ProtocolWriter writer)
    {
        // Matches the Core session's own shutdown budget (see CounterSession.ShutdownWaitBudget) so a
        // stuck stdout pipe cannot make stop/cancel/EOF handling hang indefinitely.
        private static readonly TimeSpan TerminalWriteBudget = TimeSpan.FromSeconds(5);

        private readonly TaskCompletionSource _eventsAllowed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDisposable? _counterSubscription;

        public CounterSession Session { get; } = session;
        public string SessionId { get; } = sessionId;
        public Task TerminalTask { get; private set; } = Task.CompletedTask;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _counterSubscription = Session.Attach<CounterObservation>(async (observation, handlerCancellationToken) =>
            {
                await _eventsAllowed.Task.WaitAsync(handlerCancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(new
                {
                    type = "observation",
                    sessionId = SessionId,
                    observation.Sequence,
                    observation.Timestamp,
                    counter = observation.Counter,
                }, handlerCancellationToken).ConfigureAwait(false);
            });
            await Session.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public void AllowEvents() => _eventsAllowed.TrySetResult();

        public void StartTerminalPump() => TerminalTask = PublishTerminalWhenCompleteAsync();

        private async Task PublishTerminalWhenCompleteAsync()
        {
            var completion = await Session.Completion.ConfigureAwait(false);
            // The session has already finished dispatching; this flush is best-effort and bounded so a
            // wedged stdout consumer cannot block the CLI's stop/cancel/EOF handling forever.
            try
            {
                await _eventsAllowed.Task.WaitAsync(TerminalWriteBudget).ConfigureAwait(false);
                await writer.WriteAsync(new
                {
                    type = "terminal",
                    sessionId = SessionId,
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
                _counterSubscription?.Dispose();
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
