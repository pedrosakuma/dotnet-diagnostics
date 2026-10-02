using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit and integration tests for the Windows ETW NativeAOT CPU sampler.
/// Integration tests require Windows with administrative elevation and are skipped otherwise.
/// </summary>
[Collection("LiveProcess")]
public class EtwNativeAotCpuSamplerTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, this test is not meaningful — skip.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        sampler.IsAvailable().Should().BeFalse("ETW is a Windows-only technology");
    }

    [Fact]
    public void IsAvailable_RespectsElevation_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Not on Windows — skip.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        var isElevated = TraceEventSession.IsElevated() == true;
        sampler.IsAvailable().Should().Be(isElevated,
            "IsAvailable should match administrative elevation status");
    }

    [Fact]
    public async Task SampleAsync_ThrowsOnInvalidDuration()
    {
        var sampler = new EtwNativeAotCpuSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public async Task SampleAsync_ThrowsOnInvalidTopN()
    {
        var sampler = new EtwNativeAotCpuSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1), topN: 0));
    }

    [Fact]
    public async Task SampleAsync_ThrowsWhenNotAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TraceEventSession.IsElevated() == true)
        {
            // Elevated on Windows — this test path does not apply.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1)));

        ex.Message.Should().Contain("administrative elevation",
            "error message should mention elevation requirement");
    }

    [Fact]
    public async Task SampleAsync_RespectsCancellation()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TraceEventSession.IsElevated() != true)
        {
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Should cancel promptly rather than hang for the full duration.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sampler.SampleAsync(Environment.ProcessId, TimeSpan.FromSeconds(30), cancellationToken: cts.Token));
    }

    [Fact]
    public void SymbolSource_PdbResolved_ExistsInEnum()
    {
        // Validates that the PdbResolved enum value is available for the ETW path.
        var source = NativeAotSymbolDemangler.SymbolSource.PdbResolved;
        source.Should().NotBe(NativeAotSymbolDemangler.SymbolSource.ElfDemangled,
            "Windows ETW path should use PdbResolved, not ElfDemangled");
        source.Should().NotBe(NativeAotSymbolDemangler.SymbolSource.Unknown);
    }

    [Fact]
    public async Task ErrorMessage_MentionsAdminOrPrivilege()
    {
        // Verifies the error message provides actionable remediation.
        var sampler = new EtwNativeAotCpuSampler();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TraceEventSession.IsElevated() == true)
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1)));
        ex.Message.Should().ContainAny("Administrator", "elevation", "SeSystemProfilePrivilege");
    }

    /// <summary>
    /// Live integration test: captures CPU samples from the current process (self-profiling).
    /// Requires Windows + admin. The current process is CoreCLR; CLR method-load and
    /// rundown events must retain the known managed workload name independently of DIA.
    /// Full PDB symbol resolution is validated against NativeAOT targets where symbols
    /// are statically compiled into the binary.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task SampleAsync_CapturesFromCurrentProcess_WhenElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TraceEventSession.IsElevated() != true)
        {
            // Skip: not Windows or not elevated.
            return;
        }

        var captureStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = new EtwNativeAotCpuSampler(new EtwCaptureStartedLogger(captureStarted));
        var pid = Environment.ProcessId;

        using var cts = new CancellationTokenSource();
        uint diagHotTid = 0;
        var loadTask = Task.Run(async () =>
        {
            await captureStarted.Task.WaitAsync(cts.Token);
            diagHotTid = DiagGetCurrentThreadId();
            BurnManagedCpu(cts.Token);
        }, cts.Token);

        try
        {
            var sampleTask = sampler.SampleAsync(
                pid,
                TimeSpan.FromSeconds(7),
                topN: 50,
                cancellationToken: cts.Token);
            if (await Task.WhenAny(captureStarted.Task, sampleTask) == sampleTask)
            {
                await sampleTask;
                throw new InvalidOperationException("ETW capture completed before its session-start signal.");
            }

            await captureStarted.Task;
            var result = await sampleTask;

            result.Should().NotBeNull();
            result.Summary.ProcessId.Should().Be(pid);
            result.Summary.TotalSamples.Should().BeGreaterThan(0,
                "should have captured at least one CPU sample");
            result.Summary.TopHotspots.Should().NotBeEmpty(
                "should have identified at least one hotspot");
            var nodes = new Stack<CallTreeNode>();
            nodes.Push(result.Artifact.Root);
            var namedWorkloadSamples = 0L;
            var testModuleRawSamples = 0L;
            var modulelessRawSamples = 0L;
            var namedFrames = new Dictionary<string, long>(StringComparer.Ordinal);
            while (nodes.TryPop(out var node))
            {
                if (node.Frame.Method.Contains(nameof(BurnManagedCpu), StringComparison.Ordinal))
                {
                    namedWorkloadSamples += node.InclusiveSamples;
                }
                if (node.Frame.Method.StartsWith("0x", StringComparison.Ordinal)
                    && node.Frame.Module.Contains("DotnetDiagnostics.Core.Tests", StringComparison.OrdinalIgnoreCase))
                {
                    testModuleRawSamples += node.ExclusiveSamples;
                }
                if (node.Frame.Method.StartsWith("[0x", StringComparison.Ordinal))
                {
                    modulelessRawSamples += node.ExclusiveSamples;
                }
                if (!node.Frame.Method.StartsWith("0x", StringComparison.Ordinal))
                {
                    namedFrames[node.Frame.Method] = namedFrames.GetValueOrDefault(node.Frame.Method) + node.InclusiveSamples;
                }
                foreach (var child in node.Children)
                {
                    nodes.Push(child);
                }
            }
            namedWorkloadSamples.Should().BeGreaterThan(0,
                "the workload starts after ETW enables its CLR providers, so its managed name must be resolved independently of native PDB/DIA validation. " +
                $"Total samples: {result.Summary.TotalSamples}; unnamed test-module leaf samples: {testModuleRawSamples}; moduleless raw leaf samples: {modulelessRawSamples}; named frames: " +
                string.Join(", ", namedFrames.OrderByDescending(pair => pair.Value).Take(15)
                    .Select(pair => $"{pair.Key}={pair.Value}")) + ". " +
                string.Join(" ", result.Summary.Notes));
            result.Summary.Notes.Should().Contain(note => note.Contains("CLR JIT/loader/rundown", StringComparison.Ordinal));

            result.Artifact.Root.Should().NotBeNull();
            result.Artifact.Root.Children.Should().NotBeEmpty(
                "call tree should have at least one child node");

            result.Artifact.SymbolSource.Should().Be(NativeAotSymbolDemangler.SymbolSource.PdbResolved);
            result.Summary.SymbolSource.Should().Be(result.Artifact.SymbolSource,
                "summary consumers must receive the same symbol provenance as drilldown consumers");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("ETW_DIAG_KEEP_DIR") is { Length: > 0 } probeDir && diagHotTid != 0)
            {
                // Post-capture only: the ETL is already closed, so this cannot perturb it.
                using var probeCts = new CancellationTokenSource();
                var ripPath = Path.Combine(probeDir, $"hot-rip-{pid}.txt");
                DiagWatchHotThread(diagHotTid, ripPath, probeCts.Token, iterations: 6);
                DiagIntervene(diagHotTid, ripPath, probeDir);
            }
            cts.Cancel();
            try { await loadTask; } catch (OperationCanceledException) { }
            if (Environment.GetEnvironmentVariable("ETW_DIAG_KEEP_DIR") is { Length: > 0 } diagDir)
            {
                File.AppendAllText(Path.Combine(diagDir, "hot-threads.txt"), $"pid={pid} hotTid={diagHotTid}{Environment.NewLine}");
            }
        }
    }

    private sealed class EtwCaptureStartedLogger(
        TaskCompletionSource<bool> captureStarted) : ILogger<EtwNativeAotCpuSampler>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug
                && formatter(state, exception).Contains("started for pid", StringComparison.Ordinal))
            {
                captureStarted.TrySetResult(true);
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint DiagGetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong pc, out ulong imageBase, IntPtr history);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
    [DllImport("ntdll.dll")]
    private static extern IntPtr RtlGetFunctionTableListHead();
    [DllImport("ntdll.dll")]
    private static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, IntPtr[] frames, IntPtr hash);

    [DllImport("ntdll.dll")]
    private static extern byte RtlDeleteFunctionTable(IntPtr functionTable);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out ulong buffer, IntPtr length);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static unsafe Dictionary<int, string> DiagThreadInfo(uint tid)
    {
        var r = new Dictionary<int, string>();
        var h = OpenThread(0x0040 | 0x0800 | 0x0008, false, tid);
        var buf = stackalloc byte[256];
        for (var c = 0; c < 64; c++)
        {
            if (c is 0 or 4 or 5 or 6 or 7) continue; // TEB-address/time/priority-dynamic classes are noise
            new Span<byte>(buf, 256).Clear();
            var st = NtQueryInformationThread(h, c, (IntPtr)buf, 256, out var ret);
            r[c] = st == 0 ? $"len={ret} {Convert.ToHexString(new ReadOnlySpan<byte>(buf, Math.Min(ret, 64)))}" : $"st=0x{st:x}";
        }
        CloseHandle(h);
        var tbi = stackalloc byte[48];
        h = OpenThread(0x0040, false, tid);
        NtQueryInformationThread(h, 0, (IntPtr)tbi, 48, out _);
        CloseHandle(h);
        var teb = *(ulong*)(tbi + 8);
        if (teb != 0)
        {
            r[1000] = $"SameTebFlags=0x{*(ushort*)(teb + 0x17EE):x} CrossTebFlags=0x{*(ushort*)(teb + 0x17EC):x} HardErrorMode=0x{*(uint*)(teb + 0x16B0):x} InstrumentationCallbackDisabled={*(byte*)(teb + 0x2D0)} CurrentIdealProcessor=0x{*(uint*)(teb + 0x1744):x}";
            r[1001] = "TEB[0x1700..0x1838] " + Convert.ToHexString(new ReadOnlySpan<byte>((void*)(teb + 0x1700), 0x138));
        }
        return r;
    }

    private static unsafe string DiagTeb(uint tid, ulong rsp)
    {
        var h = OpenThread(0x0040 | 0x0800, false, tid); // QUERY_INFORMATION | QUERY_LIMITED
        var tbi = stackalloc byte[48];
        var st = NtQueryInformationThread(h, 0, (IntPtr)tbi, 48, out _);
        CloseHandle(h);
        var teb = *(ulong*)(tbi + 8);
        if (st != 0 || teb == 0) return $"tid={tid} NtQueryInformationThread=0x{st:x}";
        ulong stackBase = *(ulong*)(teb + 0x8), stackLimit = *(ulong*)(teb + 0x10), dealloc = *(ulong*)(teb + 0x1478);
        var inRange = rsp == 0 ? "n/a" : (rsp >= stackLimit && rsp < stackBase).ToString();
        return $"tid={tid} teb=0x{teb:x} StackBase=0x{stackBase:x} StackLimit=0x{stackLimit:x} DeallocationStack=0x{dealloc:x} committed=0x{stackBase - stackLimit:x} reserved=0x{stackBase - dealloc:x} rsp=0x{rsp:x} rspInRange={inRange} SameTebSelf={(tid == DiagGetCurrentThreadId())}";
    }

    // DIAG ONLY: in the same (possibly broken) process, re-capture the hot thread (C0), then delete
    // the overlapping callback (type=2) dynamic function table that covers the hot RIP and re-capture (C1).
    private static unsafe void DiagIntervene(uint tid, string path, string dir)
    {
        var lines = new List<string>();
        try
        {
            var h = OpenThread(0x0008 | 0x0002 | 0x0040, false, tid);
            var ctxMem = (IntPtr)NativeMemory.AlignedAlloc(1232, 16);
            new Span<byte>((void*)ctxMem, 1232).Clear();
            *(uint*)(ctxMem + 0x30) = 0x00100001;
            SuspendThread(h);
            GetThreadContext(h, ctxMem);
            _ = ResumeThread(h);
            var rip = *(ulong*)(ctxMem + 0xF8);
            var rsp = *(ulong*)(ctxMem + 0x98);
            NativeMemory.AlignedFree((void*)ctxMem);
            CloseHandle(h);

            GetProcessMitigationPolicy(GetCurrentProcess(), 15, out var cet, (IntPtr)4); // ProcessUserShadowStackPolicy
            lines.Add($"CET user shadow stack policy flags=0x{cet:x}");
            lines.Add("hot TEB: " + DiagTeb(tid, rsp));
            lines.Add($"C0 (hot thread, before intervention): {DiagCapture(tid, dir, "c0")}");

            // Same JIT'd BurnManagedCpu code on a brand-new dedicated thread.
            using var freshCts = new CancellationTokenSource();
            uint freshTid = 0;
            using var freshReady = new ManualResetEventSlim();
            var fresh = new Thread(() => { freshTid = DiagGetCurrentThreadId(); freshReady.Set(); BurnManagedCpu(freshCts.Token); }) { IsBackground = true };
            fresh.Start();
            freshReady.Wait();
            Thread.Sleep(200);
            lines.Add("fresh TEB: " + DiagTeb(freshTid, 0));
            var hi = DiagThreadInfo(tid);
            var fi = DiagThreadInfo(freshTid);
            foreach (var k in hi.Keys.Union(fi.Keys).OrderBy(k => k))
            {
                hi.TryGetValue(k, out var a); fi.TryGetValue(k, out var b);
                lines.Add($"{(a == b ? "same" : "DIFF")} class={k} hot=[{a}] fresh=[{b}]");
            }
            lines.Add($"C2 (fresh thread, same code): {DiagCapture(freshTid, dir, "c2")}");
            lines.Add($"C3 (hot thread again, concurrently burning): {DiagCapture(tid, dir, "c3")}");
            freshCts.Cancel();
            fresh.Join();

            // C4: capture while periodically suspending/resuming the hot thread (delivers kernel APCs).
            using (var pokeCts = new CancellationTokenSource())
            {
                var poker = new Thread(() =>
                {
                    var ph = OpenThread(0x0002, false, tid);
                    while (!pokeCts.IsCancellationRequested) { _ = SuspendThread(ph); _ = ResumeThread(ph); Thread.Sleep(250); }
                    CloseHandle(ph);
                }) { IsBackground = true };
                poker.Start();
                lines.Add($"C4 (hot thread, suspend/resume every 250ms during capture): {DiagCapture(tid, dir, "c4")}");
                pokeCts.Cancel();
                poker.Join();
            }
            lines.Add($"C5 (hot thread, after C4 pokes): {DiagCapture(tid, dir, "c5")}");
            var head = RtlGetFunctionTableListHead();
            var callbacks = new List<IntPtr>();
            for (var e = *(IntPtr*)head; e != head; e = *(IntPtr*)e)
            {
                if (*(int*)(e + 80) == 2 && rip >= *(ulong*)(e + 32) && rip < *(ulong*)(e + 40))
                {
                    callbacks.Add(*(IntPtr*)(e + 16));
                }
            }
            foreach (var id in callbacks)
            {
                lines.Add($"RtlDeleteFunctionTable(callback id=0x{(long)id:x}) => {RtlDeleteFunctionTable(id)}");
            }
            lines.Add($"C1 (after deleting {callbacks.Count} callback table(s)): {DiagCapture(tid, dir, "c1")}");
            lines.Add(DiagPoolSurvey(dir));
        }
        catch (Exception ex) { lines.Add("intervene exception: " + ex); }
        File.AppendAllLines(path, lines);
    }

    // Burn on many existing thread-pool threads at once and report, per thread, whether user stacks are walked.
    private static string DiagPoolSurvey(string dir)
    {
        using var stop = new CancellationTokenSource();
        var tids = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var n = Math.Max(4, Environment.ProcessorCount * 2);
        for (var i = 0; i < n; i++)
        {
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ => { tids[(int)DiagGetCurrentThreadId()] = 1; BurnManagedCpu(stop.Token); }, null);
        }
        Thread.Sleep(1500);
        var etl = Path.Combine(dir, $"intervene-pool-{Environment.ProcessId}.etl");
        using (var s = new TraceEventSession($"diag-intervene-pool-{Environment.ProcessId}", etl) { StopOnDispose = true })
        {
            s.EnableKernelProvider(Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.Profile,
                Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.Profile);
            Thread.Sleep(3000);
        }
        stop.Cancel();
        var samples = new Dictionary<int, int>(); var withUser = new Dictionary<int, int>();
        using (var src = new Microsoft.Diagnostics.Tracing.ETWTraceEventSource(etl))
        {
            src.Kernel.PerfInfoSample += e => { if (tids.ContainsKey(e.ThreadID)) samples[e.ThreadID] = samples.GetValueOrDefault(e.ThreadID) + 1; };
            src.Kernel.StackWalkStack += e =>
            {
                if (!tids.ContainsKey(e.ThreadID)) return;
                for (var i = 0; i < e.FrameCount; i++)
                {
                    if (e.InstructionPointer(i) < 0x0000800000000000UL) { withUser[e.ThreadID] = withUser.GetValueOrDefault(e.ThreadID) + 1; break; }
                }
            };
            src.Process();
        }
        var rows = tids.Keys.OrderBy(t => t).Select(t => $"{t}:{samples.GetValueOrDefault(t)}/{withUser.GetValueOrDefault(t)}");
        var broken = tids.Keys.Count(t => samples.GetValueOrDefault(t) > 100 && withUser.GetValueOrDefault(t) == 0);
        return $"POOL survey threads={tids.Count} broken={broken} (tid:samples/withUserStack) {string.Join(" ", rows)}";
    }

    private static string DiagCapture(uint tid, string dir, string label)
    {
        var etl = Path.Combine(dir, $"intervene-{label}-{Environment.ProcessId}.etl");
        using (var s = new TraceEventSession($"diag-intervene-{label}-{Environment.ProcessId}", etl) { StopOnDispose = true })
        {
            s.EnableKernelProvider(
                Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.Profile,
                Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.Profile);
            Thread.Sleep(3000);
        }
        int samples = 0, kernelIp = 0, walks = 0, walksWithUser = 0, maxUserFrames = 0;
        using (var src = new Microsoft.Diagnostics.Tracing.ETWTraceEventSource(etl))
        {
            src.Kernel.PerfInfoSample += e =>
            {
                if (e.ThreadID != (int)tid) return;
                samples++;
                if (e.InstructionPointer >= 0xFFFF800000000000UL) kernelIp++;
            };
            src.Kernel.StackWalkStack += e =>
            {
                if (e.ThreadID != (int)tid) return;
                walks++;
                var user = 0;
                for (var i = 0; i < e.FrameCount; i++)
                {
                    if (e.InstructionPointer(i) < 0x0000800000000000UL) user++;
                }
                if (user > 0) walksWithUser++;
                maxUserFrames = Math.Max(maxUserFrames, user);
            };
            src.Process();
        }
        return $"samples={samples} kernelIp={kernelIp} stackWalks={walks} walksWithUserFrames={walksWithUser} maxUserFrames={maxUserFrames}";
    }

    // DIAG ONLY: periodically suspend the hot thread, read RIP, and ask the OS whether
    // that RIP has published unwind data (what the kernel ETW stack walker needs).
    private static unsafe void DiagWatchHotThread(uint tid, string path, CancellationToken token, int iterations = 40)
    {
        var lines = new List<string>();
        var h = OpenThread(0x0008 | 0x0002 | 0x0040, false, tid);
        var ctxMem = (IntPtr)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(1232, 16);
        try
        {
            // Walk ntdll's dynamic function table list (DYNAMIC_FUNCTION_TABLE: Min@32, Max@40, Type@80, EntryCount@84).
            var head = RtlGetFunctionTableListHead();
            var tables = new List<(ulong Min, ulong Max, int Type, uint Count, IntPtr Fn, ulong Base)>();
            for (var e = *(IntPtr*)head; e != head && tables.Count < 100_000; e = *(IntPtr*)e)
            {
                tables.Add((*(ulong*)(e + 32), *(ulong*)(e + 40), *(int*)(e + 80), *(uint*)(e + 84), *(IntPtr*)(e + 16), *(ulong*)(e + 48)));
            }
            lines.Add($"dynamic function tables: {tables.Count}; types: {string.Join(",", tables.GroupBy(t => t.Type).Select(g => $"{g.Key}={g.Count()}"))}; total entries: {tables.Sum(t => (long)t.Count)}");
            var probeRip = 0UL;
            for (var n = 0; n < iterations && !token.IsCancellationRequested; n++)
            {
                Thread.Sleep(250);
                new Span<byte>((void*)ctxMem, 1232).Clear();
                *(uint*)(ctxMem + 0x30) = 0x00100001; // CONTEXT_CONTROL (AMD64)
                if (SuspendThread(h) == uint.MaxValue) { lines.Add($"suspend failed {Marshal.GetLastWin32Error()}"); break; }
                bool ok = GetThreadContext(h, ctxMem);
                ResumeThread(h);
                if (!ok) { lines.Add($"getcontext failed {Marshal.GetLastWin32Error()}"); continue; }
                ulong rip = *(ulong*)(ctxMem + 0xF8);
                ulong rsp = *(ulong*)(ctxMem + 0x98);
                var fe = RtlLookupFunctionEntry(rip, out var imageBase, IntPtr.Zero);
                bool inModule = GetModuleHandleExW(0x4 | 0x2, (IntPtr)(long)rip, out var mod);
                string feText = fe == IntPtr.Zero ? "NULL" : $"begin=0x{imageBase + *(uint*)fe:x} end=0x{imageBase + *(uint*)(fe + 4):x} unwind=0x{*(uint*)(fe + 8):x}";
                probeRip = rip;
                lines.Add($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} rip=0x{rip:x} rsp=0x{rsp:x} inImage={inModule} mod=0x{(long)mod:x} funcEntry={feText} imageBase=0x{imageBase:x}");
            }
            var idx = tables.FindIndex(t => probeRip >= t.Min && probeRip < t.Max);
            lines.Add(idx < 0 ? "hot RIP not covered by any dynamic table" : $"hot RIP table index {idx} of {tables.Count}: [{tables[idx].Min:x},{tables[idx].Max:x}) type={tables[idx].Type} entries={tables[idx].Count}");
            for (var gi = 0; gi < tables.Count; gi++)
            {
                var g = tables[gi];
                if (g.Type != 3 || probeRip < g.Min || probeRip >= g.Max) continue;
                var rva = (uint)(probeRip - g.Base);
                int unsorted = 0, badRange = 0, linearHit = -1;
                uint prevBegin = 0;
                for (var k = 0; k < g.Count; k++)
                {
                    var rf = (uint*)(g.Fn + (k * 12));
                    if (k > 0 && rf[0] < prevBegin) unsorted++;
                    if (rf[1] <= rf[0]) badRange++;
                    if (rva >= rf[0] && rva < rf[1]) linearHit = k;
                    prevBegin = rf[0];
                }
                int lo = 0, hi = (int)g.Count - 1, binHit = -1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var rf = (uint*)(g.Fn + (mid * 12));
                    if (rva < rf[0]) hi = mid - 1; else if (rva >= rf[1]) lo = mid + 1; else { binHit = mid; break; }
                }
                lines.Add($"growable[{gi}] base=0x{g.Base:x} fn=0x{(long)g.Fn:x} count={g.Count} rva=0x{rva:x} unsortedPairs={unsorted} badRanges={badRange} linearHit={linearHit} binarySearchHit={binHit}");
                var center = linearHit >= 0 ? linearHit : lo;
                for (var k = Math.Max(0, center - 3); k < Math.Min((int)g.Count, center + 4); k++)
                {
                    var rf = (uint*)(g.Fn + (k * 12));
                    lines.Add($"    rf[{k}] begin=0x{rf[0]:x} end=0x{rf[1]:x} unwind=0x{rf[2]:x}");
                }
            }
            foreach (var (t, i) in tables.Select((t, i) => (t, i)).Where(x => x.i < 3 || x.i >= tables.Count - 3 || x.i == idx))
            {
                lines.Add($"  table[{i}] [{t.Min:x},{t.Max:x}) size=0x{t.Max - t.Min:x} type={t.Type} entries={t.Count}");
            }
        }
        catch (Exception ex) { lines.Add("watcher exception: " + ex); }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)ctxMem);
            CloseHandle(h);
            File.AppendAllLines(path, lines);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BurnManagedCpu(CancellationToken cancellationToken)
    {
        ulong value = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < 10_000; i++)
            {
                value = unchecked(value * 1_664_525 + 1_013_904_223);
            }
        }
        GC.KeepAlive(value);
    }
}
