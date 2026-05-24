using DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 / #204 — sanity coverage for <see cref="CompatibilityEnvelopeAssert"/>.
/// Verifies the helper accepts a matching pair and rejects a divergent one, plus that the
/// ignore-paths feature masks volatile fields (handle IDs, expiration timestamps) so
/// production sub-issues don't have to plumb deterministic clocks just to assert
/// compatibility.
/// </summary>
public sealed class CompatibilityEnvelopeAssertTests
{
    private sealed record Envelope(string Summary, int Value, string? Handle = null);

    [Fact]
    public async Task IdenticalEnvelopes_Pass()
    {
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync<Envelope, Envelope>(
            legacy: () => Task.FromResult(new Envelope("ok", 42)),
            successor: () => Task.FromResult(new Envelope("ok", 42)));
    }

    [Fact]
    public async Task DivergentEnvelopes_FailWithUsefulMessage()
    {
        Func<Task> act = () => CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync<Envelope, Envelope>(
            legacy: () => Task.FromResult(new Envelope("ok", 42)),
            successor: () => Task.FromResult(new Envelope("ok", 99)));

        var ex = await act.Should().ThrowAsync<AssertionFailedException>();
        ex.Which.Message.Should().Contain("--- legacy ---").And.Contain("--- successor ---");
        ex.Which.Message.Should().Contain("42").And.Contain("99");
    }

    [Fact]
    public async Task IgnorePaths_MaskVolatileFields()
    {
        // Handle IDs and expiration moments diverge between two real tool calls — the
        // helper must let sub-issues mask them and still pass when the rest matches.
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync<Envelope, Envelope>(
            legacy: () => Task.FromResult(new Envelope("ok", 42, Handle: "abc")),
            successor: () => Task.FromResult(new Envelope("ok", 42, Handle: "xyz")),
            ignore: CompatibilityEnvelopeAssert.CompatibilityIgnore.Paths("handle"));
    }
}
