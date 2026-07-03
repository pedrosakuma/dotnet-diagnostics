using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the diagnosis-agnostic exception signal groupings (#524): type concentration (both the
/// standard exception stream and the crash-guard stream) and the throw-site roll-up (crash-guard only,
/// where managed stacks are resolved). These describe <i>where</i> exceptions concentrate — by type
/// and by throw-site frame — never <i>why</i> they are thrown.
/// </summary>
public sealed class ExceptionSignalsTests
{
    private static ExceptionSnapshot Snapshot(int total, params (string Type, int Count)[] byType) => new(
        ProcessId: 4242,
        StartedAt: DateTimeOffset.UtcNow,
        Duration: TimeSpan.FromSeconds(10),
        TotalExceptions: total,
        ByType: byType.Select(t => new ExceptionCount(t.Type, t.Count)).ToArray(),
        Recent: Array.Empty<ManagedExceptionEvent>());

    // ---- by-type concentration (both collectors) -----------------------------------------------

    [Fact]
    public void ByType_EmitsConcentration_WhenOneTypeDominates()
    {
        var snap = Snapshot(20, ("System.FormatException", 18), ("System.InvalidOperationException", 2));

        var signals = ExceptionSignals.Detect(snap, "handle-exc");

        var byType = signals.Should().ContainSingle(s => s.Signal == "exceptions.by-type").Subject;
        byType.Salience.Should().BeApproximately(0.9, 0.001);
        byType.Summary.Should().Contain("System.FormatException");
        byType.Buckets[0].Key.Should().Be("System.FormatException");
        byType.Buckets[0].Magnitude.Should().Be(18);
        byType.Buckets[0].Handle.Should().Be("handle-exc");
        byType.NextAction!.SuggestedArguments!["view"].Should().Be("byType");
        // Standard stream has no stacks -> no throw-site roll-up.
        signals.Should().NotContain(s => s.Signal == "exceptions.by-throw-site");
    }

    [Fact]
    public void ByType_EmitsNothing_WhenSpreadOut()
    {
        var snap = Snapshot(30,
            ("System.FormatException", 10),
            ("System.InvalidOperationException", 10),
            ("System.NullReferenceException", 10));

        ExceptionSignals.Detect(snap, "h").Should().BeEmpty();
    }

    [Fact]
    public void ByType_EmitsNothing_WhenTooFewExceptions()
    {
        var snap = Snapshot(2, ("System.FormatException", 2));

        ExceptionSignals.Detect(snap, "h").Should().BeEmpty();
    }

    // ---- throw-site roll-up (crash-guard only) -------------------------------------------------

    private static CrashGuardExceptionEvent Event(string type, params string[] stack) => new(
        Timestamp: DateTimeOffset.UtcNow,
        ExceptionType: type,
        ExceptionMessage: string.Empty,
        ExceptionHResult: "0x0",
        ThreadId: 1,
        EventName: "ExceptionThrown_V1",
        IsUnhandled: false,
        ManagedStack: stack);

    private static CrashGuardSnapshot CrashGuard(IReadOnlyList<CrashGuardExceptionEvent> events)
    {
        var byType = events
            .GroupBy(e => e.ExceptionType, StringComparer.Ordinal)
            .Select(g => new ExceptionCount(g.Key, g.Count()))
            .ToArray();
        return new CrashGuardSnapshot(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            ProcessExited: false,
            ExitCode: null,
            UnhandledExceptionObserved: false,
            TotalExceptions: events.Count,
            ByType: byType,
            Exceptions: events,
            FinalException: null,
            Notes: Array.Empty<string>());
    }

    [Fact]
    public void ByThrowSite_EmitsRollup_WhenOneSiteDominates()
    {
        const string site = "MyApp.Parsing.Parse(System.String)";
        var events = new[]
        {
            Event("System.FormatException", site, "MyApp.Handler()"),
            Event("System.FormatException", site, "MyApp.Handler()"),
            Event("System.FormatException", site, "MyApp.Handler()"),
            Event("System.InvalidOperationException", "MyApp.Other.Do()"),
        };

        var signals = ExceptionSignals.Detect(CrashGuard(events), "handle-cg");

        var bySite = signals.Should().ContainSingle(s => s.Signal == "exceptions.by-throw-site").Subject;
        bySite.Buckets[0].Key.Should().Be($"System.FormatException @ {site}");
        bySite.Buckets[0].Magnitude.Should().Be(3);
        bySite.Buckets[0].Handle.Should().Be("handle-cg");
        bySite.Salience.Should().BeApproximately(0.75, 0.001);
        bySite.NextAction!.SuggestedArguments!["view"].Should().Be("exceptions");
        // by-type drills the crash-guard 'exceptions' view (byType is not a crash-guard view).
        var byType = signals.Should().ContainSingle(s => s.Signal == "exceptions.by-type").Subject;
        byType.NextAction!.SuggestedArguments!["view"].Should().Be("exceptions");
    }

    [Fact]
    public void ByThrowSite_EmitsNothing_WhenNoStacksResolved()
    {
        var events = new[]
        {
            Event("System.FormatException"),
            Event("System.FormatException"),
            Event("System.FormatException"),
        };

        var signals = ExceptionSignals.Detect(CrashGuard(events), "h");

        signals.Should().NotContain(s => s.Signal == "exceptions.by-throw-site");
        // by-type still fires off the exact counts even without stacks.
        signals.Should().Contain(s => s.Signal == "exceptions.by-type");
    }
}
