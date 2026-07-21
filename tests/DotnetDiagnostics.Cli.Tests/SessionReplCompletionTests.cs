using DotnetDiagnostics.Cli;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Unit tests for the pure candidate-selection logic behind the <c>session</c> REPL's tab-completion
/// (issue #657). Deliberately independent of PrettyPrompt/a real terminal — see
/// <see cref="SessionReplCompletion"/>'s remarks for why the split exists.
/// </summary>
public sealed class SessionReplCompletionTests
{
    [Fact]
    public void FirstWord_OffersCommandNamesAndReplBuiltins()
    {
        var candidates = SessionReplCompletion.GetCandidates([], "co", boundPid: null);

        candidates.Should().Contain("collect");
        candidates.Should().Contain("compare");
        candidates.Should().NotContain("dump"); // filtered by "co" prefix
    }

    [Fact]
    public void FirstWord_IncludesReplOnlyBuiltins()
    {
        var candidates = SessionReplCompletion.GetCandidates([], "ta", boundPid: null);

        candidates.Should().Contain("target");
    }

    [Fact]
    public void AfterCommandName_OffersThatCommandsFlagsAndGlobalOptions()
    {
        var candidates = SessionReplCompletion.GetCandidates(["collect"], "--", boundPid: null);

        candidates.Should().Contain("--kind");
        candidates.Should().Contain("--pid"); // global option
    }

    [Fact]
    public void AfterCollectKindFlag_OffersCollectKinds()
    {
        var candidates = SessionReplCompletion.GetCandidates(["collect", "--kind"], string.Empty, boundPid: null);

        candidates.Should().Contain("gc");
        candidates.Should().Contain("counters");
        candidates.Should().Contain("thread-snapshot");
    }

    [Fact]
    public void AfterGetBytesKindFlag_OffersByteKindsNotCollectKinds()
    {
        var candidates = SessionReplCompletion.GetCandidates(["get-bytes", "--kind"], string.Empty, boundPid: null);

        candidates.Should().Contain("module");
        candidates.Should().NotContain("gc");
    }

    [Fact]
    public void AfterSourceFlag_OnlyOffersHeapSourcesForInspectHeap()
    {
        var forInspectHeap = SessionReplCompletion.GetCandidates(["inspect-heap", "--source"], string.Empty, boundPid: null);
        forInspectHeap.Should().BeEquivalentTo(["dump", "gcdump", "live"]);

        var forOtherCommand = SessionReplCompletion.GetCandidates(["collect", "--source"], string.Empty, boundPid: null);
        forOtherCommand.Should().BeEmpty();
    }

    [Fact]
    public void AfterDumpTypeFlag_OffersDumpTypes()
    {
        var candidates = SessionReplCompletion.GetCandidates(["dump", "--dump-type"], "W", boundPid: null);

        candidates.Should().ContainSingle().Which.Should().Be("WithHeap");
    }

    [Fact]
    public void AfterDepthFlag_OffersDepthValues()
    {
        var candidates = SessionReplCompletion.GetCandidates(["inspect", "--depth"], string.Empty, boundPid: null);

        candidates.Should().BeEquivalentTo(["detail", "raw", "summary"]);
    }

    [Fact]
    public void AfterPidFlag_WithBoundTarget_OffersBoundPid()
    {
        var candidates = SessionReplCompletion.GetCandidates(["collect", "--pid"], string.Empty, boundPid: 4242);

        candidates.Should().ContainSingle().Which.Should().Be("4242");
    }

