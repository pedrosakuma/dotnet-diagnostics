namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>Rejects cleartext requests after trusted forwarded-header processing.
/// Loopback and the unauthenticated health probe remain available over local HTTP.</summary>
internal sealed class HttpsTransportEnforcementMiddleware
{
    private const string HttpsRequiredEnvelope =
        "{\"error\":{\"kind\":\"https_required\",\"message\":\"HTTPS is required for non-loopback MCP traffic\"}}";

    private readonly RequestDelegate _next;
    private readonly ILogger<HttpsTransportEnforcementMiddleware> _logger;

    public HttpsTransportEnforcementMiddleware(
        RequestDelegate next,
        ILogger<HttpsTransportEnforcementMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.IsHttps ||
            TransportSecurityPolicy.IsLoopback(context.Connection.RemoteIpAddress) ||
            context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "Rejected cleartext HTTP request to {Path} from non-loopback peer {RemoteIp}. " +
            "A trusted TLS proxy must supply X-Forwarded-Proto=https.",
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(HttpsRequiredEnvelope).ConfigureAwait(false);
    }
}
