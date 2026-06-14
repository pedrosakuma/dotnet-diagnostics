using DotnetDiagnostics.Mcp.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DotnetDiagnostics.Mcp.Auth;

internal static class OidcJwtAuthExtensions
{
    public const string JwtScheme = "OidcJwtBearer";

    public static OidcJwtAuthOptions AddOidcJwtAuth(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var authOptions = OidcJwtAuthOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(authOptions);

        var authentication = builder.Services.AddAuthentication();
        if (!authOptions.IsEnabled)
        {
            return authOptions;
        }

        foreach (var provider in authOptions.Providers)
        {
            authentication.AddJwtBearer(provider.SchemeName, options => ConfigureJwtBearer(options, provider));
        }

        return authOptions;
    }

    private static void ConfigureJwtBearer(JwtBearerOptions options, OidcJwtProvider provider)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = !provider.MetadataAddress.IsLoopback;
        options.SaveToken = false;
        options.RefreshOnIssuerKeyNotFound = true;
        options.MetadataAddress = provider.MetadataAddress.AbsoluteUri;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = provider.Issuer,
            ValidateAudience = true,
            ValidAudience = provider.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };

        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever
            {
                RequireHttps = options.RequireHttpsMetadata,
            })
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(24),
            RefreshInterval = TimeSpan.FromMinutes(5),
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                string? failureMessage = null;
                if (context.Principal is null ||
                    !provider.TryCreatePrincipal(context.Principal, out var bearerPrincipal, out failureMessage))
                {
                    context.Fail(failureMessage ?? "OIDC/JWT principal mapping failed.");
                    return Task.CompletedTask;
                }

                context.HttpContext.SetBearerPrincipal(bearerPrincipal!);
                return Task.CompletedTask;
            },
        };
    }
}
