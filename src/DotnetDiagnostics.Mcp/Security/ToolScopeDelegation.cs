using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// Creates and verifies short-lived, request-bound scope delegations for pod-local tool calls.
/// </summary>
internal static class ToolScopeDelegation
{
    internal const string ArgumentName = "__dotnetDiagnosticsScopeDelegation";
    internal const string EnvironmentVariableName = "MCP_INTERNAL_SCOPE_DELEGATION_KEY";
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(5);
    private static readonly byte[] SignatureDomain =
        "dotnet-diagnostics-mcp\0tools/call\0scope-delegation\0v1\0"u8.ToArray();
    private static readonly ConcurrentDictionary<string, long> UsedNonces = new(StringComparer.Ordinal);

    public static CallToolRequestParams Add(
        CallToolRequestParams request,
        ToolScopeRegistry.AuthorizationResult authorization,
        BearerPrincipal caller,
        string secret,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var arguments = request.Arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(request.Arguments, StringComparer.Ordinal);
        arguments.Remove(ArgumentName);
        var unsignedRequest = Clone(request, arguments);
        arguments[ArgumentName] = JsonSerializer.SerializeToElement(
            CreateToken(unsignedRequest, authorization, caller, secret, timeProvider));

        return Clone(request, arguments);
    }

    public static bool TryConsume(
        CallToolRequestParams request,
        ToolScopeRegistry registry,
        ToolScopeResolutionPolicies policies,
        string? secret,
        TimeProvider? timeProvider,
        out BearerPrincipal? delegatedPrincipal,
        out string failure)
    {
        delegatedPrincipal = null;
        failure = string.Empty;
        var arguments = request.Arguments;
        if (arguments is null || !arguments.TryGetValue(ArgumentName, out var tokenElement))
        {
            return false;
        }

        arguments.Remove(ArgumentName);
        if (string.IsNullOrWhiteSpace(secret))
        {
            failure = "internal scope delegation is not configured";
            return false;
        }
        if (tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            failure = "internal scope delegation token is malformed";
            return false;
        }

        var parts = tokenElement.GetString()!.Split('.');
        if (parts.Length != 2 ||
            !TryBase64UrlDecode(parts[0], out var payloadBytes) ||
            !TryBase64UrlDecode(parts[1], out var presentedSignature))
        {
            failure = "internal scope delegation token is malformed";
            return false;
        }

        var expectedSignature = ComputeSignature(secret, payloadBytes);
        if (presentedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(presentedSignature, expectedSignature))
        {
            failure = "internal scope delegation signature is invalid";
            return false;
        }

        DelegationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DelegationPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            failure = "internal scope delegation payload is malformed";
            return false;
        }

        if (payload is null ||
            payload.Version != 1 ||
            payload.Scopes is null ||
            payload.Scopes.Length == 0 ||
            string.IsNullOrWhiteSpace(payload.Nonce) ||
            !string.Equals(payload.Tool, request.Name, StringComparison.Ordinal) ||
            !string.Equals(payload.RequestHash, ComputeRequestHash(request), StringComparison.Ordinal))
        {
            failure = "internal scope delegation does not match this invocation";
            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds);
        if (expiresAt < now - MaximumClockSkew || expiresAt > now + Lifetime + MaximumClockSkew)
        {
            failure = "internal scope delegation has expired or has an invalid lifetime";
            return false;
        }

        var scopes = payload.Scopes.ToImmutableHashSet(StringComparer.Ordinal);
        if (scopes.Count != payload.Scopes.Length)
        {
            failure = "internal scope delegation contains duplicate scopes";
            return false;
        }

        var principal = new BearerPrincipal("internal-proxy-delegation", scopes);
        var authorization = registry.Authorize(
            request.Name,
            arguments,
            principal,
            proxyInvocation: true,
            policies: policies);
        if (!authorization.IsAllowed ||
            !scopes.SetEquals(GetDelegatedScopes(request.Name, authorization, principal)))
        {
            failure = "internal scope delegation does not contain the exact required scopes";
            return false;
        }

        PruneReplayCache(now.ToUnixTimeSeconds());
        if (!UsedNonces.TryAdd(payload.Nonce, payload.ExpiresAtUnixSeconds))
        {
            failure = "internal scope delegation has already been used";
            return false;
        }

