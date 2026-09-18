using System.Diagnostics.Tracing;

/// <summary>Opt-in, repository-owned temporal fixture; ordinary diagnostics never require it.</summary>
internal static class CrashGuardFixture
{
    private static readonly ManualResetEventSlim ReleaseExit = new();
    private static readonly TaskCompletionSource<bool> Terminating = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _started;

    internal static void Map(WebApplication app)
    {
        if (Environment.GetEnvironmentVariable("BADCODE_CRASH_GUARD_FIXTURE") != "1") return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is not InvalidOperationException exception ||
                !exception.Message.Contains("controlled crash fixture", StringComparison.Ordinal)) return;

            Terminating.TrySetResult(args.IsTerminating);
            // A lost test client must not leave a fatal foreground thread alive indefinitely.
            if (!ReleaseExit.Wait(TimeSpan.FromSeconds(45)))
                Console.Error.WriteLine("CrashGuard fixture release deadline expired.");
        };

        app.MapGet("/crash-guard-fixture/ready", () =>
        {
            FixtureEvents.Log.Ready();
            return Results.Ok();
        });
        app.MapGet("/crash-guard-fixture/crash", () =>
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return Results.Conflict();
            new Thread(static () => throw new InvalidOperationException(
                "BadCodeSample controlled crash fixture." + Environment.NewLine + Environment.StackTrace))
            {
                IsBackground = false,
                Name = "Controlled CrashGuard termination",
            }.Start();
            return Results.Accepted();
        });
        app.MapGet("/crash-guard-fixture/terminating", async (CancellationToken cancellationToken) =>
            Results.Ok(await Terminating.Task.WaitAsync(cancellationToken)));
        app.MapGet("/crash-guard-fixture/release", () =>
        {
            ReleaseExit.Set();
            return Results.Ok();
        });
    }

    [EventSource(Name = "DotnetDiagnostics.CrashGuardFixture")]
    private sealed class FixtureEvents : EventSource
    {
        internal static readonly FixtureEvents Log = new();
        [Event(1, Level = EventLevel.Informational)]
        public void Ready() => WriteEvent(1);
    }
}
