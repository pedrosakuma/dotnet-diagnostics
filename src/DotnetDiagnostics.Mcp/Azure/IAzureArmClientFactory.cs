using Azure.ResourceManager;

namespace DotnetDiagnostics.Mcp.Azure;

/// <summary>
/// Foundation seam for the Azure discovery effort (issue #231, parent #230). Returns
/// an <see cref="ArmClient"/> scoped to a single subscription, reusing a single
/// long-lived credential built at startup.
/// </summary>
/// <remarks>
/// <para>
/// The v1 implementation uses <c>Azure.Identity.DefaultAzureCredential</c> with
/// default options — the standard discovery chain (env vars → workload identity →
/// managed identity → Azure CLI → Visual Studio → interactive browser). This keeps
/// the foundation PR free of credential-policy decisions; custom chain configuration
/// will land alongside the consumers in #233 / #234 if needed.
/// </para>
/// <para>
/// Callers MUST NOT cache the returned <see cref="ArmClient"/> across subscriptions;
/// each subscription scope is a distinct client instance.
/// </para>
/// </remarks>
public interface IAzureArmClientFactory
{
    /// <summary>
    /// Builds an <see cref="ArmClient"/> rooted at the given subscription id. Throws
    /// <see cref="System.ArgumentException"/> when <paramref name="subscriptionId"/>
    /// is null, empty, or whitespace.
    /// </summary>
    /// <param name="subscriptionId">Azure subscription GUID (string form).</param>
    ArmClient Create(string subscriptionId);
}
