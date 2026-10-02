// Throwaway diagnostic for PR #1080: why do GitHub Windows runners deliver kernel
// profile samples without call stacks for a hot managed thread? Never merged.
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

return args[0] switch
{
    "workload" => Workload.Run(int.Parse(args[1])),
    "capture" => await Capture.RunAsync(args[1], args[2], args[3], int.Parse(args[4])),
    "analyze" => Capture.AnalyzeOnly(args[1], int.Parse(args[2]), args.Skip(3).Select(int.Parse).ToArray()),
    "churn" => Churn.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]),
    "capture-self" => await Capture.RunAsync(args[1], args[2], args[3], int.Parse(args[4]), self: true),
    _ => throw new ArgumentException(args[0]),
};

static class Churn
{
    static volatile bool s_stop;
    static void Burn() { ulong v = 1; while (!s_stop) for (int i = 0; i < 10_000; i++) v = unchecked(v * 1_664_525 + 1_013_904_223); GC.KeepAlive(v); }

    static Thread StartBurner(out int tid)
    {
        int t = 0; using var ready = new ManualResetEventSlim();
        var th = new Thread(() => { t = (int)Native.GetCurrentThreadId(); ready.Set(); Burn(); }) { IsBackground = true };
        th.Start(); ready.Wait(); tid = t; return th;
    }

    // variant: "stop" = dispose session while burning; "stack" = kernel Profile with stacks; "clr" adds CLR stack providers.
    public static int Run(string variant, int sessions, int sessionMs, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var hot = StartBurner(out var hotTid);
        Console.WriteLine($"INFO variant={variant} sessions={sessions} sessionMs={sessionMs} hotTid={hotTid}");
        for (int i = 0; i < sessions; i++)
        {
            var etl = Path.Combine(outDir, $"churn-{i}.etl");
            using (var s = new TraceEventSession($"etwstackdiag-churn-{i}", etl) { StopOnDispose = true })
            {
                s.EnableKernelProvider(KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.Thread,
                    KernelTraceEventParser.Keywords.Profile);
                if (variant.Contains("clr"))
                    s.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, 0x10 | 0x8);
                Thread.Sleep(sessionMs);
            }
            File.Delete(etl);
        }
        var fresh = StartBurner(out var freshTid);
        var final = Path.Combine(outDir, "final.etl");
        using (var s = new TraceEventSession("etwstackdiag-churn-final", final) { StopOnDispose = true })
        {
            s.EnableKernelProvider(KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ImageLoad |
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread, KernelTraceEventParser.Keywords.Profile);
            Thread.Sleep(3000);
        }
        s_stop = true; hot.Join(); fresh.Join();
        int hotS = 0, hotW = 0, hotU = 0, frS = 0, frW = 0, frU = 0;
        using (var src = new ETWTraceEventSource(final))
        {
            src.Kernel.PerfInfoSample += e => { if (e.ThreadID == hotTid) hotS++; else if (e.ThreadID == freshTid) frS++; };
            src.Kernel.StackWalkStack += e =>
            {
                bool user = false; for (int i = 0; i < e.FrameCount; i++) if (e.InstructionPointer(i) < 0x0000800000000000UL) { user = true; break; }
                if (e.ThreadID == hotTid) { hotW++; if (user) hotU++; } else if (e.ThreadID == freshTid) { frW++; if (user) frU++; }
            };
            src.Process();
        }
        Console.WriteLine($"RESULT variant={variant} sessions={sessions} hot: samples={hotS} walks={hotW} withUser={hotU} | fresh: samples={frS} walks={frW} withUser={frU}");
        return 0;
    }
}

static class Native
{
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("ntdll.dll")] public static extern int RtlAddGrowableFunctionTable(out IntPtr handle, IntPtr table, uint count, uint max, IntPtr rangeStart, IntPtr rangeEnd);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("ntdll.dll")] public static extern IntPtr RtlGetFunctionTableListHead();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll.dll")] public static extern unsafe uint RtlComputeCrc32(uint initial, byte* buffer, int length);
}

static class Workload
{
    public static volatile bool Stop;
    public static long Sink;

    public static int Run(int seconds)
    {
        foreach (var l in StartThreads()) Console.WriteLine(l);
        Console.WriteLine($"PID {Environment.ProcessId}");
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Stop = true;
        return 0;
    }

    public static unsafe int CountDynamicTables()
    {
        var head = Native.RtlGetFunctionTableListHead();
        int n = 0;
        for (var e = *(IntPtr*)head; e != head && n < 1_000_000; e = *(IntPtr*)e) n++;
        return n;
    }

