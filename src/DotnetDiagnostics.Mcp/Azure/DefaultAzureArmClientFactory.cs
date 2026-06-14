using System;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;

namespace DotnetDiagnostics.Mcp.Azure;

/// <summary>
/// Default <see cref="IAzureArmClientFactory"/> implementation. Constructs a single
/// <see cref="DefaultAzureCredential"/> at startup and reuses it across every
/// <see cref="Create(string)"/> call.
/// </summary>
internal sealed class DefaultAzureArmClientFactory : IAzureArmClientFactory
{
    private readonly TokenCredential _credential;

    public DefaultAzureArmClientFactory()
    {
        _credential = new DefaultAzureCredential();
    }

    public ArmClient Create(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Subscription id must be a non-empty value.", nameof(subscriptionId));
        }

        return new ArmClient(_credential, subscriptionId);
    }
}
