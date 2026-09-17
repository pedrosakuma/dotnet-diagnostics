using DotnetDiagnostics.Core.Networking;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NetworkingCaptureQualityTests
{
    [Theory]
    [InlineData("normal", 0L, 0L, false)]
    [InlineData("early", 0L, 0L, true)]
    [InlineData("source-failure", null, 0L, true)]
    [InlineData("unknown", null, 0L, true)]
    [InlineData("normal", null, 0L, true)]
    [InlineData("normal", 7L, 0L, true)]
    [InlineData("normal", 0L, 1L, true)]
    public void AcquisitionLimitations_DoNotDependOnCorrelation(
        string completion, long? lost, long errors, bool limited)
    {
        var quality = new NetworkingCaptureQuality(completion, lost, null, errors);
        Assert.Equal(limited, quality.HasLimitations);
        Assert.Contains($"completion={completion}", quality.Describe(), StringComparison.Ordinal);
        Assert.Contains(lost is null ? "lost=unknown" : $"lost={lost}", quality.Describe(), StringComparison.Ordinal);
        Assert.Equal(limited, quality.Describe().Contains("partial", StringComparison.Ordinal));
    }
}