    public static unsafe List<string> DescribeTables()
    {
        var r = new List<string>();
        var head = Native.RtlGetFunctionTableListHead();
        var t = new List<(ulong Min, ulong Max, int Type, uint Count)>();
        for (var e = *(IntPtr*)head; e != head && t.Count < 1_000_000; e = *(IntPtr*)e)
            t.Add((*(ulong*)(e + 32), *(ulong*)(e + 40), *(int*)(e + 80), *(uint*)(e + 84)));
        r.Add($"INFO tables={t.Count} types={string.Join(",", t.GroupBy(x => x.Type).Select(g => $"{g.Key}={g.Count()}"))}");
        for (int i = 0; i < t.Count; i++)
            if (t[i].Max - t[i].Min >= 0x10000)
                r.Add($"INFO table[{i}] [{t[i].Min:x},{t[i].Max:x}) type={t[i].Type} entries={t[i].Count}");
        return r;
    }

    public static unsafe void RegisterDummyTables(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var region = Native.VirtualAlloc(IntPtr.Zero, (UIntPtr)0x10000, 0x2000, 0x01); // MEM_RESERVE, PAGE_NOACCESS
            var table = (IntPtr)NativeMemory.AllocZeroed(12);
            int st = Native.RtlAddGrowableFunctionTable(out _, table, 0, 1, region, region + 0x10000);
            if (st != 0) throw new InvalidOperationException($"RtlAddGrowableFunctionTable failed 0x{st:x} at {i}");
        }
    }

    // Collectible dynamic assembly => new LoaderAllocator code heap => new range section and
    // new growable function table registered *after* everything already in the list.
    static Func<long> BuildCollectibleLoop()
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName("CollectibleLoop"), AssemblyBuilderAccess.RunAndCollect);
        var mb = ab.DefineDynamicModule("m");
        var tb = mb.DefineType("T", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed);
        var stopField = tb.DefineField("Stop", typeof(bool), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var m = tb.DefineMethod("Loop", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, typeof(long), Type.EmptyTypes);
        m.SetImplementationFlags(System.Reflection.MethodImplAttributes.NoInlining | System.Reflection.MethodImplAttributes.AggressiveOptimization);
        var il = m.GetILGenerator();
        var acc = il.DeclareLocal(typeof(long));
        var top = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Stloc, acc);
        il.MarkLabel(top);
        il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ldc_I8, 43L); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldc_I8, 3L); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, acc);
        il.Emit(OpCodes.Volatile); il.Emit(OpCodes.Ldsfld, stopField);
        il.Emit(OpCodes.Brfalse, top);
        il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ret);
        var t = tb.CreateType();
        return t.GetMethod("Loop")!.CreateDelegate<Func<long>>();
    }

    public static List<string> StartThreads()
    {
        var dummies = int.Parse(Environment.GetEnvironmentVariable("DIAG_DUMMY_TABLES") ?? "0");
        var before = CountDynamicTables();
        RegisterDummyTables(dummies);
        var collectible = BuildCollectibleLoop();
        var started = new CountdownEvent(5);
        var lines = new List<string>();
        void Start(string name, Action body)
        {
            var t = new Thread(() =>
            {
                lock (lines) lines.Add($"TID {name} {Native.GetCurrentThreadId()}");
                started.Signal();
                body();
            }) { IsBackground = true, Name = name };
            t.Start();
        }

        Start("managed-default", () => Sink += ManagedDefault());
        Start("managed-aggopt", () => Sink += ManagedAggressive());
        var lcg = BuildDynamicLoop();
        Start("managed-lcg", () => Sink += lcg());
        Start("native-ntdll", NativeLoop);
        Start("collectible-new", () => Sink += collectible());
        started.Wait();
        var trimMs = int.Parse(Environment.GetEnvironmentVariable("DIAG_TRIM_MS") ?? "0");
        if (trimMs > 0)
        {
            new Thread(() =>
            {
                while (!Stop) { Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1); Thread.Sleep(trimMs); }
            }) { IsBackground = true, Name = "trimmer" }.Start();
        }
        lines.Add($"INFO trimMs={trimMs}");
        Thread.Sleep(500);
        lines.AddRange(DescribeTables());
        lines.Add($"INFO dynamicTablesBefore={before} dummies={dummies} after={CountDynamicTables()}");
        return lines;
    }

    // R2R-precompiled when published with PublishReadyToRun; tier0+OSR when DOTNET_ReadyToRun=0.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long ManagedDefault()
    {
        long acc = 0;
        while (!Stop) { for (int i = 0; i < 1_000_000; i++) acc = acc * 31 + i; }
        return acc;
    }

    // crossgen skips AggressiveOptimization methods: always jitted, fully optimized.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long ManagedAggressive()
    {
        long acc = 0;
        while (!Stop) { for (int i = 0; i < 1_000_000; i++) acc = acc * 37 + i; }
        return acc;
    }

    // Lightweight-codegen (DynamicMethod): jitted into the dynamic code heap.
    static Func<long> BuildDynamicLoop()
    {
        var dm = new DynamicMethod("LcgLoop", typeof(long), Type.EmptyTypes, typeof(Workload).Module);
        var il = dm.GetILGenerator();
        var acc = il.DeclareLocal(typeof(long));
        var top = il.DefineLabel();
        var stop = typeof(Workload).GetField(nameof(Stop))!;
        il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Stloc, acc);
        il.MarkLabel(top);
        il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ldc_I8, 41L); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldc_I8, 7L); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, acc);
        il.Emit(OpCodes.Volatile); il.Emit(OpCodes.Ldsfld, stop);
        il.Emit(OpCodes.Brfalse, top);
        il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ret);
        return dm.CreateDelegate<Func<long>>();
    }

    static unsafe void NativeLoop()
    {
        var buffer = GC.AllocateUninitializedArray<byte>(64 * 1024 * 1024, pinned: true);
        uint crc = 0;
        fixed (byte* p = buffer)
        {
            while (!Stop) crc = Native.RtlComputeCrc32(crc, p, buffer.Length);
        }
        Sink += crc;
    }
}

