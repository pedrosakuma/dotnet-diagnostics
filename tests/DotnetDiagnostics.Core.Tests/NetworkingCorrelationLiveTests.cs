using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class NetworkingCorrelationLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public Task ConcurrentPaths_MatchMeasuredClientScope(string framework)
        => NetworkingPositiveAcceptance.RunAsync(framework, output);
}
