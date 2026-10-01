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
    "capture-self" => await Capture.RunAsync(args[1], args[2], args[3], int.Parse(args[4]), self: true),
    _ => throw new ArgumentException(args[0]),
};

static class Native
{
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
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

    public static List<string> StartThreads()
    {
        var started = new CountdownEvent(4);
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
        started.Wait();
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
            foreach (var l in Workload.StartThreads()) { var parts = l.Split(' '); roles[int.Parse(parts[2])] = parts[1]; }
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