static class Capture
{
    public static async Task<int> RunAsync(string mode, string label, string outDir, int seconds, bool self = false)
    {
        Directory.CreateDirectory(outDir);
        var exe = Environment.ProcessPath!;
        var roles = new Dictionary<int, string>();
        Process? child = null;
        int pid;
        if (self)
        {
            pid = Environment.ProcessId;
            foreach (var l in Workload.StartThreads()) { var parts = l.Split(' '); if (parts[0] == "TID") roles[int.Parse(parts[2])] = parts[1]; else Console.WriteLine(l); }
        }
        else
        {
            child = Process.Start(new ProcessStartInfo(exe, $"workload {seconds + 20}") { RedirectStandardOutput = true })!;
            pid = child.Id;
            string? line;
            while ((line = await child.StandardOutput.ReadLineAsync()) != "READY")
            {
                if (line is null) throw new InvalidOperationException("workload exited early");
                var parts = line.Split(' ');
                if (parts[0] == "TID") roles[int.Parse(parts[2])] = parts[1];
                else Console.WriteLine(line);
            }
        }
        Console.WriteLine($"[{label}] child pid {pid}, threads: {string.Join(", ", roles.Select(r => $"{r.Value}={r.Key}"))}");

        var etl = Path.Combine(outDir, $"{label}.etl");
        var merged = Path.Combine(outDir, $"{label}.merged.etl");
        switch (mode)
        {
            case "private":
            case "kernellogger":
                {
                    var name = mode == "private" ? $"etwstackdiag-{label}" : KernelTraceEventParser.KernelSessionName;
                    using (var s = new TraceEventSession(name, etl) { StopOnDispose = true })
                    {
                        s.EnableKernelProvider(
                            KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ImageLoad |
                            KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
                            KernelTraceEventParser.Keywords.Profile);
                        if (mode == "private")
                        {
                            s.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, 0x10 | 0x8);
                            s.EnableProvider(ClrRundownTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, 0x10 | 0x8 | 0x40);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        s.Stop();
                    }
                    TraceEventSession.Merge([etl], merged, TraceEventMergeOptions.ImageIDsOnly);
                    break;
                }
            case "wpr":
                Run("wpr", "-cancel", allowFail: true);
                Run("wpr", "-start CPU -filemode");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                Run("wpr", $"-stop \"{merged}\"");
                break;
            default:
                throw new ArgumentException(mode);
        }
        Workload.Stop = true;
        try { child?.Kill(); } catch { }

        var report = Analyze(merged, pid, roles, label, mode);
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(outDir, $"{label}.txt"), report);
        return 0;
    }

    public static int AnalyzeOnly(string etl, int pid, int[] hotTids)
    {
        Console.WriteLine(Analyze(etl, pid, hotTids.ToDictionary(t => t, _ => "HOT-BurnManagedCpu"), Path.GetFileName(etl), "analyze"));
        return 0;
    }

    static void Run(string file, string args, bool allowFail = false)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine($"> {file} {args} => {p.ExitCode} {o.Trim()}");
        if (p.ExitCode != 0 && !allowFail) throw new InvalidOperationException($"{file} {args} failed");
    }

    const ulong KernelBase = 0xFFFF800000000000;

    sealed class Row
    {
        public long Samples, SamplesKernelIp, SamplesUserIp;
        public long RawStackEvents, RawStackWithUser, RawStackKernelOnly, RawStackUserOnly;
        public long LogNoStack, LogKernelOnly, LogWithUser;
        public Dictionary<string, long> LeafUserModule = new();
        public Dictionary<string, long> SampleIpModule = new();
    }

    static string Analyze(string etl, int pid, Dictionary<int, string> roles, string label, string mode)
    {
        var rows = new SortedDictionary<int, Row>();
        Row R(int tid) => rows.TryGetValue(tid, out var r) ? r : rows[tid] = new Row();
        var eventNames = new SortedDictionary<string, long>();
        var stackEventNamesAllPids = new SortedDictionary<string, long>();
        long lost = 0;

        using (var src = new ETWTraceEventSource(etl))
        {
            src.Kernel.All += e =>
            {
                if (e.ProviderGuid == KernelTraceEventParser.ProviderGuid && e.TaskName == "StackWalk" || e.EventName.StartsWith("StackWalk"))
                {
                    stackEventNamesAllPids[e.EventName] = stackEventNamesAllPids.GetValueOrDefault(e.EventName) + 1;
                }
                if (e.ProcessID == pid)
                {
                    eventNames[e.EventName] = eventNames.GetValueOrDefault(e.EventName) + 1;
                }
            };
            src.Kernel.PerfInfoSample += d =>
            {
                if (d.ProcessID != pid) return;
                var r = R(d.ThreadID);
                r.Samples++;
                if (d.InstructionPointer >= KernelBase) r.SamplesKernelIp++; else r.SamplesUserIp++;
            };
            src.Kernel.StackWalkStack += d =>
            {
                if (d.ProcessID != pid) return;
                var r = R(d.ThreadID);
                r.RawStackEvents++;
                bool user = false, kernel = false;
                for (int i = 0; i < d.FrameCount; i++)
                {
                    if (d.InstructionPointer(i) >= KernelBase) kernel = true; else user = true;
                }
                if (user) r.RawStackWithUser++;
                if (kernel && !user) r.RawStackKernelOnly++;
                if (user && !kernel) r.RawStackUserOnly++;
            };
            src.Process();
            lost = src.EventsLost;
        }

        var etlx = TraceLog.CreateFromEventTraceLogFile(etl, null, new TraceLogOptions { ShouldResolveSymbols = _ => false, LocalSymbolsOnly = true });
        using (var log = TraceLog.OpenOrConvert(etlx))
        {
            foreach (var ev in log.Events)
            {
                if (ev is not SampledProfileTraceData s || s.ProcessID != pid) continue;
                var r = R(s.ThreadID);
                var ipModule = log.CodeAddresses.ModuleFile(s.IntructionPointerCodeAddressIndex())?.Name ?? (s.InstructionPointer >= KernelBase ? "<kernel?>" : "<no-module>");
                r.SampleIpModule[ipModule] = r.SampleIpModule.GetValueOrDefault(ipModule) + 1;
                var idx = s.CallStackIndex();
                if (idx == CallStackIndex.Invalid) { r.LogNoStack++; continue; }
                string? leafUser = null;
                for (var c = idx; c != CallStackIndex.Invalid; c = log.CallStacks.Caller(c))
                {
                    var ca = log.CallStacks.CodeAddressIndex(c);
                    if (log.CodeAddresses.Address(ca) < KernelBase)
                    {
                        leafUser = log.CodeAddresses.ModuleFile(ca)?.Name ?? "<no-module>";
                        break;
                    }
                }
                if (leafUser is null) { r.LogKernelOnly++; continue; }
                r.LogWithUser++;
                r.LeafUserModule[leafUser] = r.LeafUserModule.GetValueOrDefault(leafUser) + 1;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== {label} (mode={mode}, env DOTNET_ReadyToRun={Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "-"}, DOTNET_TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "-"}) pid={pid} lost={lost}");
        sb.AppendLine("role             tid    samples kIP    uIP    | rawStk withUser kOnly  uOnly  | logNoStk logKOnly logWithUser | leaf user modules | sample IP modules");
        foreach (var (tid, r) in rows.Where(kv => kv.Value.Samples > 20 || roles.ContainsKey(kv.Key)))
        {
            var role = roles.GetValueOrDefault(tid, "-");
            string Top(Dictionary<string, long> d) => string.Join(" ", d.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key}:{kv.Value}"));
            sb.AppendLine($"{role,-16} {tid,-6} {r.Samples,-7} {r.SamplesKernelIp,-6} {r.SamplesUserIp,-6} | {r.RawStackEvents,-6} {r.RawStackWithUser,-8} {r.RawStackKernelOnly,-6} {r.RawStackUserOnly,-6} | {r.LogNoStack,-8} {r.LogKernelOnly,-8} {r.LogWithUser,-11} | {Top(r.LeafUserModule)} | {Top(r.SampleIpModule)}");
        }
        sb.AppendLine("kernel events for pid: " + string.Join(", ", eventNames.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("stack events (all pids): " + string.Join(", ", stackEventNamesAllPids.Select(kv => $"{kv.Key}={kv.Value}")));
        return sb.ToString();
    }
}
