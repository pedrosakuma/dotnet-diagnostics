using DotnetDiagnostics.Mcp.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Immutable;
using System.Text.Json;

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

    internal static bool TryGetMatchingProviderSchemes(
        string jwt,
        OidcJwtAuthOptions authOptions,
        out ImmutableArray<string> schemes)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        ArgumentNullException.ThrowIfNull(authOptions);

        schemes = ImmutableArray<string>.Empty;
        if (!TryReadIssuerAndAudiences(jwt, out var issuer, out var audiences))
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var provider in authOptions.Providers)
        {
            if (!string.Equals(provider.Issuer, issuer, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var audience in audiences)
            {
                if (!string.Equals(provider.Audience, audience, StringComparison.Ordinal))
                {
                    continue;
                }

                builder.Add(provider.SchemeName);
                break;
            }
        }

        schemes = builder.ToImmutable();
        return true;
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

    private static bool TryReadIssuerAndAudiences(
        string jwt,
        out string? issuer,
        out ImmutableArray<string> audiences)
    {
        issuer = null;
        audiences = ImmutableArray<string>.Empty;

        var firstDot = jwt.IndexOf('.');
        if (firstDot <= 0)
        {
            return false;
        }

        var secondDot = jwt.IndexOf('.', firstDot + 1);
        if (secondDot <= firstDot + 1)
        {
            return false;
        }

        try
        {
            var payloadBytes = Base64UrlEncoder.DecodeBytes(jwt.Substring(firstDot + 1, secondDot - firstDot - 1));
            var reader = new Utf8JsonReader(payloadBytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            ImmutableArray<string>.Builder? audienceBuilder = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var propertyName = reader.GetString();
                if (!reader.Read())
                {
                    return false;
                }

                if (string.Equals(propertyName, "iss", StringComparison.Ordinal))
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    issuer = reader.GetString();
                    continue;
                }

                if (string.Equals(propertyName, "aud", StringComparison.Ordinal))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        audienceBuilder ??= ImmutableArray.CreateBuilder<string>();
                        audienceBuilder.Add(reader.GetString()!);
                        continue;
                    }

                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        return false;
                    }

                    audienceBuilder ??= ImmutableArray.CreateBuilder<string>();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            break;
                        }

                        if (reader.TokenType != JsonTokenType.String)
                        {
                            return false;
                        }

                        audienceBuilder.Add(reader.GetString()!);
                    }

                    continue;
                }

                reader.Skip();
            }

            audiences = audienceBuilder?.ToImmutable() ?? ImmutableArray<string>.Empty;
            return !string.IsNullOrWhiteSpace(issuer) && !audiences.IsDefaultOrEmpty;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