    [Fact]
    public void AfterPidFlag_WithNoBoundTarget_OffersNothing()
    {
        var candidates = SessionReplCompletion.GetCandidates(["collect", "-p"], string.Empty, boundPid: null);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void AfterHandleFlag_OffersNothing_NotFlagNames()
    {
        // --handle has no enum candidates and must never fall through to offering flag names as its value.
        var candidates = SessionReplCompletion.GetCandidates(["query", "--handle"], string.Empty, boundPid: null);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void AfterUnknownValueFlag_NeverOffersFlagNames()
    {
        var candidates = SessionReplCompletion.GetCandidates(["dump", "--out"], string.Empty, boundPid: null);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void PrefixFilter_IsCaseInsensitive()
    {
        var candidates = SessionReplCompletion.GetCandidates([], "COLL", boundPid: null);

        candidates.Should().Contain("collect");
    }

    [Fact]
    public void UnknownCommand_StillOffersGlobalOptionsForFlags()
    {
        var candidates = SessionReplCompletion.GetCandidates(["not-a-real-command"], "--", boundPid: null);

        candidates.Should().Contain("--json");
    }

    [Theory]
    [InlineData("collect --k", 11, 8, 3)]   // "--k" -> span covers both dashes + "k", not just "k"
    [InlineData("collect --kind", 14, 8, 6)] // full "--kind" typed
    [InlineData("inspect-h", 9, 0, 9)]      // hyphenated command name mid-word
    [InlineData("", 0, 0, 0)]               // empty buffer
    [InlineData("collect ", 8, 8, 0)]       // caret right after a space: empty span at caret
    public void GetReplacementSpan_TreatsDashAsTokenCharacter(string text, int caret, int expectedStart, int expectedLength)
    {
        var (start, length) = SessionReplCompletion.GetReplacementSpan(text, caret);

        start.Should().Be(expectedStart);
        length.Should().Be(expectedLength);
    }

    [Theory]
    [InlineData("-", 1, true)]           // start of line
    [InlineData("collect -", 9, true)]   // right after whitespace
    [InlineData("collect --", 10, true)] // second dash of a long flag
    [InlineData("collect gc", 10, false)] // last char is a letter, not a dash
    [InlineData("collect --kind gc", 17, false)] // mid-value, not starting a flag
    public void IsStartingFlagToken_DetectsDashStartPositions(string text, int caret, bool expected)
    {
        SessionReplCompletion.IsStartingFlagToken(text, caret).Should().Be(expected);
    }

    // --- Live-capture completion detection (issue #675) --------------------------------------------

    [Fact]
    public void IsLiveCaptureKindCompletion_CollectKindFlag_ReturnsTrue()
    {
        SessionReplCompletion.IsLiveCaptureKindCompletion(["collect", "--kind"]).Should().BeTrue();
    }

    [Fact]
    public void IsLiveCaptureKindCompletion_GetBytesKindFlag_ReturnsFalse()
    {
        // get-bytes --kind offers dump kinds too, which don't require a live process; only
        // collect --kind is unconditionally live-only.
        SessionReplCompletion.IsLiveCaptureKindCompletion(["get-bytes", "--kind"]).Should().BeFalse();
    }

    [Fact]
    public void IsLiveCaptureKindCompletion_CollectPidFlag_ReturnsFalse()
    {
        SessionReplCompletion.IsLiveCaptureKindCompletion(["collect", "--pid"]).Should().BeFalse();
    }

    [Fact]
    public void IsLiveCaptureKindCompletion_ExplicitPidOverridesBoundTarget_ReturnsFalse()
    {
        // collect --pid 5678 --kind <TAB> targets pid 5678 explicitly, overriding whatever the session's
        // bound target is — so it must NOT be flagged as live-only even though the last token is --kind
        // (code review, issue #675).
        SessionReplCompletion.IsLiveCaptureKindCompletion(["collect", "--pid", "5678", "--kind"]).Should().BeFalse();
        SessionReplCompletion.IsLiveCaptureKindCompletion(["collect", "-p", "5678", "--kind"]).Should().BeFalse();
    }

    [Fact]
    public void IsLiveCaptureKindCompletion_EmptyTokens_ReturnsFalse()
    {
        SessionReplCompletion.IsLiveCaptureKindCompletion([]).Should().BeFalse();
    }
}
