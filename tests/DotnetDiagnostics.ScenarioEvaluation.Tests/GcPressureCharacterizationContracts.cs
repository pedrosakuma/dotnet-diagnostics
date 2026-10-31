using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class GcPressureLiveBounds
{
    public const int WindowCount = 6;
    public const int WindowSeconds = 3;
    public const int NormalGcDetailCap = 512;
    public const int LossGcDetailCap = 1;
}

public sealed record GcPressureReplayFixture(
    int SchemaVersion,
    IReadOnlyList<GcPressureReplayScenario> Scenarios);

public sealed record GcPressureReplayScenario(
    string Id,
    GcPressureCapture Capture,
    GcPressureNextAction ExpectedAction,
    bool? ExpectedPostLoadStabilized,
    int MinimumPressureWindows);