        delegatedPrincipal = principal;
        return true;
    }

    public static byte[] AddToJsonRpcBody(
        ReadOnlyMemory<byte> body,
        ToolScopeRegistry registry,
        ToolScopeResolutionPolicies policies,
        BearerPrincipal caller,
        string secret,
        TimeProvider? timeProvider = null)
    {
        var root = JsonNode.Parse(body.Span) ??
            throw new JsonException("JSON-RPC body is empty.");
        AddToEnvelope(root, registry, policies, caller, secret, timeProvider);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    internal static string CreateToken(
        CallToolRequestParams request,
        ToolScopeRegistry.AuthorizationResult authorization,
        BearerPrincipal caller,
        string secret,
        TimeProvider? timeProvider = null)
    {
        if (!authorization.IsAllowed)
        {
            throw new InvalidOperationException("Cannot delegate an invocation that was not authorized.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var payload = new DelegationPayload(
            Version: 1,
            Tool: request.Name,
            RequestHash: ComputeRequestHash(request),
            Scopes: GetDelegatedScopes(request.Name, authorization, caller).Order(StringComparer.Ordinal).ToArray(),
            ExpiresAtUnixSeconds: (now + Lifetime).ToUnixTimeSeconds(),
            Nonce: Base64UrlEncode(RandomNumberGenerator.GetBytes(18)));
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(ComputeSignature(secret, payloadBytes));
    }

    internal static ImmutableHashSet<string> GetDelegatedScopes(
        string toolName,
        ToolScopeRegistry.AuthorizationResult authorization,
        BearerPrincipal principal)
    {
        var scopes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (authorization.Primary.IsAny)
        {
            var selected = string.Equals(toolName, "query_snapshot", StringComparison.Ordinal)
                ? authorization.Primary.Any.Where(principal.HasScope).ToArray()
                : authorization.Primary.Any.Where(principal.HasScope).Take(1).ToArray();
            if (selected.Length == 0)
            {
                throw new InvalidOperationException("The authorized principal does not satisfy a primary scope.");
            }
            scopes.UnionWith(selected);
        }
        else
        {
            scopes.UnionWith(authorization.Primary.All);
        }

        scopes.UnionWith(authorization.AdditionalScopes);
        scopes.UnionWith(authorization.ModifierScopes);
        if (string.Equals(toolName, "query_snapshot", StringComparison.Ordinal) &&
            principal.HasExplicitScope(ToolInvocationScopeResolver.SensitiveParameterReadScope))
        {
            // The orchestrator cannot inspect a pod-local handle's kind. Method-parameter
            // handles require this literal modifier for every view, so carry it only when
            // the caller explicitly presented it and bind it to this exact invocation.
            scopes.Add(ToolInvocationScopeResolver.SensitiveParameterReadScope);
        }
        return scopes.ToImmutable();
    }

    private static string ComputeRequestHash(CallToolRequestParams request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("method", "tools/call");
            writer.WriteString("name", request.Name);
            writer.WritePropertyName("arguments");
            writer.WriteStartObject();
            if (request.Arguments is not null)
            {
                foreach (var pair in request.Arguments
                             .Where(static pair => !string.Equals(pair.Key, ArgumentName, StringComparison.Ordinal))
                             .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteCanonical(writer, pair.Value);
                }
            }
            writer.WriteEndObject();
            writer.WritePropertyName("meta");
            WriteCanonical(writer, JsonSerializer.SerializeToElement(request.Meta));
            writer.WritePropertyName("task");
            WriteCanonical(writer, JsonSerializer.SerializeToElement(request.Task));
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void PruneReplayCache(long nowUnixSeconds)
    {
        foreach (var entry in UsedNonces)
        {
            if (entry.Value < nowUnixSeconds)
            {
                UsedNonces.TryRemove(entry.Key, out _);
            }
        }
    }

    private static void AddToEnvelope(
        JsonNode node,
        ToolScopeRegistry registry,
        ToolScopeResolutionPolicies policies,
        BearerPrincipal caller,
        string secret,
        TimeProvider? timeProvider)
    {
        if (node is JsonArray batch)
        {
            foreach (var item in batch)
            {
                if (item is not null)
                {
                    AddToEnvelope(item, registry, policies, caller, secret, timeProvider);
                }
            }
            return;
        }

        if (node is not JsonObject envelope ||
            envelope["method"] is not JsonValue methodValue ||
            !methodValue.TryGetValue<string>(out var method) ||
            method != "tools/call" ||
            envelope["params"] is not JsonObject requestParams ||
            requestParams["name"]?.GetValue<string>() is not { Length: > 0 } toolName)
        {
            return;
        }

        var request = requestParams.Deserialize<CallToolRequestParams>() ??
            throw new JsonException("JSON-RPC tools/call params are malformed.");
        var argumentObject = requestParams["arguments"] as JsonObject ?? new JsonObject();
        requestParams["arguments"] = argumentObject;
        request.Arguments ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        request.Arguments.Remove(ArgumentName);
        argumentObject.Remove(ArgumentName);
        var authorization = registry.Authorize(
            toolName,
            request.Arguments,
            caller,
            proxyInvocation: true,
            policies: policies);
        if (!authorization.IsAllowed)
        {
            throw new InvalidOperationException(
                $"Cannot delegate unauthorized tool invocation '{toolName}'.");
        }

        argumentObject[ArgumentName] = CreateToken(
            request,
            authorization,
            caller,
            secret,
            timeProvider);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            bytes = Convert.FromBase64String(padded);
            return true;
        }

        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static byte[] ComputeSignature(string secret, byte[] payloadBytes)
    {
        var input = new byte[SignatureDomain.Length + payloadBytes.Length];
        SignatureDomain.CopyTo(input, 0);
        payloadBytes.CopyTo(input, SignatureDomain.Length);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), input);
    }

    private static CallToolRequestParams Clone(
        CallToolRequestParams request,
        IDictionary<string, JsonElement> arguments)
        => new()
        {
            Name = request.Name,
            Arguments = arguments,
            Meta = request.Meta,
            Task = request.Task,
        };

    private sealed record DelegationPayload(
        int Version,
        string Tool,
        string RequestHash,
        string[] Scopes,
        long ExpiresAtUnixSeconds,
        string Nonce);
}

internal sealed class ToolScopeDelegationKeyProvider
{
    public ToolScopeDelegationKeyProvider()
        : this(Environment.GetEnvironmentVariable(ToolScopeDelegation.EnvironmentVariableName))
    {
    }

    internal ToolScopeDelegationKeyProvider(string? key)
    {
        Key = string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public string? Key { get; }
}
