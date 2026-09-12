using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public interface IAgentModelTransport
{
    Task<AgentModelTurn> CompleteAsync(
        AgentModelConfiguration configuration,
        IReadOnlyList<JsonObject> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken);
}

public sealed class OpenAiCompatibleAgentTransport(HttpClient httpClient, string apiKey)
    : IAgentModelTransport
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string _apiKey = string.IsNullOrWhiteSpace(apiKey)
        ? throw new ArgumentException("An explicitly configured model API key is required.", nameof(apiKey))
        : apiKey;

    public async Task<AgentModelTurn> CompleteAsync(
        AgentModelConfiguration configuration,
        IReadOnlyList<JsonObject> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = configuration.Model,
            ["temperature"] = configuration.Temperature,
            ["max_completion_tokens"] = configuration.MaximumOutputTokens,
            ["messages"] = new JsonArray(messages.Select(message => message.DeepClone()).ToArray()),
            ["tools"] = new JsonArray(tools.Select(ToWireTool).ToArray()),
            ["tool_choice"] = "auto",
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint)
        {
            Content = JsonContent.Create(request),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var raw = await ReadBoundedAsync(
            response.Content,
            configuration.MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentTransportException(
                $"Model endpoint returned HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0].GetProperty("message");
        var content = choice.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;
        var calls = new List<AgentToolCall>();
        if (choice.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                calls.Add(new AgentToolCall(
                    call.GetProperty("id").GetString() ?? throw new JsonException("Tool call id was missing."),
                    function.GetProperty("name").GetString() ?? throw new JsonException("Tool call name was missing."),
                    function.GetProperty("arguments").GetString() ?? "{}"));
            }
        }

        int? inputTokens = null;
        int? outputTokens = null;
        decimal? estimatedCost = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = OptionalInt(usage, "prompt_tokens");
            outputTokens = OptionalInt(usage, "completion_tokens");
            estimatedCost = OptionalDecimal(usage, "estimated_cost_usd")
                ?? OptionalDecimal(usage, "cost");
        }

        return new AgentModelTurn(
            raw,
            content,
            calls,
            new AgentModelUsage(inputTokens, outputTokens, estimatedCost),
            root.TryGetProperty("id", out var id) ? id.GetString() : null);
    }

    private static JsonObject ToWireTool(AgentToolDefinition tool)
        => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
            },
        };

    private static int? OptionalInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static decimal? OptionalDecimal(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var parsed)
            ? parsed
            : null;

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new AgentTransportException("Model response exceeded the configured response-byte budget.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16_384));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new AgentTransportException("Model response exceeded the configured response-byte budget.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}

public sealed class AgentTransportException(
    string message,
    System.Net.HttpStatusCode? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
}
