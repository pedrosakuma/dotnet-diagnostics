using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SweepUseCaseTests
{
    [Fact]
    public void FormatFailureText_PointsToFailuresWithinSweepProjection()
    {
        SweepUseCase.FormatFailureText(2).Should()
            .Be(" 2 collector(s) failed (see data.sweep.failures).");
    }
}
