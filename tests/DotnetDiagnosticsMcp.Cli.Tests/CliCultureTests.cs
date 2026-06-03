using System.Globalization;
using System.Text;
using DotnetDiagnosticsMcp.Cli;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

/// <summary>
/// Coverage for the locale-independence fix (issue #301 #2). Core builds human summaries/hints with
/// <c>{x:F1}</c>/<c>{x:N0}</c> interpolation, which honours the ambient culture (pt-BR renders
/// <c>cpu-usage=0,0%</c>). <see cref="CliHost"/> pins the invariant culture at the top of its run loop
/// so the CLI's textual output is reproducible regardless of the machine locale, matching the already
/// invariant <c>--json</c> path. Runs non-parallel because it mutates process-global culture state.
/// </summary>
[Collection(nameof(CliCultureTests))]
[CollectionDefinition(nameof(CliCultureTests), DisableParallelization = true)]
public sealed class CliCultureTests
{
    [Fact]
    public async Task RunAsync_PinsInvariantCulture_EvenWhenAmbientIsCommaDecimal()
    {
        var originalCurrent = CultureInfo.CurrentCulture;
        var originalDefault = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var ptBr = CultureInfo.GetCultureInfo("pt-BR");
            CultureInfo.CurrentCulture = ptBr;
            CultureInfo.DefaultThreadCurrentCulture = ptBr;
            // Sanity: pt-BR really does format with a comma, so the assertion below is meaningful.
            (1.5).ToString("F1", CultureInfo.CurrentCulture).Should().Be("1,5");

            var stdout = new StringWriter(new StringBuilder());
            var stderr = new StringWriter(new StringBuilder());
            var exit = await CliHost.RunAsync(new[] { "--help" }, stdout, stderr, CancellationToken.None);

            exit.Should().Be(0);
            // RunAsync pins the process-global default thread culture to invariant; that is what makes
            // Core's `{x:F1}` interpolation (which runs on threads spawned during the command) emit a
            // dot decimal regardless of the machine locale. (The caller's own CurrentCulture is restored
            // by the async ExecutionContext on resume, so it is not asserted here.)
            CultureInfo.DefaultThreadCurrentCulture.Should().Be(CultureInfo.InvariantCulture);
            CultureInfo.DefaultThreadCurrentUICulture.Should().Be(CultureInfo.InvariantCulture);
            (1.5).ToString("F1", CultureInfo.DefaultThreadCurrentCulture!).Should().Be("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCurrent;
            CultureInfo.DefaultThreadCurrentCulture = originalDefault;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUi;
        }
    }
}
