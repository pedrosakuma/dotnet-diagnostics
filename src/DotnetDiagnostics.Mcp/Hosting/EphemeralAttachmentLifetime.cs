using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Hosting;

internal sealed class EphemeralAttachmentLifetime
{
    internal const string ExpiryEnvironmentVariableName = "MCP_EPHEMERAL_ATTACHMENT_EXPIRES_AT";
    internal const string RevokePath = "/internal/attachment/revoke";

    private readonly TimeProvider _timeProvider;
    private int _revoked;

    public EphemeralAttachmentLifetime(IConfiguration configuration, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        var raw = configuration[ExpiryEnvironmentVariableName];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!DateTimeOffset.TryParseExact(
                raw,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new InvalidOperationException(
                $"{ExpiryEnvironmentVariableName} must be an ISO-8601 round-trip timestamp.");
        }

        ExpiresAt = expiresAt;
    }

    public DateTimeOffset? ExpiresAt { get; }

    public bool IsEphemeral => ExpiresAt.HasValue;

    public bool IsActive =>
        Volatile.Read(ref _revoked) == 0 &&
        (!ExpiresAt.HasValue || _timeProvider.GetUtcNow() < ExpiresAt.Value);

    public bool Revoke() => Interlocked.Exchange(ref _revoked, 1) == 0;
}

internal sealed class EphemeralAttachmentExpiryService : BackgroundService
{
    private readonly EphemeralAttachmentLifetime _lifetime;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EphemeralAttachmentExpiryService> _logger;

    public EphemeralAttachmentExpiryService(
        EphemeralAttachmentLifetime lifetime,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        ILogger<EphemeralAttachmentExpiryService> logger)
    {
        _lifetime = lifetime;
        _applicationLifetime = applicationLifetime;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_lifetime.ExpiresAt is not { } expiresAt)
        {
            return;
        }

        var delay = expiresAt - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
        }

        if (_lifetime.Revoke())
        {
            _logger.LogInformation("Ephemeral attachment credentials expired; stopping the pod-local diagnostics server.");
        }
        _applicationLifetime.StopApplication();
    }
}

internal static class EphemeralAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapEphemeralAttachmentControl(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(EphemeralAttachmentLifetime.RevokePath, static (HttpContext context) =>
        {
            var lifetime = context.RequestServices.GetRequiredService<EphemeralAttachmentLifetime>();
            var delegation = context.RequestServices.GetRequiredService<ToolScopeDelegationKeyProvider>();
            var principal = context.GetBearerPrincipal();
            if (!lifetime.IsEphemeral || string.IsNullOrWhiteSpace(delegation.Key))
            {
                return Results.NotFound();
            }
            if (principal is null || !principal.HasScope(BearerPrincipal.RootScope))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            lifetime.Revoke();
            var applicationLifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
            context.Response.OnCompleted(() =>
            {
                applicationLifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.NoContent();
        });
        return app;
    }
}
