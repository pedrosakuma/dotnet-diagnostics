using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SweepUseCaseTests
{
    [Fact]
    public void FormatFailureText_RemainsHostPathNeutral()
    {
        SweepUseCase.FormatFailureText(2).Should()
            .Be(" 2 collector(s) failed.")
            .And.NotContain("data.", "Core is shared by hosts with different JSON envelopes");
    }
}
