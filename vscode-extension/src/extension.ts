import { randomBytes } from "node:crypto";
import { execFile as execFileCallback, spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { basename } from "node:path";
import { createInterface, type Interface as ReadlineInterface } from "node:readline";
import { promisify } from "node:util";
import * as vscode from "vscode";
import {
    describeStreamCompatibilityError,
    isGcCollection,
    isCpuSampleSummary,
    isDumpHeapCaptureResult,
    isHeapCaptureResult,
    isQueryResult,
    isThreadCaptureResult,
    buildQueryFrame,
    parseProcessList,
    parseTypeFilter,
    parseProtocolFrame,
    type CounterValue,
    type CpuSampleSummary,
    type DumpHeapCaptureResult,
    type HeapCaptureResult,
    type QueryResult,
    type ThreadCaptureResult,
    type ProtocolFrame,
    type TargetProcess,
} from "./protocol";

/**
 * The 7 heap query views available unconditionally once any heap snapshot is captured, plus the 3
 * opt-in views gated by `includeStaticFields`/`includeDelegateTargets`/`includeRetentionPaths` on
 * the `capture` request. Mirrors `CliStreamingProtocol.HeapQueryViews` (issue #1116); kept as two
 * separate lists so the always-available ones can be queried unconditionally while the opt-in ones
 * are only queried when the corresponding checkbox was selected before capture.
 */
const ALWAYS_AVAILABLE_HEAP_QUERY_VIEWS = ["roots-by-kind", "finalizer-queue", "fragmentation", "gchandles", "async", "timers", "alc"] as const;

/** The 4 thread query views queried automatically once a thread snapshot is captured. Mirrors `CliStreamingProtocol.ThreadQueryViews` (issue #1116). `thread-statics` is excluded: it needs an exact type name, so it is requested on demand. */
const THREAD_QUERY_VIEWS = ["deadlocks", "unique-stacks", "wait-chains", "threadpool"] as const;

/**
 * The 4 opt-in heap capture enrichments a user can select before a heap capture runs (issue
 * #1116). All default to unchecked/`false`, matching the CLI's own defaults.
 */
interface HeapQueryOptIns {
    includeStaticFields: boolean;
    includeDelegateTargets: boolean;
    includeRetentionPaths: boolean;
    includeRetainedExceptions: boolean;
}

const HEAP_QUERY_OPT_IN_PICKS: Array<{ label: string; description: string; key: keyof HeapQueryOptIns }> = [
    { label: "Static fields", description: "Capture static field values for the `static-fields` drilldown view", key: "includeStaticFields" },
    { label: "Delegate targets", description: "Capture delegate target instances for the `delegate-targets` drilldown view", key: "includeDelegateTargets" },
    { label: "Retention paths", description: "Capture GC root retention paths for the `retention-paths` drilldown view", key: "includeRetentionPaths" },
    { label: "Retained exceptions", description: "Capture exception objects and their retainers for the `retained-exceptions` drilldown view", key: "includeRetainedExceptions" },
];

/**
 * Prompts for the 4 opt-in heap enrichments via a multi-select QuickPick (all unchecked/`false`
 * by default). Returns `undefined` if the user cancels the picker (distinct from selecting none),
 * so callers can abort the capture flow the same way they do for the source/risk prompts.
 */
async function pickHeapQueryOptIns(): Promise<HeapQueryOptIns | undefined> {
    const picked = await vscode.window.showQuickPick(
        HEAP_QUERY_OPT_IN_PICKS.map(item => ({ label: item.label, description: item.description, key: item.key })),
        {
            canPickMany: true,
            placeHolder: "Optionally include richer heap drilldown views (all unchecked by default)",
        },
    );
    if (picked === undefined) {
        return undefined;
    }
    const selectedKeys = new Set(picked.map(item => item.key));
    return {
        includeStaticFields: selectedKeys.has("includeStaticFields"),
        includeDelegateTargets: selectedKeys.has("includeDelegateTargets"),
        includeRetentionPaths: selectedKeys.has("includeRetentionPaths"),
        includeRetainedExceptions: selectedKeys.has("includeRetainedExceptions"),
    };
}

const execFile = promisify(execFileCallback);
const PROTOCOL_VERSION = 1;
const MAX_WEBVIEW_SERIES = 128;

interface Deferred<T> {
    promise: Promise<T>;
    resolve: (value: T) => void;
    reject: (error: Error) => void;
    settled: boolean;
}

/**
 * A pending one-shot `capture` request keyed by `requestId` (see `StreamChild.pendingCaptures`).
 * `kind` disambiguates the result shape the `capture` response frame must be validated against —
 * `cpu`, `heap`, and `thread` captures share the same request/response envelope but have distinct
 * payloads.
 */
type PendingCapture =
    | { kind: "cpu"; deferred: Deferred<CpuSampleSummary> }
    | { kind: "heap"; deferred: Deferred<HeapCaptureResult> }
    | { kind: "thread-snapshot"; deferred: Deferred<ThreadCaptureResult> };

/** Heap snapshot source the user selects before a "Capture Heap Snapshot" request (see #1110). */
type HeapCaptureSource = "live" | "gcdump";


interface StreamChild {
    process: ChildProcessWithoutNullStreams;
    lines: ReadlineInterface;
    handshake: Deferred<void>;
    started: Deferred<void>;
    terminal: Deferred<void>;
    closed: Deferred<void>;
    sessionId?: string;
    stderrTail: string;
    terminalReceived: boolean;
    startupErrorReported: boolean;
    pendingCaptures: Map<string, PendingCapture>;
    /** Pending follow-up `query` requests keyed by `requestId` (issue #1116), analogous to `pendingCaptures`. */
    pendingQueries: Map<string, Deferred<QueryResult>>;
    /**
     * Set before intentionally closing a connection that never started a live session (e.g. a
     * capture-only connection after its result arrives), so the `close` handler can skip reporting
     * an unexpected-exit error for what is actually a normal, requested shutdown.
     */
    expectedShutdown: boolean;
    /**
     * True from the moment a `start` request is written on this connection until it settles
     * (`started` or a start failure). Lets a concurrent capture-only owner of a shared connection
     * know not to close it out from under an in-flight start attempt (the CLI processes requests
     * strictly in order, so a queued `start` can still be pending when a `capture` response on the
     * same connection arrives first).
     */
    startInFlight: boolean;
    /**
     * True from the moment `closeChild()` begins tearing this connection down (stdin ended,
     * awaiting process exit) until it fully exits. `acquireChild()` must not reuse a connection in
     * this state — its stdin is already ended, so a subsequent write (e.g. a `start` frame) would
     * fail even though the process has not exited yet and still looks "non-terminal, alive".
     */
    closing: boolean;
}

class CounterPanelController implements vscode.Disposable {
    private panel?: vscode.WebviewPanel;
    private panelReady?: Deferred<void>;
    private selectedTarget?: TargetProcess;
    private activeChild?: StreamChild;
    private readonly statusBar: vscode.StatusBarItem;
    private disposed = false;
    private shutdownTask?: Promise<void>;
    private childDisposalTask?: Promise<void>;
    private startCommandTask?: Promise<void>;
    private startStreamTask?: Promise<void>;
    private captureCpuTask?: Promise<void>;
    private captureHeapTask?: Promise<void>;
    private captureThreadSnapshotTask?: Promise<void>;
    /**
     * The most recent thread snapshot handle and the connection that owns it. Handles live in the
     * CLI process, so the connection is kept open after a thread capture (until it is closed by a
     * later capture, a stop, or disposal) to allow on-demand `thread-statics` queries.
     */
    private threadSnapshot?: { session: StreamChild; handle: string };
    private connectChildTask?: Promise<StreamChild>;

    public constructor(
        private readonly output: vscode.OutputChannel,
    ) {
        this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 80);
        this.statusBar.command = "dotnetDiagnostics.startCounters";
        this.statusBar.text = "$(pulse) .NET Counters";
        this.statusBar.tooltip = "Start a live .NET runtime counters panel";
        this.statusBar.show();
    }

    public startCounters(): Promise<void> {
        if (!this.startCommandTask) {
            this.startCommandTask = this.startCountersCore().finally(() => {
                this.startCommandTask = undefined;
            });
        }
        return this.startCommandTask;
    }

    private async startCountersCore(): Promise<void> {
        if (this.disposed) {
            return;
        }

        if (this.activeChild
            && this.activeChild.sessionId !== undefined
            && !this.activeChild.terminalReceived
            && this.activeChild.process.exitCode === null
            && this.activeChild.process.signalCode === null)
        {
            // Only an active *live session* (not a capture-only connection) should short-circuit
            // this command — otherwise a running CPU capture would silently block Start.
            this.panel?.reveal(vscode.ViewColumn.Active);
            return;
        }

        const target = await this.ensureTargetAndPanel();
        if (!target) {
            return;
        }

        await this.startStream(target);
    }

    public captureCpu(): Promise<void> {
        if (!this.captureCpuTask) {
            this.captureCpuTask = this.captureCpuCore().finally(() => {
                this.captureCpuTask = undefined;
            });
        }
        return this.captureCpuTask;
    }

    private async captureCpuCore(): Promise<void> {
        if (this.disposed) {
            return;
        }

        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) {
            return;
        }

        this.postMessage({ type: "captureStatus", kind: "cpu", state: "running", message: "Capturing CPU…" });

        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "cpu",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            return;
        }

        const requestId = randomBytes(12).toString("hex");
        const pending: PendingCapture = { kind: "cpu", deferred: deferred<CpuSampleSummary>() };
        session.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(session, {
                type: "capture",
                requestId,
                kind: "cpu",
                processId: target.processId,
                durationSeconds: 10,
                topN: 10,
            });
            const summary = await withTimeout(pending.deferred.promise, 60_000);
            this.postMessage({ type: "capture", kind: "cpu", summary });
            this.postMessage({ type: "captureStatus", kind: "cpu", state: "done", message: "CPU capture complete." });
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "cpu",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            this.output.appendLine(`CPU capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0) {
                // This connection exists only to serve capture requests — no live session was
                // started on it (and no `start` request is queued/in-flight behind this capture
                // response, which the single-threaded CLI read loop processes strictly in order).
                // Only close it once every other capture sharing this connection (e.g. a heap
                // capture that overlapped with this CPU capture) has also finished, so completing
                // one capture never tears down the connection out from under a sibling capture
                // still in flight. Mark the shutdown as expected so the `close` handler doesn't
                // report it as an unexpected CLI exit over whatever status the capture itself just
                // posted.
                session.expectedShutdown = true;
                await this.closeChild(session);
            }
        }
    }

    public captureHeap(): Promise<void> {
        if (!this.captureHeapTask) {
            this.captureHeapTask = this.captureHeapCore().finally(() => {
                this.captureHeapTask = undefined;
            });
        }
        return this.captureHeapTask;
    }

    /**
     * Takes a single point-in-time heap snapshot. Both supported sources (`live` via ClrMD/ptrace,
     * `gcdump` via EventPipe) are High risk / Acknowledge in Core's `InvocationSafetyRegistry`
     * (unlike CPU sampling's Moderate risk), so — unlike `captureCpuCore` — this flow first asks
     * the user to choose a source, then shows a native modal explaining that source's impact, and
     * only sends the capture request (with `acknowledgeRisk: "high"`) after explicit acknowledgement.
     */
    private async captureHeapCore(): Promise<void> {
        if (this.disposed) {
            return;
        }

        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) {
            return;
        }

        const sourcePick = await vscode.window.showQuickPick(
            [
                {
                    label: "Live (ClrMD via ptrace)",
                    description: "Attaches via ptrace and suspends the target",
                    source: "live" as HeapCaptureSource,
                },
                {
                    label: "GC Dump (EventPipe)",
                    description: "Induces a blocking Gen2 GC",
                    source: "gcdump" as HeapCaptureSource,
                },
            ],
            { placeHolder: "Select a heap snapshot source" },
        );
        if (!sourcePick || this.disposed) {
            return;
        }

        // Opt-in heap drilldown enrichments (issue #1116): purely additive to what the CLI
        // records at capture time, so this doesn't change the risk tier already resolved for
        // `sourcePick.source` above — just ask before the risk modal so the acknowledgement
        // covers the final request shape. A `gcdump` artifact only ever retains per-type node/byte
        // totals (HeapSnapshotQueryDispatcher rejects every one of the 10 heap `query` views below
        // except `top-types`, which isn't part of this feature's scope), so there is nothing useful
        // to opt into or to query afterwards — skip both for that source.
        const queryOptIns = sourcePick.source === "gcdump"
            ? { includeStaticFields: false, includeDelegateTargets: false, includeRetentionPaths: false, includeRetainedExceptions: false }
            : await pickHeapQueryOptIns();
        if (!queryOptIns || this.disposed) {
            return;
        }

        const acknowledged = await vscode.window.showWarningMessage(
            describeHeapCaptureRisk(sourcePick.source, sourcePick.label),
            { modal: true },
            "Acknowledge and Capture",
            "Cancel",
        );
        if (acknowledged !== "Acknowledge and Capture" || this.disposed) {
            return;
        }

        this.postMessage({
            type: "captureStatus",
            kind: "heap",
            state: "running",
            message: `Capturing heap snapshot (${sourcePick.label})…`,
        });

        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "heap",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            return;
        }

        const requestId = randomBytes(12).toString("hex");
        const pending: PendingCapture = { kind: "heap", deferred: deferred<HeapCaptureResult>() };
        session.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(session, {
                type: "capture",
                requestId,
                kind: "heap",
                processId: target.processId,
                source: sourcePick.source,
                topTypes: 20,
                acknowledgeRisk: "high",
                includeStaticFields: queryOptIns.includeStaticFields,
                includeDelegateTargets: queryOptIns.includeDelegateTargets,
                includeRetentionPaths: queryOptIns.includeRetentionPaths,
                includeRetainedExceptions: queryOptIns.includeRetainedExceptions,
            });
            // A `gcdump` capture induces and waits out a blocking Gen2 GC, and a `live` capture
            // suspends the whole target during a ClrMD walk — both can legitimately take longer
            // than the CPU capture's fixed-duration sample, so this uses a longer ceiling.
            const result = await withTimeout(pending.deferred.promise, 120_000);
            this.postMessage({ type: "capture", kind: "heap", source: sourcePick.source, result });
            this.postMessage({
                type: "captureStatus",
                kind: "heap",
                state: "done",
                message: "Heap snapshot capture complete.",
            });
            // Follow-up `query` drilldown requests (issue #1116), sent sequentially on this same
            // connection — matching `DumpAnalysisPanelController`'s sequential-not-concurrent
            // discipline, since the CLI's request loop processes one request at a time per
            // connection. Skipped for `gcdump`: see the opt-in comment above for why none of these
            // views apply to a gcdump-origin snapshot.
            const handle = result.data?.handle;
            if (handle && sourcePick.source !== "gcdump") {
                await this.runHeapQueries(session, handle, queryOptIns);
            }
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "heap",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            this.output.appendLine(`Heap snapshot capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0) {
                // This connection exists only to serve capture requests — no live session was
                // started on it (and no `start` request is queued/in-flight behind this capture
                // response, which the single-threaded CLI read loop processes strictly in order).
                // Only close it once every other capture sharing this connection (e.g. a CPU and a
                // heap capture that overlapped) has also finished, so completing one capture never
                // tears down the connection out from under a sibling capture still in flight.
                // Mark the shutdown as expected so the `close` handler doesn't report it as an
                // unexpected CLI exit over whatever status the capture itself just posted.
                session.expectedShutdown = true;
                await this.closeChild(session);
            }
        }
    }

    /**
     * Sends a follow-up `query` request for `view` against `handle` on `session` (issue #1116)
     * and awaits its response. A pure in-memory read of an already-registered handle is normally
     * fast, but this shares one serialized CLI connection with capture requests (the request loop
     * fully processes one request before reading the next line), so a query issued shortly after
     * another in-flight capture/query can sit queued behind it for a while; 60s gives that a
     * reasonable margin without being as generous as a capture's own ceiling.
     */
    private async sendQuery(session: StreamChild, handle: string, view: string, typeFilter?: string): Promise<QueryResult> {
        const requestId = randomBytes(12).toString("hex");
        const pending = deferred<QueryResult>();
        session.pendingQueries.set(requestId, pending);
        try {
            this.writeFrame(session, buildQueryFrame(requestId, handle, view, typeFilter));
            return await withTimeout(pending.promise, 60_000);
        } finally {
            session.pendingQueries.delete(requestId);
        }
    }

    /**
     * Issues the 7 always-available heap `query` views, plus whichever of the 3 opt-in views the
     * user selected before capture, sequentially (not concurrently — see `sendQuery`'s doc
     * comment) against the just-captured `handle`. Each view's result (or failure) is posted to
     * the webview independently so one slow/failing view doesn't block the others from rendering.
     */
    private async runHeapQueries(session: StreamChild, handle: string, optIns: HeapQueryOptIns): Promise<void> {
        const views: string[] = [...ALWAYS_AVAILABLE_HEAP_QUERY_VIEWS];
        if (optIns.includeStaticFields) {
            views.push("static-fields");
        }
        if (optIns.includeDelegateTargets) {
            views.push("delegate-targets");
        }
        if (optIns.includeRetentionPaths) {
            views.push("retention-paths");
        }
        if (optIns.includeRetainedExceptions) {
            views.push("retained-exceptions");
        }

        for (const view of views) {
            try {
                const result = await this.sendQuery(session, handle, view);
                this.postMessage({ type: "query", kind: "heap", view, result });
            } catch (error) {
                this.postMessage({ type: "queryError", kind: "heap", view, message: errorMessage(error) });
                this.output.appendLine(`Heap query view '${view}' failed: ${errorMessage(error)}`);
            }
        }
    }

    /**
     * Issues the 4 thread `query` views sequentially against the just-captured `handle`, mirroring
     * `runHeapQueries`.
     */
    private async runThreadQueries(session: StreamChild, handle: string): Promise<void> {
        for (const view of THREAD_QUERY_VIEWS) {
            try {
                const result = await this.sendQuery(session, handle, view);
                this.postMessage({ type: "query", kind: "thread-snapshot", view, result });
            } catch (error) {
                this.postMessage({ type: "queryError", kind: "thread-snapshot", view, message: errorMessage(error) });
                this.output.appendLine(`Thread query view '${view}' failed: ${errorMessage(error)}`);
            }
        }
    }

    /**
     * Runs the on-demand `thread-statics` query for `typeFilter` against the last thread snapshot
     * (issue #1126). The view needs an exact type name, so it is never auto-queried.
     */
    private async queryThreadStatics(typeFilter: string): Promise<void> {
        const snapshot = this.threadSnapshot;
        const view = "thread-statics";
        if (!snapshot || snapshot.session.closing || snapshot.session.process.exitCode !== null || snapshot.session.process.signalCode !== null) {
            this.postMessage({ type: "queryError", kind: "thread-snapshot", view, message: "No active thread snapshot. Capture a thread snapshot first." });
            return;
        }
        try {
            const result = await this.sendQuery(snapshot.session, snapshot.handle, view, typeFilter);
            this.postMessage({ type: "query", kind: "thread-snapshot", view, result });
        } catch (error) {
            this.postMessage({ type: "queryError", kind: "thread-snapshot", view, message: errorMessage(error) });
            this.output.appendLine(`Thread query view '${view}' failed: ${errorMessage(error)}`);
        }
    }

    public captureThreadSnapshot(): Promise<void> {
        if (!this.captureThreadSnapshotTask) {
            this.captureThreadSnapshotTask = this.captureThreadSnapshotCore().finally(() => {
                this.captureThreadSnapshotTask = undefined;
            });
        }
        return this.captureThreadSnapshotTask;
    }

    /**
     * Takes a single point-in-time thread snapshot. Unlike heap captures there is only one source
     * for this VS Code flow — always a live ClrMD attach (the CLI's offline `--dump-file` option
     * is not relevant to a live panel) — so this skips the source QuickPick and goes straight to
     * the risk-acknowledgement modal. A live thread snapshot is High risk / Acknowledge in Core's
     * `InvocationSafetyRegistry` (same tier as heap's `live` source, unlike CPU sampling's
     * Moderate risk), so the capture request is only sent after explicit acknowledgement.
     */
    private async captureThreadSnapshotCore(): Promise<void> {
        if (this.disposed) {
            return;
        }

        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) {
            return;
        }

        const acknowledged = await vscode.window.showWarningMessage(
            describeThreadCaptureRisk(),
            { modal: true },
            "Acknowledge and Capture",
            "Cancel",
        );
        if (acknowledged !== "Acknowledge and Capture" || this.disposed) {
            return;
        }

        this.postMessage({
            type: "captureStatus",
            kind: "thread-snapshot",
            state: "running",
            message: "Capturing thread snapshot…",
        });

        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "thread-snapshot",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            return;
        }

        const requestId = randomBytes(12).toString("hex");
        const pending: PendingCapture = { kind: "thread-snapshot", deferred: deferred<ThreadCaptureResult>() };
        session.pendingCaptures.set(requestId, pending);
        this.threadSnapshot = undefined;
        let keepSession = false;
        try {
            this.writeFrame(session, {
                type: "capture",
                requestId,
                kind: "thread-snapshot",
                processId: target.processId,
                maxFramesPerThread: 64,
                acknowledgeRisk: "high",
            });
            // A thread snapshot suspends the whole target during a ClrMD walk, same ceiling
            // reasoning as heap's `live` source — see captureHeapCore's matching comment.
            const result = await withTimeout(pending.deferred.promise, 120_000);
            this.postMessage({ type: "capture", kind: "thread-snapshot", result });
            this.postMessage({
                type: "captureStatus",
                kind: "thread-snapshot",
                state: "done",
                message: "Thread snapshot capture complete.",
            });
            // Follow-up `query` drilldown requests (issue #1116) — same sequential discipline as
            // runHeapQueries/captureHeapCore.
            const handle = result.data?.handle;
            if (handle) {
                this.threadSnapshot = { session, handle };
                keepSession = true;
                await this.runThreadQueries(session, handle);
            }
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "thread-snapshot",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            this.output.appendLine(`Thread snapshot capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !keepSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0) {
                // Same connection-reuse bookkeeping as captureCpuCore/captureHeapCore: only close
                // a capture-only connection once every other capture sharing it has also finished.
                session.expectedShutdown = true;
                await this.closeChild(session);
            }
        }
    }

    /** Discovers a target process (if needed), opens the panel, and waits for it to initialize. */
    private async ensureTargetAndPanel(): Promise<TargetProcess | undefined> {
        if (this.selectedTarget && this.panel) {
            this.panel.reveal(vscode.ViewColumn.Active);
            return this.selectedTarget;
        }

        const cliPath = this.getCliPath();
        let targets: TargetProcess[];
        try {
            const result = await execFile(cliPath, ["processes", "--json"], {
                timeout: 15_000,
                maxBuffer: 2 * 1024 * 1024,
                windowsHide: true,
            });
            targets = parseProcessList(result.stdout);
        } catch (error) {
            await this.showCliError(error);
            return undefined;
        }
        if (this.disposed) {
            return undefined;
        }

        if (targets.length === 0) {
            void vscode.window.showInformationMessage("No .NET processes with a diagnostic IPC endpoint were found.");
            return undefined;
        }

        const selected = await vscode.window.showQuickPick(
            targets.map(target => ({
                label: `${target.processId}  ${target.managedEntrypointAssemblyName ?? "Unknown .NET application"}`,
                description: `${target.runtimeVersion} · ${target.operatingSystem}/${target.processArchitecture}`,
                target,
            })),
            { placeHolder: "Select a .NET process for live diagnostics" },
        );
        if (!selected || this.disposed) {
            return undefined;
        }

        this.selectedTarget = selected.target;
        this.openPanel(selected.target);
        await this.waitForPanelReady();
        return selected.target;
    }

    public async stopCounters(): Promise<void> {
        const child = this.activeChild;
        if (!child?.sessionId) {
            this.postMessage({ type: "status", state: "stopped", message: "No live capture is running." });
            return;
        }

        this.postMessage({ type: "status", state: "stopping", message: "Stopping and draining EventPipe…" });
        try {
            this.writeFrame(child, { type: "cancel", sessionId: child.sessionId });
            // The CLI reads requests strictly in order on a shared connection. If a capture is in
            // flight (e.g. triggered while this live session was streaming), this `cancel` frame
            // sits queued behind it and won't be read until that capture's response is fully
            // processed. Give that case extra headroom instead of racing a fixed 10s timeout and
            // force-killing the process while a capture is still legitimately busy. A pending heap
            // or thread-snapshot capture can legitimately run far longer than a CPU capture (a
            // `gcdump` induces and waits out a blocking Gen2 GC; a `live` heap walk or a thread
            // snapshot suspends and walks the whole target), so size the timeout to the longest
            // capture ceiling actually in flight rather than a single CPU-sized constant.
            const hasPendingLongCapture = [...child.pendingCaptures.values()]
                .some(pending => pending.kind === "heap" || pending.kind === "thread-snapshot");
            const timeoutMs = hasPendingLongCapture ? 125_000 : child.pendingCaptures.size > 0 ? 25_000 : 10_000;
            await withTimeout(child.terminal.promise, timeoutMs);
        } catch (error) {
            this.output.appendLine(`Could not confirm CLI stream shutdown: ${errorMessage(error)}`);
            child.process.kill();
        }
    }

    public dispose(): void {
        void this.shutdown();
    }

    public shutdown(): Promise<void> {
        if (this.shutdownTask) {
            return this.shutdownTask;
        }
        this.disposed = true;
        this.statusBar.dispose();
        this.panel?.dispose();
        this.shutdownTask = this.disposeChild();
        return this.shutdownTask;
    }

    private getCliPath(): string {
        return resolveCliPath();
    }

    private getHistoryDurationSeconds(): number {
        const configured = vscode.workspace
            .getConfiguration("dotnetDiagnostics")
            .get<number>("liveView.historyDurationSeconds", 120);
        return Number.isFinite(configured) && configured > 0 ? configured : 120;
    }

    private openPanel(target: TargetProcess): void {
        if (this.panel) {
            this.panel.reveal(vscode.ViewColumn.Active);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            "dotnetDiagnostics.counters",
            `.NET Counters — ${target.processId}`,
            vscode.ViewColumn.Active,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [],
            },
        );
        this.panel = panel;
        this.panelReady = deferred<void>();
        panel.webview.html = renderHtml(target, randomBytes(18).toString("base64url"), this.getHistoryDurationSeconds());
        const configListener = vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration("dotnetDiagnostics.liveView.historyDurationSeconds")) {
                this.postMessage({ type: "historyDuration", seconds: this.getHistoryDurationSeconds() });
            }
        });
        panel.webview.onDidReceiveMessage(message => {
            if (!isRecord(message) || typeof message.type !== "string") {
                return;
            }

            if (message.type === "ready") {
                this.panelReady?.resolve();
            } else if (message.type === "start" && this.selectedTarget) {
                void this.startStream(this.selectedTarget);
            } else if (message.type === "stop") {
                void this.stopCounters();
            } else if (message.type === "captureCpu") {
                void this.captureCpu();
            } else if (message.type === "captureHeap") {
                void this.captureHeap();
            } else if (message.type === "captureThreadSnapshot") {
                void this.captureThreadSnapshot();
            } else if (message.type === "queryThreadStatics") {
                const typeFilter = parseTypeFilter(message.typeFilter);
                if (typeFilter === undefined) {
                    this.postMessage({ type: "queryError", kind: "thread-snapshot", view: "thread-statics", message: "A non-empty exact type name is required." });
                } else {
                    void this.queryThreadStatics(typeFilter);
                }
            }
        }, undefined, []);
        panel.onDidDispose(() => {
            configListener.dispose();
            this.panel = undefined;
            this.selectedTarget = undefined;
            this.panelReady?.resolve();
            this.panelReady = undefined;
            void this.disposeChild();
        });
    }

    private async waitForPanelReady(): Promise<void> {
        if (!this.panelReady) {
            return;
        }

        try {
            await withTimeout(this.panelReady.promise, 5_000);
        } catch {
            this.output.appendLine("The counters panel did not finish initializing before the timeout.");
        }
    }

    private startStream(target: TargetProcess): Promise<void> {
        if (!this.startStreamTask) {
            this.startStreamTask = this.startStreamCore(target).finally(() => {
                this.startStreamTask = undefined;
            });
        }
        return this.startStreamTask;
    }

    private async startStreamCore(target: TargetProcess): Promise<void> {
        if (this.disposed) {
            return;
        }
        if (this.activeChild && this.activeChild.sessionId !== undefined && !this.activeChild.terminalReceived) {
            // A live session is already running on the shared connection; nothing to do here. The
            // webview's Start button is disabled while running, but guard against a stray message.
            return;
        }

        this.postMessage({ type: "status", state: "starting", message: "Negotiating CLI protocol…" });

        let session: StreamChild;
        try {
            ({ session } = await this.acquireChild());
        } catch (error) {
            this.postMessage({ type: "status", state: "error", message: withRuntimeGuidance(errorMessage(error)) });
            this.output.appendLine(`Could not launch the CLI: ${errorMessage(error)}`);
            return;
        }

        try {
            session.startInFlight = true;
            this.writeFrame(session, {
                type: "start",
                requestId: randomBytes(12).toString("hex"),
                processId: target.processId,
                kinds: [
                    { kind: "counters", providers: ["System.Runtime"], intervalSeconds: 1, observationCapacity: 256 },
                    { kind: "gc", observationCapacity: 256 },
                ],
            });
            await withTimeout(session.started.promise, 30_000);
            this.postMessage({ type: "status", state: "running", message: "Live counters and GC pauses are streaming." });
        } catch (error) {
            if (!session.startupErrorReported) {
                this.postMessage({ type: "status", state: "error", message: withRuntimeGuidance(errorMessage(error)) });
            }
            this.output.appendLine(`Live counter startup failed: ${errorMessage(error)}`);
            await this.closeChild(session);
        } finally {
            session.startInFlight = false;
        }
    }

    /**
     * Reuses `activeChild` if it is still a live, non-terminal connection; otherwise closes any
     * stale leftover connection (an ended live session or an exited process) and opens a fresh
     * one. Shared by `startStreamCore` and `captureCpuCore` so a capture-only connection can be
     * promoted to a live session (and vice versa) without leaking the previous CLI child.
     */
    private async acquireChild(): Promise<{ session: StreamChild; owns: boolean }> {
        const existing = this.activeChild;
        if (
            existing &&
            !existing.terminalReceived &&
            !existing.closing &&
            existing.process.exitCode === null &&
            existing.process.signalCode === null
        ) {
            return { session: existing, owns: false };
        }
        if (existing) {
            await this.closeChild(existing);
        }
        const session = await this.connectChild();
        return { session, owns: true };
    }

    /**
     * Spawns the CLI child process, wires up frame dispatch/lifecycle handling, and completes the
     * protocol handshake. Does not send a `start` request — callers that need a live session send
     * one afterwards; callers that only need a one-shot `capture` can use the connection as-is.
     *
     * `startStreamCore` and `captureCpuCore` can both reach this method after an unbounded UI
     * await (process quick-pick), so concurrent callers are serialized onto a single in-flight
     * connection attempt instead of racing to spawn two CLI children and clobber `activeChild`.
     */
    private connectChild(): Promise<StreamChild> {
        if (!this.connectChildTask) {
            this.connectChildTask = this.connectChildCore().finally(() => {
                this.connectChildTask = undefined;
            });
        }
        return this.connectChildTask;
    }

    private async connectChildCore(): Promise<StreamChild> {
        let child: ChildProcessWithoutNullStreams;
        try {
            child = this.spawnStream();
        } catch (error) {
            throw new Error(withRuntimeGuidance(errorMessage(error)));
        }
        const session: StreamChild = {
            process: child,
            lines: createInterface({ input: child.stdout }),
            handshake: deferred<void>(),
            started: deferred<void>(),
            terminal: deferred<void>(),
            closed: deferred<void>(),
            stderrTail: "",
            terminalReceived: false,
            startupErrorReported: false,
            pendingCaptures: new Map(),
            pendingQueries: new Map(),
            expectedShutdown: false,
            startInFlight: false,
            closing: false,
        };
        this.activeChild = session;
        this.statusBar.text = "$(sync~spin) .NET Counters";

        session.lines.on("line", line => this.handleFrame(session, line));
        child.stdin.on("error", error => this.failPending(session, error));
        child.stderr.on("data", (chunk: Buffer | string) => {
            session.stderrTail = (session.stderrTail + chunk.toString()).slice(-8_192);
        });
        child.once("error", error => {
            this.failPending(session, error);
        });
        child.once("close", (code, signal) => {
            session.lines.close();
            if (!session.terminalReceived && !session.expectedShutdown) {
                const details = session.stderrTail.trim();
                const reason = details
                    ? describeStreamCompatibilityError(details)
                    : `CLI exited (code ${code ?? "unknown"}, signal ${signal ?? "none"}).`;
                this.failPending(session, new Error(reason));
                if (!session.startupErrorReported) {
                    this.postMessage({
                        type: "status",
                        state: "error",
                        message: withRuntimeGuidance(reason),
                    });
                }
                if (details) {
                    this.output.appendLine(`CLI stream stderr: ${details}`);
                }
            }
            session.terminal.resolve();
            session.closed.resolve();
            for (const pending of session.pendingCaptures.values()) {
                pending.deferred.reject(new Error("The CLI connection closed before the capture completed."));
            }
            session.pendingCaptures.clear();
            for (const pending of session.pendingQueries.values()) {
                pending.reject(new Error("The CLI connection closed before the query completed."));
            }
            session.pendingQueries.clear();
            if (this.activeChild === session) {
                this.activeChild = undefined;
                this.statusBar.text = "$(pulse) .NET Counters";
            }
        });

        this.writeFrame(session, { type: "hello", protocolVersion: PROTOCOL_VERSION });
        try {
            await withTimeout(session.handshake.promise, 10_000);
        } catch (error) {
            if (!session.startupErrorReported) {
                this.postMessage({ type: "status", state: "error", message: withRuntimeGuidance(errorMessage(error)) });
            }
            await this.closeChild(session);
            throw error;
        }
        return session;
    }

    private spawnStream(): ChildProcessWithoutNullStreams {
        return spawnStreamingCli(this.getCliPath());
    }

    private handleFrame(session: StreamChild, line: string): void {
        let frame: ProtocolFrame;
        try {
            frame = parseProtocolFrame(line);
        } catch (error) {
            const compatibilityError = describeStreamCompatibilityError(line);
            this.failPending(session, new Error(compatibilityError));
            session.startupErrorReported = true;
            this.postMessage({
                type: "status",
                state: "error",
                message: compatibilityError,
            });
            session.process.kill();
            return;
        }

        switch (frame.type) {
            case "hello":
                if (frame.protocolVersion !== PROTOCOL_VERSION) {
                    this.failPending(session, new Error(
                        `CLI protocol mismatch. Extension supports version ${PROTOCOL_VERSION}; CLI reported ${String(frame.protocolVersion)}.`,
                    ));
                    session.process.kill();
                    return;
                }
                session.handshake.resolve();
                break;

            case "started":
                if (typeof frame.sessionId !== "string") {
                    this.failPending(session, new Error("CLI started response did not include a session ID."));
                    session.process.kill();
                    return;
                }
                session.sessionId = frame.sessionId;
                session.started.resolve();
                this.postMessage({
                    type: "session",
                    sessionId: frame.sessionId,
                    kinds: Array.isArray(frame.kinds) ? frame.kinds : ["counters"],
                });
                break;

            case "observation": {
                if (frame.sessionId !== session.sessionId
                    || !Number.isSafeInteger(frame.sequence)
                    || typeof frame.timestamp !== "string") {
                    break;
                }

                // Older (#1091–#1093) CLI builds never tagged observations with a kind; treat an
                // absent kind as the legacy implicit counters session for forward compatibility.
                const kind = typeof frame.kind === "string" ? frame.kind : "counters";
                if (kind === "counters" && isCounterValue(frame.counter)) {
                    this.postMessage({
                        type: "observation",
                        kind: "counters",
                        sequence: frame.sequence,
                        timestamp: frame.timestamp,
                        counter: frame.counter,
                    });
                } else if (kind === "gc" && isGcCollection(frame.collection)) {
                    this.postMessage({
                        type: "observation",
                        kind: "gc",
                        sequence: frame.sequence,
                        timestamp: frame.timestamp,
                        collection: frame.collection,
                    });
                }
                break;
            }

            case "terminal":
                if (frame.sessionId !== session.sessionId
                    || !["stopped", "targetExited", "failed"].includes(String(frame.status))) {
                    this.failPending(session, new Error("CLI sent a terminal frame with an invalid session ID or status."));
                    session.process.kill();
                    return;
                }
                session.terminalReceived = true;
                session.sessionId = undefined;
                session.terminal.resolve();
                this.postMessage({
                    type: "terminal",
                    status: frame.status,
                    kinds: Array.isArray(frame.kinds) ? frame.kinds : ["counters"],
                    eventPipeEventsLost: frame.eventPipeEventsLost,
                    droppedObservations: frame.droppedObservations,
                    error: frame.error,
                });
                this.statusBar.text = "$(pulse) .NET Counters";
                break;

            case "capture": {
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pending = requestId ? session.pendingCaptures.get(requestId) : undefined;
                if (!pending) {
                    break;
                }
                if (pending.kind === "cpu" && isCpuSampleSummary(frame.result)) {
                    pending.deferred.resolve(frame.result);
                } else if (pending.kind === "heap" && isHeapCaptureResult(frame.result)) {
                    pending.deferred.resolve(frame.result);
                } else if (pending.kind === "thread-snapshot" && isThreadCaptureResult(frame.result)) {
                    pending.deferred.resolve(frame.result);
                } else {
                    pending.deferred.reject(new Error("The CLI returned a capture result in an unexpected shape."));
                }
                break;
            }

            // Follow-up heap/thread-snapshot drilldown response for a handle registered by a
            // prior `capture` on this same connection (issue #1116).
            case "query": {
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pending = requestId ? session.pendingQueries.get(requestId) : undefined;
                if (!pending) {
                    break;
                }
                if (isQueryResult(frame.result)) {
                    pending.resolve(frame.result);
                } else {
                    pending.reject(new Error("The CLI returned a query result in an unexpected shape."));
                }
                break;
            }

            case "error": {
                const message = typeof frame.message === "string" ? frame.message : "The CLI returned a protocol error.";
                const error = new Error(
                    frame.code === "protocol_version_unsupported"
                        ? `CLI protocol mismatch: ${message}`
                        // An older CLI build that predates #1110/#1112 does not recognize the
                        // "heap"/"thread-snapshot" capture kinds; surface a concise upgrade hint
                        // instead of the raw CLI error, matching the protocol-version-mismatch
                        // compatibility message.
                        : frame.code === "unsupported_capture_kind"
                            ? withRuntimeGuidance(message)
                            : message,
                );
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pendingCapture = requestId ? session.pendingCaptures.get(requestId) : undefined;
                const pendingQuery = requestId ? session.pendingQueries.get(requestId) : undefined;
                if (pendingCapture) {
                    pendingCapture.deferred.reject(error);
                } else if (pendingQuery) {
                    pendingQuery.reject(error);
                } else if (!session.handshake.settled) {
                    session.handshake.reject(error);
                } else if (!session.started.settled) {
                    session.started.reject(error);
                } else {
                    this.postMessage({ type: "status", state: "error", message });
                    this.output.appendLine(`CLI protocol error: ${message}`);
                    if (session.sessionId) {
                        try {
                            this.writeFrame(session, { type: "cancel", sessionId: session.sessionId });
                        } catch (error) {
                            this.output.appendLine(`Could not cancel the CLI after a protocol error: ${errorMessage(error)}`);
                            session.process.kill();
                        }
                    } else {
                        session.process.kill();
                    }
                }
                break;
            }

            default:
                this.output.appendLine(`Ignored unknown CLI protocol frame type '${frame.type}'.`);
                break;
        }
    }

    private failPending(session: StreamChild, error: Error): void {
        if (!session.handshake.settled) {
            session.handshake.reject(error);
        }
        if (!session.started.settled) {
            session.started.reject(error);
        }
    }

    private writeFrame(session: StreamChild, frame: object): void {
        if (!session.process.stdin.writable) {
            throw new Error("CLI stdin is closed.");
        }
        session.process.stdin.write(`${JSON.stringify(frame)}\n`);
    }

    private postMessage(message: object): void {
        void this.panel?.webview.postMessage(message);
    }

    private async showCliError(error: unknown): Promise<void> {
        const message = errorMessage(error);
        this.output.appendLine(`CLI process discovery failed: ${message}`);
        const action = await vscode.window.showErrorMessage(
            withRuntimeGuidance(message),
            "CLI installation instructions",
        );
        if (action) {
            await vscode.env.openExternal(vscode.Uri.parse(
                "https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/cli-reference.md",
            ));
        }
    }

    private disposeChild(): Promise<void> {
        if (!this.childDisposalTask) {
            this.childDisposalTask = this.disposeChildCore().finally(() => {
                this.childDisposalTask = undefined;
            });
        }
        return this.childDisposalTask;
    }

    private async disposeChildCore(): Promise<void> {
        const child = this.activeChild;
        if (child) {
            if (child.sessionId) {
                try {
                    this.writeFrame(child, { type: "cancel", sessionId: child.sessionId });
                    await withTimeout(child.terminal.promise, 10_000);
                } catch (error) {
                    this.output.appendLine(`Forcing CLI shutdown: ${errorMessage(error)}`);
                    child.process.kill();
                }
            }
            await this.closeChild(child);
        }
    }

    private async closeChild(session: StreamChild): Promise<void> {
        if (session.process.exitCode === null && session.process.signalCode === null) {
            session.closing = true;
            try {
                session.process.stdin.end();
            } catch (error) {
                this.output.appendLine(`Could not close CLI stdin: ${errorMessage(error)}`);
            }
            try {
                await withTimeout(session.closed.promise, 5_000);
            } catch {
                session.process.kill();
                try {
                    await withTimeout(session.closed.promise, 5_000);
                } catch {
                    this.output.appendLine("CLI child did not exit after cancellation and was forcibly terminated.");
                }
            }
        }
        session.lines.close();
        if (this.activeChild === session) {
            this.activeChild = undefined;
        }
    }
}

/** A pending one-shot `capture` request on a {@link DumpAnalysisChild} connection, keyed by `requestId`. */
type DumpAnalysisPendingCapture =
    | { kind: "heap"; deferred: Deferred<DumpHeapCaptureResult> }
    | { kind: "thread-snapshot"; deferred: Deferred<ThreadCaptureResult> };

/**
 * A short-lived, capture-only CLI connection used exactly once by {@link DumpAnalysisPanelController}:
 * handshake, send both capture requests, collect both results, close. Unlike `StreamChild` there is
 * no `started`/`sessionId`/counters-session state at all, because this flow never starts a live
 * streaming session.
 */
interface DumpAnalysisChild {
    process: ChildProcessWithoutNullStreams;
    lines: ReadlineInterface;
    handshake: Deferred<void>;
    closed: Deferred<void>;
    stderrTail: string;
    terminated: boolean;
    pendingCaptures: Map<string, DumpAnalysisPendingCapture>;
    /** Pending follow-up `query` requests keyed by `requestId` (issue #1116), analogous to `pendingCaptures`. */
    pendingQueries: Map<string, Deferred<QueryResult>>;
}

/**
 * Dedicated webview panel for "Analyze Dump File" (#1114). Deliberately NOT built on top of
 * `CounterPanelController`: that controller's entire model (`TargetProcess`, `selectedTarget`,
 * start/stop live session, connection reuse across repeated captures) assumes a live PID, and a
 * dump file has no PID and is analyzed exactly once per panel. This controller instead opens one
 * short-lived capture-only CLI connection, fires the heap-from-dump and thread-snapshot-from-dump
 * requests concurrently, renders both results, and closes the connection — it holds no live-session
 * state and reuses only the module-level spawn/handshake/frame-parsing helpers shared with
 * `CounterPanelController`.
 */
class DumpAnalysisPanelController implements vscode.Disposable {
    private readonly panel: vscode.WebviewPanel;
    private disposed = false;
    /** Tracked so `dispose()` can forcibly end an in-flight capture-only connection when the panel is closed mid-analysis. */
    private activeChild: DumpAnalysisChild | undefined;
    /** Handle of the thread snapshot; the connection stays open (until `dispose`) so `thread-statics` can be queried on demand. */
    private threadHandle: string | undefined;

    public constructor(
        private readonly dumpFilePath: string,
        private readonly output: vscode.OutputChannel,
        /** The 3 opt-in heap enrichments selected before analysis started (issue #1116). */
        private readonly heapQueryOptIns: HeapQueryOptIns,
    ) {
        const fileName = basename(dumpFilePath);
        this.panel = vscode.window.createWebviewPanel(
            "dotnetDiagnostics.dumpAnalysis",
            `Dump Analysis: ${fileName}`,
            vscode.ViewColumn.Active,
            { enableScripts: true, retainContextWhenHidden: true },
        );
        this.panel.webview.html = renderDumpAnalysisHtml(fileName, randomBytes(16).toString("hex"));
        this.panel.webview.onDidReceiveMessage(message => {
            if (!isRecord(message) || message.type !== "queryThreadStatics") {
                return;
            }
            const typeFilter = parseTypeFilter(message.typeFilter);
            if (typeFilter === undefined) {
                this.postMessage({ type: "queryError", kind: "thread-snapshot", view: "thread-statics", message: "A non-empty exact type name is required." });
            } else {
                void this.queryThreadStatics(typeFilter);
            }
        });
        this.panel.onDidDispose(() => {
            this.dispose();
        });
    }

    public reveal(): void {
        this.panel.reveal(vscode.ViewColumn.Active);
    }

    public dispose(): void {
        if (this.disposed) {
            return;
        }
        this.disposed = true;
        // Best-effort: kill rather than gracefully close, since this can run synchronously from
        // panel disposal / extension deactivation and must not block on the child's own shutdown.
        this.activeChild?.process.kill();
        this.panel.dispose();
    }

    /**
     * Opens the capture-only connection, fires the dump captures, renders each result as it
     * arrives, and always closes the connection afterwards (there is nothing left to reuse it for
     * — unlike `CounterPanelController`'s capture-only connections, this panel never starts a live
     * session on the same connection). The two capture requests are sent and awaited one at a time
     * rather than concurrently: `CliStreamingProtocol`'s request loop fully awaits one `capture`
     * before reading the next line on a given connection, so sending both at once would only queue
     * the second behind the first while its own client-side timeout was already ticking.
     */
    public async run(): Promise<void> {
        this.postMessage({ type: "captureStatus", kind: "heap", state: "running", message: "Analyzing heap types from the dump file…" });
        this.postMessage({ type: "captureStatus", kind: "thread-snapshot", state: "running", message: "Analyzing threads and locks from the dump file…" });

        let child: DumpAnalysisChild;
        try {
            child = await this.connect();
        } catch (error) {
            const message = withRuntimeGuidance(errorMessage(error));
            this.postMessage({ type: "captureStatus", kind: "heap", state: "error", message });
            this.postMessage({ type: "captureStatus", kind: "thread-snapshot", state: "error", message });
            return;
        }

        try {
            await this.captureHeapFromDump(child);
            await this.captureThreadSnapshotFromDump(child);
        } finally {
            if (!this.threadHandle) {
                await this.close(child);
            }
        }
    }

    /** See `CounterPanelController.queryThreadStatics`. */
    private async queryThreadStatics(typeFilter: string): Promise<void> {
        const child = this.activeChild;
        const handle = this.threadHandle;
        const view = "thread-statics";
        if (!child || !handle || child.terminated) {
            this.postMessage({ type: "queryError", kind: "thread-snapshot", view, message: "No active thread snapshot for this dump." });
            return;
        }
        try {
            const result = await this.sendQuery(child, handle, view, typeFilter);
            this.postMessage({ type: "query", kind: "thread-snapshot", view, result });
        } catch (error) {
            this.postMessage({ type: "queryError", kind: "thread-snapshot", view, message: errorMessage(error) });
            this.output.appendLine(`Thread query view '${view}' failed: ${errorMessage(error)}`);
        }
    }

    private async captureHeapFromDump(child: DumpAnalysisChild): Promise<void> {
        const requestId = randomBytes(12).toString("hex");
        const pending: DumpAnalysisPendingCapture = { kind: "heap", deferred: deferred<DumpHeapCaptureResult>() };
        child.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(child, {
                type: "capture",
                requestId,
                kind: "heap",
                source: "dump",
                dumpFile: this.dumpFilePath,
                topTypes: 20,
                includeStaticFields: this.heapQueryOptIns.includeStaticFields,
                includeDelegateTargets: this.heapQueryOptIns.includeDelegateTargets,
                includeRetentionPaths: this.heapQueryOptIns.includeRetentionPaths,
                includeRetainedExceptions: this.heapQueryOptIns.includeRetainedExceptions,
            });
            // Offline dump parsing has no live target to time out against, but this still needs a
            // ceiling so a stuck/huge dump can't hang the panel forever; large dumps can take a
            // while to enumerate, so this is generous compared to the live heap-capture ceiling.
            const result = await withTimeout(pending.deferred.promise, 180_000);
            this.postMessage({ type: "capture", kind: "heap", source: "dump", result });
            this.postMessage({ type: "captureStatus", kind: "heap", state: "done", message: "Heap analysis complete." });
            // Follow-up `query` drilldown requests (issue #1116), sent sequentially — see this
            // class's `run()` doc comment for why the CLI connection can't handle concurrent
            // requests.
            const handle = result.data?.handle;
            if (handle) {
                await this.runHeapQueries(child, handle);
            }
        } catch (error) {
            const message = withRuntimeGuidance(errorMessage(error));
            this.postMessage({ type: "captureStatus", kind: "heap", state: "error", message });
            this.output.appendLine(`Dump heap analysis failed: ${message}`);
        } finally {
            child.pendingCaptures.delete(requestId);
        }
    }

    private async captureThreadSnapshotFromDump(child: DumpAnalysisChild): Promise<void> {
        const requestId = randomBytes(12).toString("hex");
        const pending: DumpAnalysisPendingCapture = { kind: "thread-snapshot", deferred: deferred<ThreadCaptureResult>() };
        child.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(child, {
                type: "capture",
                requestId,
                kind: "thread-snapshot",
                dumpFile: this.dumpFilePath,
                maxFramesPerThread: 64,
            });
            const result = await withTimeout(pending.deferred.promise, 180_000);
            this.postMessage({ type: "capture", kind: "thread-snapshot", result });
            this.postMessage({ type: "captureStatus", kind: "thread-snapshot", state: "done", message: "Thread/lock analysis complete." });
            const handle = result.data?.handle;
            if (handle) {
                this.threadHandle = handle;
                await this.runThreadQueries(child, handle);
            }
        } catch (error) {
            const message = withRuntimeGuidance(errorMessage(error));
            this.postMessage({ type: "captureStatus", kind: "thread-snapshot", state: "error", message });
            this.output.appendLine(`Dump thread-snapshot analysis failed: ${message}`);
        } finally {
            child.pendingCaptures.delete(requestId);
        }
    }

    /** See `CounterPanelController.sendQuery`'s doc comment — identical reasoning, different connection type. */
    private async sendQuery(child: DumpAnalysisChild, handle: string, view: string, typeFilter?: string): Promise<QueryResult> {
        const requestId = randomBytes(12).toString("hex");
        const pending = deferred<QueryResult>();
        child.pendingQueries.set(requestId, pending);
        try {
            this.writeFrame(child, buildQueryFrame(requestId, handle, view, typeFilter));
            return await withTimeout(pending.promise, 60_000);
        } finally {
            child.pendingQueries.delete(requestId);
        }
    }

    /** See `CounterPanelController.runHeapQueries`'s doc comment. */
    private async runHeapQueries(child: DumpAnalysisChild, handle: string): Promise<void> {
        const views: string[] = [...ALWAYS_AVAILABLE_HEAP_QUERY_VIEWS];
        if (this.heapQueryOptIns.includeStaticFields) {
            views.push("static-fields");
        }
        if (this.heapQueryOptIns.includeDelegateTargets) {
            views.push("delegate-targets");
        }
        if (this.heapQueryOptIns.includeRetentionPaths) {
            views.push("retention-paths");
        }
        if (this.heapQueryOptIns.includeRetainedExceptions) {
            views.push("retained-exceptions");
        }

        for (const view of views) {
            try {
                const result = await this.sendQuery(child, handle, view);
                this.postMessage({ type: "query", kind: "heap", view, result });
            } catch (error) {
                this.postMessage({ type: "queryError", kind: "heap", view, message: errorMessage(error) });
                this.output.appendLine(`Heap query view '${view}' failed: ${errorMessage(error)}`);
            }
        }
    }

    /** See `CounterPanelController.runThreadQueries`'s doc comment. */
    private async runThreadQueries(child: DumpAnalysisChild, handle: string): Promise<void> {
        for (const view of THREAD_QUERY_VIEWS) {
            try {
                const result = await this.sendQuery(child, handle, view);
                this.postMessage({ type: "query", kind: "thread-snapshot", view, result });
            } catch (error) {
                this.postMessage({ type: "queryError", kind: "thread-snapshot", view, message: errorMessage(error) });
                this.output.appendLine(`Thread query view '${view}' failed: ${errorMessage(error)}`);
            }
        }
    }

    private async connect(): Promise<DumpAnalysisChild> {
        let process: ChildProcessWithoutNullStreams;
        try {
            process = spawnStreamingCli(resolveCliPath());
        } catch (error) {
            throw new Error(withRuntimeGuidance(errorMessage(error)));
        }
        const child: DumpAnalysisChild = {
            process,
            lines: createInterface({ input: process.stdout }),
            handshake: deferred<void>(),
            closed: deferred<void>(),
            stderrTail: "",
            terminated: false,
            pendingCaptures: new Map(),
            pendingQueries: new Map(),
        };
        this.activeChild = child;

        child.lines.on("line", line => this.handleFrame(child, line));
        process.stdin.on("error", error => this.failPending(child, error));
        process.stderr.on("data", (chunk: Buffer | string) => {
            child.stderrTail = (child.stderrTail + chunk.toString()).slice(-8_192);
        });
        process.once("error", error => this.failPending(child, error));
        process.once("close", () => {
            child.lines.close();
            child.terminated = true;
            const details = child.stderrTail.trim();
            if (details && !child.handshake.settled) {
                this.failPending(child, new Error(describeStreamCompatibilityError(details)));
            }
            for (const pending of child.pendingCaptures.values()) {
                pending.deferred.reject(new Error("The CLI connection closed before the capture completed."));
            }
            child.pendingCaptures.clear();
            for (const pending of child.pendingQueries.values()) {
                pending.reject(new Error("The CLI connection closed before the query completed."));
            }
            child.pendingQueries.clear();
            child.closed.resolve();
        });

        this.writeFrame(child, { type: "hello", protocolVersion: PROTOCOL_VERSION });
        try {
            await withTimeout(child.handshake.promise, 10_000);
        } catch (error) {
            await this.close(child);
            throw error;
        }
        return child;
    }

    private handleFrame(child: DumpAnalysisChild, line: string): void {
        let frame: ProtocolFrame;
        try {
            frame = parseProtocolFrame(line);
        } catch (error) {
            const compatibilityError = describeStreamCompatibilityError(line);
            this.failPending(child, new Error(compatibilityError));
            child.process.kill();
            return;
        }

        switch (frame.type) {
            case "hello":
                if (frame.protocolVersion !== PROTOCOL_VERSION) {
                    this.failPending(child, new Error(
                        `CLI protocol mismatch. Extension supports version ${PROTOCOL_VERSION}; CLI reported ${String(frame.protocolVersion)}.`,
                    ));
                    child.process.kill();
                    return;
                }
                child.handshake.resolve();
                break;

            case "capture": {
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pending = requestId ? child.pendingCaptures.get(requestId) : undefined;
                if (!pending) {
                    break;
                }
                if (pending.kind === "heap" && isDumpHeapCaptureResult(frame.result)) {
                    pending.deferred.resolve(frame.result);
                } else if (pending.kind === "thread-snapshot" && isThreadCaptureResult(frame.result)) {
                    pending.deferred.resolve(frame.result);
                } else {
                    pending.deferred.reject(new Error("The CLI returned a capture result in an unexpected shape."));
                }
                break;
            }

            // Follow-up heap/thread-snapshot drilldown response (issue #1116) — see `CounterPanelController`'s matching case.
            case "query": {
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pending = requestId ? child.pendingQueries.get(requestId) : undefined;
                if (!pending) {
                    break;
                }
                if (isQueryResult(frame.result)) {
                    pending.resolve(frame.result);
                } else {
                    pending.reject(new Error("The CLI returned a query result in an unexpected shape."));
                }
                break;
            }

            case "error": {
                const message = typeof frame.message === "string" ? frame.message : "The CLI returned a protocol error.";
                const error = new Error(
                    frame.code === "protocol_version_unsupported"
                        ? `CLI protocol mismatch: ${message}`
                        : frame.code === "unsupported_capture_kind"
                            ? withRuntimeGuidance(message)
                            : message,
                );
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pending = requestId ? child.pendingCaptures.get(requestId) : undefined;
                const pendingQuery = requestId ? child.pendingQueries.get(requestId) : undefined;
                if (pending) {
                    pending.deferred.reject(error);
                } else if (pendingQuery) {
                    pendingQuery.reject(error);
                } else if (!child.handshake.settled) {
                    child.handshake.reject(error);
                } else {
                    this.output.appendLine(`CLI protocol error: ${message}`);
                }
                break;
            }

            default:
                this.output.appendLine(`Ignored unexpected CLI protocol frame type '${frame.type}' during dump analysis.`);
                break;
        }
    }

    private failPending(child: DumpAnalysisChild, error: Error): void {
        if (!child.handshake.settled) {
            child.handshake.reject(error);
        }
        for (const pending of child.pendingCaptures.values()) {
            pending.deferred.reject(error);
        }
        for (const pending of child.pendingQueries.values()) {
            pending.reject(error);
        }
    }

    private writeFrame(child: DumpAnalysisChild, frame: object): void {
        if (!child.process.stdin.writable) {
            throw new Error("CLI stdin is closed.");
        }
        child.process.stdin.write(`${JSON.stringify(frame)}\n`);
    }

    private postMessage(message: object): void {
        if (!this.disposed) {
            void this.panel.webview.postMessage(message);
        }
    }

    private async close(child: DumpAnalysisChild): Promise<void> {
        if (child.process.exitCode === null && child.process.signalCode === null) {
            try {
                child.process.stdin.end();
            } catch (error) {
                this.output.appendLine(`Could not close CLI stdin: ${errorMessage(error)}`);
            }
            try {
                await withTimeout(child.closed.promise, 5_000);
            } catch {
                child.process.kill();
                try {
                    await withTimeout(child.closed.promise, 5_000);
                } catch {
                    this.output.appendLine("CLI child did not exit after dump analysis and was forcibly terminated.");
                }
            }
        }
        child.lines.close();
        if (this.activeChild === child) {
            this.activeChild = undefined;
        }
    }
}

/**
 * Command handler for "Analyze Dump File" (#1114): picks an existing dump file from disk (no
 * extension filter — dumps can be `.dmp`, `.dump`, or extensionless) and opens a dedicated panel
 * analyzing it. Unlike heap/thread-snapshot live capture, dump-sourced requests resolve to Core's
 * Moderate/Warn risk profiles rather than High/Acknowledge (see `InvocationSafetyRegistry`'s
 * `HeapSources.Dump` and `CollectThreadSnapshot`'s `"dump"` profile), so — matching CPU sampling's
 * existing no-modal posture — this proceeds straight from the file picker to capture with no
 * risk-acknowledgement modal.
 */
async function analyzeDumpFile(output: vscode.OutputChannel, context: vscode.ExtensionContext): Promise<void> {
    const picked = await vscode.window.showOpenDialog({
        canSelectMany: false,
        canSelectFiles: true,
        canSelectFolders: false,
        openLabel: "Analyze Dump File",
        title: "Select a process dump file to analyze",
    });
    const dumpFilePath = picked?.[0]?.fsPath;
    if (!dumpFilePath) {
        return;
    }

    // Opt-in heap drilldown enrichments (issue #1116) — asked up front since this panel fires the
    // heap capture immediately on construction, with no separate risk-acknowledgement step to
    // piggyback on (dump-sourced captures are Moderate/Warn risk, not High/Acknowledge).
    const queryOptIns = await pickHeapQueryOptIns();
    if (!queryOptIns) {
        return;
    }

    const controller = new DumpAnalysisPanelController(dumpFilePath, output, queryOptIns);
    // Registered so extension deactivation disposes any still-open dump-analysis panel (and kills
    // its in-flight capture-only connection) rather than leaking a child process past host shutdown.
    context.subscriptions.push(controller);
    await controller.run();
}

let activeController: CounterPanelController | undefined;

export function activate(context: vscode.ExtensionContext): void {
    const output = vscode.window.createOutputChannel(".NET Diagnostics");
    const controller = new CounterPanelController(output);
    activeController = controller;
    const actionsView = vscode.window.createTreeView("dotnetDiagnostics.actions", {
        treeDataProvider: new DiagnosticsActionsProvider(),
        showCollapseAll: false,
    });
    context.subscriptions.push(
        output,
        controller,
        actionsView,
        vscode.commands.registerCommand("dotnetDiagnostics.startCounters", () => controller.startCounters()),
        vscode.commands.registerCommand("dotnetDiagnostics.stopCounters", () => controller.stopCounters()),
        vscode.commands.registerCommand("dotnetDiagnostics.captureCpu", () => controller.captureCpu()),
        vscode.commands.registerCommand("dotnetDiagnostics.captureHeap", () => controller.captureHeap()),
        vscode.commands.registerCommand("dotnetDiagnostics.captureThreadSnapshot", () => controller.captureThreadSnapshot()),
        vscode.commands.registerCommand("dotnetDiagnostics.analyzeDumpFile", () => analyzeDumpFile(output, context)),
    );
}

class DiagnosticsActionsProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    public getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    public getChildren(): vscode.TreeItem[] {
        const start = new vscode.TreeItem("Start Live Counters", vscode.TreeItemCollapsibleState.None);
        start.description = "Choose a running .NET process";
        start.iconPath = new vscode.ThemeIcon("play");
        start.command = {
            command: "dotnetDiagnostics.startCounters",
            title: "Start Live Counters",
        };

        const stop = new vscode.TreeItem("Stop Live Counters", vscode.TreeItemCollapsibleState.None);
        stop.description = "Stop the active capture";
        stop.iconPath = new vscode.ThemeIcon("debug-stop");
        stop.command = {
            command: "dotnetDiagnostics.stopCounters",
            title: "Stop Live Counters",
        };

        const captureCpu = new vscode.TreeItem("Capture CPU Now", vscode.TreeItemCollapsibleState.None);
        captureCpu.description = "Take a point-in-time CPU sample";
        captureCpu.iconPath = new vscode.ThemeIcon("flame");
        captureCpu.command = {
            command: "dotnetDiagnostics.captureCpu",
            title: "Capture CPU Now",
        };

        const captureHeap = new vscode.TreeItem("Capture Heap Snapshot", vscode.TreeItemCollapsibleState.None);
        captureHeap.description = "Take a point-in-time heap snapshot (live or GC dump)";
        captureHeap.iconPath = new vscode.ThemeIcon("archive");
        captureHeap.command = {
            command: "dotnetDiagnostics.captureHeap",
            title: "Capture Heap Snapshot",
        };

        const captureThreadSnapshot = new vscode.TreeItem("Capture Thread Snapshot", vscode.TreeItemCollapsibleState.None);
        captureThreadSnapshot.description = "Take a point-in-time thread/lock snapshot (live)";
        captureThreadSnapshot.iconPath = new vscode.ThemeIcon("list-tree");
        captureThreadSnapshot.command = {
            command: "dotnetDiagnostics.captureThreadSnapshot",
            title: "Capture Thread Snapshot",
        };

        const analyzeDumpFile = new vscode.TreeItem("Analyze Dump File", vscode.TreeItemCollapsibleState.None);
        analyzeDumpFile.description = "Analyze heap types and threads/locks from an existing dump";
        analyzeDumpFile.iconPath = new vscode.ThemeIcon("file-binary");
        analyzeDumpFile.command = {
            command: "dotnetDiagnostics.analyzeDumpFile",
            title: "Analyze Dump File",
        };

        return [start, stop, captureCpu, captureHeap, captureThreadSnapshot, analyzeDumpFile];
    }
}

export async function deactivate(): Promise<void> {
    await activeController?.shutdown();
    activeController = undefined;
}

export function renderHtml(target: TargetProcess, nonce: string, historyDurationSeconds: number): string {
    const label = `${target.managedEntrypointAssemblyName ?? "Unknown .NET application"} · PID ${target.processId}`;
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>.NET runtime counters</title>
  <style nonce="${nonce}">
    body { color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); padding: 0 1.2rem; }
    header { display:flex; align-items:center; justify-content:space-between; gap:1rem; flex-wrap:wrap; }
    .controls { display:flex; gap:.6rem; align-items:center; }
    button, select { color:var(--vscode-button-foreground); background:var(--vscode-button-background); border:0; border-radius:2px; padding:.45rem .75rem; }
    button:hover { background:var(--vscode-button-hoverBackground); }
    button:disabled { opacity:.55; }
    #status, #quality { color:var(--vscode-descriptionForeground); margin:.6rem 0; }
    #value { font-size:1.5rem; font-weight:600; margin:.6rem 0; }
    canvas { width:100%; height:280px; border-bottom:1px solid var(--vscode-panel-border); }
    #error { color:var(--vscode-errorForeground); white-space:pre-wrap; }
    section.panel { margin-top:1.4rem; }
    section.panel h3 { margin-bottom:.3rem; }
    table { border-collapse:collapse; width:100%; font-size:.9rem; }
    th, td { text-align:left; padding:.25rem .6rem; border-bottom:1px solid var(--vscode-panel-border); }
    #gcHeadline { margin:.3rem 0 .6rem; color:var(--vscode-descriptionForeground); }
    #captureStatus { color:var(--vscode-descriptionForeground); margin:.3rem 0; }
    .empty { display:none; }
    .query-views { margin-top:.8rem; }
    details.query-view { border:1px solid var(--vscode-panel-border); border-radius:3px; margin-bottom:.5rem; }
    .statics-form { margin:.5rem 0; display:flex; gap:.5rem; align-items:center; flex-wrap:wrap; }
    .statics-form input { min-width:20rem; }
    .statics-error { color: var(--vscode-errorForeground); }
    details.query-view > summary { cursor:pointer; padding:.4rem .6rem; font-weight:600; }
    details.query-view[open] > summary { border-bottom:1px solid var(--vscode-panel-border); }
    details.query-view pre { margin:0; padding:.6rem; white-space:pre-wrap; word-break:break-word; font-size:.85rem; max-height:360px; overflow:auto; }
    details.query-view.query-view-error > summary { color:var(--vscode-errorForeground); }
    details.query-view.query-view-deadlocks { border-color:var(--vscode-errorForeground); border-width:2px; }
    details.query-view.query-view-deadlocks > summary { color:var(--vscode-errorForeground); font-size:1.05rem; }
  </style>
</head>
<body>
  <header>
    <div><h2>Live runtime counters</h2><div>${escapeHtml(label)}</div></div>
    <div class="controls">
      <label for="metric">Counter</label><select id="metric" aria-label="Select counter"></select>
      <button id="start">Start</button><button id="stop" disabled>Stop</button>
      <button id="captureCpu">Capture CPU now</button>
      <button id="captureHeap">Capture heap snapshot</button>
      <button id="captureThreadSnapshot">Capture thread snapshot</button>
    </div>
  </header>
  <div id="status" role="status">Connecting to CLI…</div>
  <div id="value">Waiting for first observation</div>
  <canvas id="chart" aria-label="Selected counter time series"></canvas>
  <div id="quality"></div>
  <div id="error" role="alert"></div>

  <section class="panel">
    <h3>GC pauses</h3>
    <div id="gcHeadline">No GC pauses observed yet.</div>
    <table id="gcTable">
      <thead><tr><th>Time</th><th>Gen</th><th>Reason</th><th>Type</th><th>Pause</th></tr></thead>
      <tbody id="gcBody"></tbody>
    </table>
  </section>

  <section class="panel">
    <h3>CPU capture</h3>
    <div id="captureStatus"></div>
    <div id="captureResult" class="empty">
      <div id="captureHeadline"></div>
      <table id="captureTable">
        <thead><tr><th>Module</th><th>Method</th><th>Inclusive</th><th>Exclusive</th></tr></thead>
        <tbody id="captureBody"></tbody>
      </table>
    </div>
  </section>

  <section class="panel">
    <h3>Heap snapshot</h3>
    <div id="captureHeapStatus"></div>
    <div id="captureHeapResult" class="empty">
      <div id="captureHeapHeadline"></div>
      <table id="captureHeapTable">
        <thead><tr><th>Type</th><th>Instances</th><th>Bytes</th></tr></thead>
        <tbody id="captureHeapBody"></tbody>
      </table>
      <div id="heapQueryViews" class="query-views"></div>
    </div>
  </section>

  <section class="panel">
    <h3>Thread snapshot</h3>
    <div id="captureThreadStatus"></div>
    <div id="captureThreadResult" class="empty">
      <div id="captureThreadHeadline"></div>
      <table id="captureThreadTable">
        <thead><tr><th>Thread</th><th>State</th><th>Wait reason</th><th>Top frame</th></tr></thead>
        <tbody id="captureThreadBody"></tbody>
      </table>
      <div class="statics-form">
        <input id="threadStaticsType" type="text" maxlength="512" placeholder="Exact type name for thread statics, e.g. MyApp.Cache" />
        <button id="threadStaticsButton" type="button">Query thread statics</button>
        <span id="threadStaticsError" class="statics-error"></span>
      </div>
      <div id="threadQueryViews" class="query-views"></div>
    </div>
  </section>

  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const metricSelect = document.getElementById('metric');
    const startButton = document.getElementById('start');
    const stopButton = document.getElementById('stop');
    const captureCpuButton = document.getElementById('captureCpu');
    const captureHeapButton = document.getElementById('captureHeap');
    const captureThreadSnapshotButton = document.getElementById('captureThreadSnapshot');
    const statusElement = document.getElementById('status');
    const valueElement = document.getElementById('value');
    const qualityElement = document.getElementById('quality');
    const errorElement = document.getElementById('error');
    const canvas = document.getElementById('chart');
    const context = canvas.getContext('2d');
    const gcHeadline = document.getElementById('gcHeadline');
    const gcBody = document.getElementById('gcBody');
    const captureStatusElement = document.getElementById('captureStatus');
    const captureResultElement = document.getElementById('captureResult');
    const captureHeadline = document.getElementById('captureHeadline');
    const captureBody = document.getElementById('captureBody');
    const captureHeapStatusElement = document.getElementById('captureHeapStatus');
    const captureHeapResultElement = document.getElementById('captureHeapResult');
    const captureHeapHeadline = document.getElementById('captureHeapHeadline');
    const captureHeapBody = document.getElementById('captureHeapBody');
    const captureThreadStatusElement = document.getElementById('captureThreadStatus');
    const threadStaticsInput = document.getElementById('threadStaticsType');
    const threadStaticsButton = document.getElementById('threadStaticsButton');
    const threadStaticsError = document.getElementById('threadStaticsError');
    const captureThreadResultElement = document.getElementById('captureThreadResult');
    const captureThreadHeadline = document.getElementById('captureThreadHeadline');
    const captureThreadBody = document.getElementById('captureThreadBody');
    const counters = new Map();
    const maxSeries = ${MAX_WEBVIEW_SERIES};
    // Defensive backstops in case a timestamp is missing/unparseable and time-based eviction
    // (the primary bound, driven by historyDurationMs below) can't kick in for a given point/row.
    const maxPointsBackstop = 5000;
    const maxGcRowsBackstop = 2000;
    let historyDurationMs = ${Math.max(1, Math.round(historyDurationSeconds))} * 1000;
    const gcRows = []; // { element, receivedAtMs }, newest first
    // A logical clock derived from Date.now() that only ever accumulates non-negative deltas: a
    // backward system clock adjustment can't make receipt timestamps non-monotonic (which would
    // break the eviction scans below), and — unlike clamping Date.now() to its own previous
    // maximum — it resumes advancing on the very next tick instead of freezing until wall time
    // catches back up to the pre-adjustment value. It still advances correctly across an OS
    // suspend/resume cycle (unlike performance.now(), whose monotonic clock can pause during
    // suspend on some platforms), because a forward jump is a normal positive delta.
    const monotonicNow = (() => {
      let prevRaw = Date.now();
      let logical = prevRaw;
      return () => {
        const now = Date.now();
        const delta = now - prevRaw;
        if (delta > 0) {
          logical += delta;
        }
        prevRaw = now;
        return logical;
      };
    })();
    let selectedKey = '';
    let lastSequence = 0;
    let sequenceGaps = 0;
    let seriesLimitReached = false;
    let gcPauseCount = 0;
    let gcTotalPauseMs = 0;


    function updateButtons(running) {
      startButton.disabled = running;
      stopButton.disabled = !running;
    }

    function resizeCanvas() {
      const scale = window.devicePixelRatio || 1;
      canvas.width = Math.max(1, Math.floor(canvas.clientWidth * scale));
      canvas.height = Math.max(1, Math.floor(canvas.clientHeight * scale));
      context.setTransform(scale, 0, 0, scale, 0, 0);
      draw();
    }

    function draw() {
      const width = canvas.clientWidth;
      const height = canvas.clientHeight;
      context.clearRect(0, 0, width, height);
      const series = counters.get(selectedKey);
      if (!series || series.points.length === 0) return;
      const values = series.points.map(point => point.value).filter(Number.isFinite);
      if (values.length === 0) return;
      const min = Math.min(...values);
      const max = Math.max(...values);
      const range = Math.max(max - min, Math.abs(max) * 0.01, 1e-9);
      const left = 44, right = width - 12, top = 14, bottom = height - 26;
      context.strokeStyle = getComputedStyle(document.body).getPropertyValue('--vscode-charts-blue') || '#3794ff';
      context.lineWidth = 2;
      context.beginPath();
      series.points.forEach((point, index) => {
        const x = left + (right - left) * (series.points.length === 1 ? 1 : index / (series.points.length - 1));
        const y = bottom - ((point.value - min) / range) * (bottom - top);
        if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
      });
      context.stroke();
      context.fillStyle = getComputedStyle(document.body).color;
      context.font = '12px ' + getComputedStyle(document.body).fontFamily;
      context.fillText(max.toPrecision(4), 2, top + 4);
      context.fillText(min.toPrecision(4), 2, bottom);
    }

    // Drops points older than the rolling history window so it keeps sliding even if observations
    // stop arriving for a while (matching how vscode-js-profile-flame's realtimeViewDuration
    // bounds its visible window). Uses the webview's own receipt time via monotonicNow() (not
    // the CLI-provided observation timestamp, and not Date.now()) as the cutoff basis: this avoids
    // clock skew between a remote extension host and the client, an unparseable timestamp blocking
    // eviction, AND a system wall-clock adjustment making receipt times non-monotonic (which would
    // break the single forward/backward scan below, since it assumes arrival order == time order).
    // A single splice replaces the stale prefix/suffix in one pass instead of repeated shift()/pop().
    function evictOldPoints(series) {
      const points = series.points;
      if (Number.isFinite(historyDurationMs)) {
        const cutoff = monotonicNow() - historyDurationMs;
        let index = 0;
        while (index < points.length && points[index].receivedAtMs < cutoff) index++;
        if (index > 0) points.splice(0, index);
      }
      if (points.length > maxPointsBackstop) points.splice(0, points.length - maxPointsBackstop);
    }

    function evictOldGcRows() {
      if (Number.isFinite(historyDurationMs)) {
        const cutoff = monotonicNow() - historyDurationMs;
        let keep = gcRows.length;
        while (keep > 0 && gcRows[keep - 1].receivedAtMs < cutoff) keep--;
        if (keep < gcRows.length) gcRows.splice(keep).forEach(stale => stale.element.remove());
      }
      if (gcRows.length > maxGcRowsBackstop) gcRows.splice(maxGcRowsBackstop).forEach(stale => stale.element.remove());
    }

    function addCounter(message) {
      const counter = message.counter;
      if (!counter || typeof counter.provider !== 'string' || typeof counter.name !== 'string' ||
          typeof counter.value !== 'number' || !Number.isFinite(counter.value)) return;
      const key = counter.provider + '/' + counter.name;
      let series = counters.get(key);
      if (!series) {
        if (counters.size >= maxSeries) {
          seriesLimitReached = true;
          qualityElement.textContent = 'Chart limit reached: additional counter series are omitted.';
          return;
        }
        series = { label: counter.displayName || counter.name, unit: counter.unit || '', points: [] };
        counters.set(key, series);
        const option = document.createElement('option');
        option.value = key;
        option.textContent = counter.provider + ' / ' + series.label;
        metricSelect.appendChild(option);
        if (!selectedKey || key.endsWith('/cpu-usage')) {
          selectedKey = key;
          metricSelect.value = key;
        }
      }
      series.points.push({ value: counter.value, timestamp: message.timestamp, receivedAtMs: monotonicNow() });
      evictOldPoints(series);
      if (key === selectedKey) {
        valueElement.textContent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(counter.value) +
          (series.unit ? ' ' + series.unit : '');
      }
      draw();
    }

    // The CLI assigns one shared sequence number across every observation kind in a session (not
    // one per kind), so gap tracking must consume every observation regardless of its kind —
    // otherwise GC pauses between two counter samples look like dropped counter observations.
    function trackSequence(sequence) {
      if (!Number.isFinite(sequence)) return;
      if (sequence > lastSequence + 1 && lastSequence > 0) {
        sequenceGaps += sequence - lastSequence - 1;
      }
      lastSequence = Math.max(lastSequence, sequence);
      if (sequenceGaps > 0) {
        qualityElement.textContent = sequenceGaps + ' observations missing (sequence gaps); gaps are not zero values.';
      }
    }

    // System.TimeSpan serializes via the .NET 8+ constant "c" format, e.g. "00:00:00.0123456".
    // The backslashes below must be doubled (\\\\d etc.) because this whole script is embedded
    // inside an outer TypeScript template literal: a single backslash is consumed as a (no-op)
    // string escape by the outer literal before this text ever reaches the browser's regex engine.
    function parseTimeSpanToMs(value) {
      if (typeof value !== 'string') return NaN;
      const match = /^(?:(\\d+)\\.)?(\\d{2}):(\\d{2}):(\\d{2})(?:\\.(\\d+))?$/.exec(value);
      if (!match) return NaN;
      const days = Number(match[1] || 0);
      const hours = Number(match[2]);
      const minutes = Number(match[3]);
      const seconds = Number(match[4]);
      const fraction = match[5] ? Number('0.' + match[5]) : 0;
      return (((days * 24 + hours) * 60 + minutes) * 60 + seconds + fraction) * 1000;
    }

    function addGcPause(message) {
      const collection = message.collection;
      if (!collection || typeof collection.timestamp !== 'string') return;
      const pauseMs = parseTimeSpanToMs(collection.pauseDuration);
      gcPauseCount += 1;
      if (Number.isFinite(pauseMs)) gcTotalPauseMs += pauseMs;
      const row = document.createElement('tr');
      const cells = [
        new Date(collection.timestamp).toLocaleTimeString(),
        String(collection.generation),
        String(collection.reason ?? ''),
        String(collection.type ?? ''),
        Number.isFinite(pauseMs) ? pauseMs.toFixed(1) + ' ms' : 'unknown',
      ];
      cells.forEach(text => {
        const cell = document.createElement('td');
        cell.textContent = text;
        row.appendChild(cell);
      });
      gcBody.insertBefore(row, gcBody.firstChild);
      gcRows.unshift({ element: row, receivedAtMs: monotonicNow() });
      evictOldGcRows();
      gcHeadline.textContent = gcPauseCount + ' pause(s) observed · last ' +
        (Number.isFinite(pauseMs) ? pauseMs.toFixed(1) + ' ms' : 'unknown') +
        ' · total ' + gcTotalPauseMs.toFixed(1) + ' ms';
    }

    // A capture is a single point-in-time snapshot, not a timeline — it replaces the prior result
    // instead of appending to a list.
    function renderCapture(summary) {
      captureResultElement.classList.remove('empty');
      captureHeadline.textContent = 'PID ' + summary.processId + ' · started ' +
        new Date(summary.startedAt).toLocaleTimeString() + ' · ' + summary.totalSamples + ' sample(s)';
      captureBody.innerHTML = '';
      (summary.topHotspots || []).forEach(hotspot => {
        const row = document.createElement('tr');
        const cells = [
          hotspot.frame && hotspot.frame.module || '',
          hotspot.frame && hotspot.frame.method || '',
          String(hotspot.inclusiveSamples),
          String(hotspot.exclusiveSamples),
        ];
        cells.forEach(text => {
          const cell = document.createElement('td');
          cell.textContent = text;
          row.appendChild(cell);
        });
        captureBody.appendChild(row);
      });
    }

    // Same one-shot replace-not-append semantics as renderCapture, for heap snapshots. 'result'
    // is the trimmed DiagnosticResult projection the CLI streaming protocol emits for a "heap"
    // capture: summary/data (topTypesByBytes, suspendDuration, gcDumpStatus, warnings, quality) -
    // see CliStreamingProtocol.HandleHeapCaptureAsync.
    function renderHeapCapture(source, result) {
      captureHeapResultElement.classList.remove('empty');
      const data = result && result.data;
      const headlineParts = [source === 'gcdump' ? 'GC dump' : 'Live heap', 'snapshot'];
      if (data && typeof data.processId === 'number') headlineParts.push('· PID ' + data.processId);
      if (source === 'live' && data && typeof data.suspendDuration === 'string') {
        const suspendMs = parseTimeSpanToMs(data.suspendDuration);
        headlineParts.push('· suspended ' + (Number.isFinite(suspendMs) ? suspendMs.toFixed(1) + ' ms' : data.suspendDuration));
      }
      if (source === 'gcdump' && data && data.gcDumpStatus) {
        const status = data.gcDumpStatus;
        const flags = [];
        if (status.timedOut) flags.push('timed out');
        if (status.readerFailed) flags.push('reader failed');
        if (flags.length) headlineParts.push('· ' + flags.join(', '));
      }
      if (result && (!data || data.warnings && data.warnings.length)) {
        const warnings = data && data.warnings ? data.warnings : [];
        if (warnings.length) headlineParts.push('· ' + warnings.length + ' warning(s)');
      }
      captureHeapHeadline.textContent = headlineParts.join(' ');
      captureHeapBody.innerHTML = '';
      const topTypes = (data && data.topTypesByBytes) || [];
      topTypes.forEach(typeStat => {
        const row = document.createElement('tr');
        const cells = [
          String(typeStat.typeFullName || ''),
          String(typeStat.instanceCount),
          String(typeStat.totalBytes),
        ];
        cells.forEach(text => {
          const cell = document.createElement('td');
          cell.textContent = text;
          row.appendChild(cell);
        });
        captureHeapBody.appendChild(row);
      });
    }

    const MAX_THREAD_ROWS = 20;

    // Same one-shot replace-not-append semantics as renderCapture/renderHeapCapture, for thread
    // snapshots. 'result' is the trimmed DiagnosticResult projection the CLI streaming protocol
    // emits for a "thread-snapshot" capture: summary/data (threads, locks, totals) - see
    // CliStreamingProtocol.HandleThreadSnapshotCaptureAsync. This is a summary-first panel (not a
    // full stack-dump viewer), so the table is capped at MAX_THREAD_ROWS even though the CLI's own
    // bounded projection already keeps the wire payload small.
    function renderThreadCapture(result) {
      captureThreadResultElement.classList.remove('empty');
      const data = result && result.data;
      const headlineParts = ['Thread snapshot'];
      if (data && typeof data.processId === 'number') headlineParts.push('· PID ' + data.processId);
      if (data && typeof data.totalThreads === 'number') headlineParts.push('· ' + data.totalThreads + ' thread(s)');
      if (data && typeof data.totalLocks === 'number' && data.totalLocks > 0) {
        headlineParts.push('· ' + data.totalLocks + ' lock(s)');
      }
      if (data && typeof data.omittedThreads === 'number' && data.omittedThreads > 0) {
        headlineParts.push('· ' + data.omittedThreads + ' thread(s) omitted inline');
      }
      captureThreadHeadline.textContent = headlineParts.join(' ');
      captureThreadBody.innerHTML = '';
      const threads = (data && data.threads) || [];
      threads.slice(0, MAX_THREAD_ROWS).forEach(thread => {
        const row = document.createElement('tr');
        const topFrame = Array.isArray(thread.frames) && thread.frames.length > 0 ? thread.frames[0] : '';
        const cells = [
          String(thread.managedThreadId),
          String(thread.state || '') + (thread.isLikelyBlocked ? ' (blocked)' : ''),
          String(thread.inferredWaitReason || ''),
          String(topFrame),
        ];
        cells.forEach(text => {
          const cell = document.createElement('td');
          cell.textContent = text;
          row.appendChild(cell);
        });
        captureThreadBody.appendChild(row);
      });
    }

    // Friendly labels for the 11 follow-up drilldown views (issue #1116). 'deadlocks' is called
    // out here only for its title; the visual prominence (border/color, open-by-default) is driven
    // by the 'query-view-deadlocks' CSS class applied in renderQueryView below.
    const QUERY_VIEW_LABELS = {
      'roots-by-kind': 'GC roots by kind',
      'finalizer-queue': 'Finalizer queue',
      'fragmentation': 'Heap fragmentation',
      'gchandles': 'GC handles',
      'async': 'Async state machines',
      'timers': 'Timers',
      'alc': 'AssemblyLoadContexts',
      'static-fields': 'Static fields',
      'delegate-targets': 'Delegate targets',
      'retention-paths': 'Retention paths',
      'retained-exceptions': 'Retained exceptions',
      'deadlocks': 'Deadlocks',
      'unique-stacks': 'Unique stacks',
      'wait-chains': 'Wait chains',
      'threadpool': 'Thread pool',
      'thread-statics': 'Thread statics',
    };

    // Renders one follow-up 'query' drilldown result (or its error) as a collapsible details
    // section inside the given container. This is a deliberately generic JSON-dump renderer rather
    // than 11 bespoke per-view tables: the view result shapes come directly from
    // HeapSnapshotQueryDispatcher/ThreadSnapshotQueryDispatcher and are already structured for
    // human/LLM reading, so a formatted JSON block is a reasonable first cut. 'deadlocks' gets
    // extra visual weight (open by default, distinct border/color) since it's the single most
    // actionable output of this feature - an inferred wait-for cycle.
    function renderQueryView(containerId, view, resultOrMessage, isError) {
      const container = document.getElementById(containerId);
      if (!container) return;
      const elementId = containerId + '-' + view;
      let details = document.getElementById(elementId);
      if (!details) {
        details = document.createElement('details');
        details.id = elementId;
        details.className = 'query-view';
        if (view === 'deadlocks') {
          details.open = true;
          details.classList.add('query-view-deadlocks');
        }
        const summary = document.createElement('summary');
        summary.textContent = QUERY_VIEW_LABELS[view] || view;
        const pre = document.createElement('pre');
        details.appendChild(summary);
        details.appendChild(pre);
        container.appendChild(details);
      }
      const summary = details.querySelector('summary');
      const pre = details.querySelector('pre');
      details.classList.toggle('query-view-error', !!isError);
      if (isError) {
        summary.textContent = (QUERY_VIEW_LABELS[view] || view) + ' (unavailable)';
        pre.textContent = String(resultOrMessage);
      } else {
        summary.textContent = QUERY_VIEW_LABELS[view] || view;
        pre.textContent = JSON.stringify(resultOrMessage, null, 2);
      }
    }

    // The thread-statics view needs an exact type name, so it is requested on demand rather than
    // auto-queried; the extension host validates the message again.
    threadStaticsButton.addEventListener('click', () => {
      const typeFilter = threadStaticsInput.value.trim();
      if (!typeFilter) {
        threadStaticsError.textContent = 'Enter an exact type name.';
        return;
      }
      threadStaticsError.textContent = '';
      vscode.postMessage({ type: 'queryThreadStatics', typeFilter });
    });

    window.addEventListener('message', event => {
      const message = event.data;
      if (!message || typeof message.type !== 'string') return;
      if (message.type === 'status') {
        statusElement.textContent = message.message || message.state;
        errorElement.textContent = message.state === 'error' ? (message.message || '') : '';
        updateButtons(message.state === 'running' || message.state === 'starting' || message.state === 'stopping');
      } else if (message.type === 'observation') {
        trackSequence(message.sequence);
        if (message.kind === 'gc') {
          addGcPause(message);
        } else {
          addCounter(message);
        }
      } else if (message.type === 'captureStatus' && message.kind === 'heap') {
        captureHeapStatusElement.textContent = message.message || message.state || '';
        captureHeapButton.disabled = message.state === 'running';
        if (message.state === 'running') {
          // Clear any query-view sections left over from a previous capture so a stale
          // opt-in/gcdump-origin section can't be mistaken for data from the capture in flight.
          document.getElementById('heapQueryViews').innerHTML = '';
        }
      } else if (message.type === 'captureStatus' && message.kind === 'thread-snapshot') {
        captureThreadStatusElement.textContent = message.message || message.state || '';
        captureThreadSnapshotButton.disabled = message.state === 'running';
        if (message.state === 'running') {
          document.getElementById('threadQueryViews').innerHTML = '';
        }
      } else if (message.type === 'captureStatus') {
        captureStatusElement.textContent = message.message || message.state || '';
        captureCpuButton.disabled = message.state === 'running';
      } else if (message.type === 'capture' && message.kind === 'heap') {
        renderHeapCapture(message.source, message.result);
      } else if (message.type === 'capture' && message.kind === 'thread-snapshot') {
        renderThreadCapture(message.result);
      } else if (message.type === 'capture') {
        renderCapture(message.summary);
      } else if (message.type === 'query' && message.kind === 'heap') {
        renderQueryView('heapQueryViews', message.view, message.result, false);
      } else if (message.type === 'query' && message.kind === 'thread-snapshot') {
        renderQueryView('threadQueryViews', message.view, message.result, false);
      } else if (message.type === 'queryError' && message.kind === 'heap') {
        renderQueryView('heapQueryViews', message.view, message.message, true);
      } else if (message.type === 'queryError' && message.kind === 'thread-snapshot') {
        renderQueryView('threadQueryViews', message.view, message.message, true);
      } else if (message.type === 'historyDuration') {
        if (typeof message.seconds === 'number' && Number.isFinite(message.seconds) && message.seconds > 0) {
          historyDurationMs = message.seconds * 1000;
          counters.forEach(evictOldPoints);
          evictOldGcRows();
          draw();
        }
      } else if (message.type === 'terminal') {
        updateButtons(false);
        const loss = typeof message.eventPipeEventsLost === 'number'
          ? message.eventPipeEventsLost + ' EventPipe events lost'
          : 'EventPipe loss count unavailable';
        const dropped = typeof message.droppedObservations === 'number'
          ? message.droppedObservations + ' observations dropped by the bounded queue'
          : 'dropped-observation count unavailable';
        qualityElement.textContent = [
          loss,
          dropped,
          sequenceGaps ? sequenceGaps + ' sequence gaps' : 'no sequence gaps reported',
          seriesLimitReached ? 'additional counter series omitted by chart limit' : '',
        ].filter(Boolean).join(' · ');
        statusElement.textContent = message.status === 'targetExited'
          ? 'Target process exited.'
          : message.status === 'failed' ? 'Capture failed.' : 'Capture stopped.';
        if (typeof message.error === 'string' && message.error.length > 0) errorElement.textContent = message.error;
      }
    });

    metricSelect.addEventListener('change', () => {
      selectedKey = metricSelect.value;
      const series = counters.get(selectedKey);
      const latest = series && series.points[series.points.length - 1];
      valueElement.textContent = latest
        ? new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(latest.value) + (series.unit ? ' ' + series.unit : '')
        : 'Waiting for first observation';
      draw();
    });
    startButton.addEventListener('click', () => vscode.postMessage({ type: 'start' }));
    stopButton.addEventListener('click', () => vscode.postMessage({ type: 'stop' }));
    captureCpuButton.addEventListener('click', () => vscode.postMessage({ type: 'captureCpu' }));
    captureHeapButton.addEventListener('click', () => vscode.postMessage({ type: 'captureHeap' }));
    captureThreadSnapshotButton.addEventListener('click', () => vscode.postMessage({ type: 'captureThreadSnapshot' }));
    window.addEventListener('resize', resizeCanvas);
    resizeCanvas();
    updateButtons(false);
    // Keeps the rolling history window sliding forward in wall-clock time even if the stream goes
    // quiet for a while, instead of only evicting stale points/rows when fresh data arrives.
    setInterval(() => {
      let changed = false;
      counters.forEach(series => {
        const before = series.points.length;
        evictOldPoints(series);
        if (series.points.length !== before) changed = true;
      });
      const beforeGcCount = gcRows.length;
      evictOldGcRows();
      if (gcRows.length !== beforeGcCount) changed = true;
      if (changed) draw();
    }, 1000);
    vscode.postMessage({ type: 'ready' });
  </script>

</body>
</html>`;
}

/** Shared with `DumpAnalysisPanelController` so both live-counters and dump-analysis connections resolve the CLI path identically. */
function resolveCliPath(): string {
    const configured = vscode.workspace
        .getConfiguration("dotnetDiagnostics")
        .get<string>("cliPath", "dotnet-diagnostics-cli")
        .trim();
    return configured || "dotnet-diagnostics-cli";
}

/**
 * Spawns `dotnet-diagnostics-cli stream --protocol jsonl`. Shared by `CounterPanelController`
 * (live counters + its capture-only reuse path) and the dump-analysis flow below, which opens its
 * own short-lived connection without any `TargetProcess`/live-session state.
 */
function spawnStreamingCli(cliPath: string): ChildProcessWithoutNullStreams {
    try {
        return spawn(cliPath, ["stream", "--protocol", "jsonl"], {
            stdio: ["pipe", "pipe", "pipe"],
            windowsHide: true,
            shell: false,
        });
    } catch (error) {
        throw new Error(`Could not launch '${cliPath}': ${errorMessage(error)}`);
    }
}

/**
 * Renders the dedicated "Analyze Dump File" webview (#1114): a standalone page (no counters
 * chart, no start/stop controls, no `TargetProcess`) with two result panels — heap top-types and
 * thread/lock snapshot — populated once both dump-sourced captures complete. Visually consistent
 * with (but intentionally not code-shared with) `renderHtml`'s heap/thread capture panels, because
 * `DumpHeapCaptureResult`'s shape (`filePath`/`fileSizeBytes`, no `processId`/`suspendDuration`/
 * `gcDumpStatus`) differs from the live `HeapCaptureResult` the counters panel renders.
 */
export function renderDumpAnalysisHtml(dumpFileName: string, nonce: string): string {
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Dump analysis</title>
  <style nonce="${nonce}">
    body { color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); padding: 0 1.2rem; }
    header h2 { margin-bottom: .2rem; }
    #error { color:var(--vscode-errorForeground); white-space:pre-wrap; }
    section.panel { margin-top:1.4rem; }
    section.panel h3 { margin-bottom:.3rem; }
    table { border-collapse:collapse; width:100%; font-size:.9rem; }
    th, td { text-align:left; padding:.25rem .6rem; border-bottom:1px solid var(--vscode-panel-border); }
    .empty { display:none; }
    .query-views { margin-top:.8rem; }
    details.query-view { border:1px solid var(--vscode-panel-border); border-radius:3px; margin-bottom:.5rem; }
    .statics-form { margin:.5rem 0; display:flex; gap:.5rem; align-items:center; flex-wrap:wrap; }
    .statics-form input { min-width:20rem; }
    .statics-error { color: var(--vscode-errorForeground); }
    details.query-view > summary { cursor:pointer; padding:.4rem .6rem; font-weight:600; }
    details.query-view[open] > summary { border-bottom:1px solid var(--vscode-panel-border); }
    details.query-view pre { margin:0; padding:.6rem; white-space:pre-wrap; word-break:break-word; font-size:.85rem; max-height:360px; overflow:auto; }
    details.query-view.query-view-error > summary { color:var(--vscode-errorForeground); }
    details.query-view.query-view-deadlocks { border-color:var(--vscode-errorForeground); border-width:2px; }
    details.query-view.query-view-deadlocks > summary { color:var(--vscode-errorForeground); font-size:1.05rem; }
    .status { color:var(--vscode-descriptionForeground); margin:.3rem 0; }
  </style>
</head>
<body>
  <header>
    <h2>Dump analysis</h2>
    <div>${escapeHtml(dumpFileName)}</div>
  </header>
  <div id="error" role="alert"></div>

  <section class="panel">
    <h3>Heap snapshot</h3>
    <div id="captureHeapStatus" class="status"></div>
    <div id="captureHeapResult" class="empty">
      <div id="captureHeapHeadline"></div>
      <table id="captureHeapTable">
        <thead><tr><th>Type</th><th>Instances</th><th>Bytes</th></tr></thead>
        <tbody id="captureHeapBody"></tbody>
      </table>
      <div id="heapQueryViews" class="query-views"></div>
    </div>
  </section>

  <section class="panel">
    <h3>Thread snapshot</h3>
    <div id="captureThreadStatus" class="status"></div>
    <div id="captureThreadResult" class="empty">
      <div id="captureThreadHeadline"></div>
      <table id="captureThreadTable">
        <thead><tr><th>Thread</th><th>State</th><th>Wait reason</th><th>Top frame</th></tr></thead>
        <tbody id="captureThreadBody"></tbody>
      </table>
      <div class="statics-form">
        <input id="threadStaticsType" type="text" maxlength="512" placeholder="Exact type name for thread statics, e.g. MyApp.Cache" />
        <button id="threadStaticsButton" type="button">Query thread statics</button>
        <span id="threadStaticsError" class="statics-error"></span>
      </div>
      <div id="threadQueryViews" class="query-views"></div>
    </div>
  </section>

  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const errorElement = document.getElementById('error');
    const captureHeapStatusElement = document.getElementById('captureHeapStatus');
    const captureHeapResultElement = document.getElementById('captureHeapResult');
    const captureHeapHeadline = document.getElementById('captureHeapHeadline');
    const captureHeapBody = document.getElementById('captureHeapBody');
    const captureThreadStatusElement = document.getElementById('captureThreadStatus');
    const threadStaticsInput = document.getElementById('threadStaticsType');
    const threadStaticsButton = document.getElementById('threadStaticsButton');
    const threadStaticsError = document.getElementById('threadStaticsError');
    const captureThreadResultElement = document.getElementById('captureThreadResult');
    const captureThreadHeadline = document.getElementById('captureThreadHeadline');
    const captureThreadBody = document.getElementById('captureThreadBody');

    // 'result' is the trimmed DiagnosticResult<DumpInspection> projection the CLI streaming
    // protocol emits for a "heap" capture with source "dump" - see
    // CliStreamingProtocol.HandleHeapCaptureAsync's dump branch. Unlike the live counters panel's
    // renderHeapCapture, there is no processId/suspendDuration/gcDumpStatus to show; instead the
    // dump file path and size identify what was analyzed.
    function renderDumpHeapCapture(result) {
      captureHeapResultElement.classList.remove('empty');
      const data = result && result.data;
      const headlineParts = ['Heap snapshot from dump'];
      if (data && typeof data.fileSizeBytes === 'number') {
        headlineParts.push('· ' + (data.fileSizeBytes / (1024 * 1024)).toFixed(1) + ' MB');
      }
      const warnings = (data && data.warnings) || [];
      if (warnings.length) headlineParts.push('· ' + warnings.length + ' warning(s)');
      captureHeapHeadline.textContent = headlineParts.join(' ');
      captureHeapBody.innerHTML = '';
      const topTypes = (data && data.topTypesByBytes) || [];
      topTypes.forEach(typeStat => {
        const row = document.createElement('tr');
        const cells = [
          String(typeStat.typeFullName || ''),
          String(typeStat.instanceCount),
          String(typeStat.totalBytes),
        ];
        cells.forEach(text => {
          const cell = document.createElement('td');
          cell.textContent = text;
          row.appendChild(cell);
        });
        captureHeapBody.appendChild(row);
      });
    }

    const MAX_THREAD_ROWS = 20;

    // 'result' is the trimmed DiagnosticResult<ThreadSnapshotQueryResult> projection the CLI
    // streaming protocol emits for a "thread-snapshot" capture with dumpFile set - see
    // CliStreamingProtocol.HandleThreadSnapshotCaptureAsync. ThreadSnapshotQueryResult's shape is
    // identical regardless of live vs. dump origin (only data.origin differs), so this mirrors the
    // live counters panel's renderThreadCapture.
    function renderDumpThreadCapture(result) {
      captureThreadResultElement.classList.remove('empty');
      const data = result && result.data;
      const headlineParts = ['Thread snapshot from dump'];
      if (data && typeof data.totalThreads === 'number') headlineParts.push('· ' + data.totalThreads + ' thread(s)');
      if (data && typeof data.totalLocks === 'number' && data.totalLocks > 0) {
        headlineParts.push('· ' + data.totalLocks + ' lock(s)');
      }
      if (data && typeof data.omittedThreads === 'number' && data.omittedThreads > 0) {
        headlineParts.push('· ' + data.omittedThreads + ' thread(s) omitted inline');
      }
      captureThreadHeadline.textContent = headlineParts.join(' ');
      captureThreadBody.innerHTML = '';
      const threads = (data && data.threads) || [];
      threads.slice(0, MAX_THREAD_ROWS).forEach(thread => {
        const row = document.createElement('tr');
        const topFrame = Array.isArray(thread.frames) && thread.frames.length > 0 ? thread.frames[0] : '';
        const cells = [
          String(thread.managedThreadId),
          String(thread.state || '') + (thread.isLikelyBlocked ? ' (blocked)' : ''),
          String(thread.inferredWaitReason || ''),
          String(topFrame),
        ];
        cells.forEach(text => {
          const cell = document.createElement('td');
          cell.textContent = text;
          row.appendChild(cell);
        });
        captureThreadBody.appendChild(row);
      });
    }

    // See renderHtml's identical QUERY_VIEW_LABELS/renderQueryView for the full rationale; kept as
    // a duplicate copy here since this is a separate self-contained webview template/script.
    const QUERY_VIEW_LABELS = {
      'roots-by-kind': 'GC roots by kind',
      'finalizer-queue': 'Finalizer queue',
      'fragmentation': 'Heap fragmentation',
      'gchandles': 'GC handles',
      'async': 'Async state machines',
      'timers': 'Timers',
      'alc': 'AssemblyLoadContexts',
      'static-fields': 'Static fields',
      'delegate-targets': 'Delegate targets',
      'retention-paths': 'Retention paths',
      'retained-exceptions': 'Retained exceptions',
      'deadlocks': 'Deadlocks',
      'unique-stacks': 'Unique stacks',
      'wait-chains': 'Wait chains',
      'threadpool': 'Thread pool',
      'thread-statics': 'Thread statics',
    };

    function renderQueryView(containerId, view, resultOrMessage, isError) {
      const container = document.getElementById(containerId);
      if (!container) return;
      const elementId = containerId + '-' + view;
      let details = document.getElementById(elementId);
      if (!details) {
        details = document.createElement('details');
        details.id = elementId;
        details.className = 'query-view';
        if (view === 'deadlocks') {
          details.open = true;
          details.classList.add('query-view-deadlocks');
        }
        const summary = document.createElement('summary');
        summary.textContent = QUERY_VIEW_LABELS[view] || view;
        const pre = document.createElement('pre');
        details.appendChild(summary);
        details.appendChild(pre);
        container.appendChild(details);
      }
      const summary = details.querySelector('summary');
      const pre = details.querySelector('pre');
      details.classList.toggle('query-view-error', !!isError);
      if (isError) {
        summary.textContent = (QUERY_VIEW_LABELS[view] || view) + ' (unavailable)';
        pre.textContent = String(resultOrMessage);
      } else {
        summary.textContent = QUERY_VIEW_LABELS[view] || view;
        pre.textContent = JSON.stringify(resultOrMessage, null, 2);
      }
    }

    // The thread-statics view needs an exact type name, so it is requested on demand rather than
    // auto-queried; the extension host validates the message again.
    threadStaticsButton.addEventListener('click', () => {
      const typeFilter = threadStaticsInput.value.trim();
      if (!typeFilter) {
        threadStaticsError.textContent = 'Enter an exact type name.';
        return;
      }
      threadStaticsError.textContent = '';
      vscode.postMessage({ type: 'queryThreadStatics', typeFilter });
    });

    window.addEventListener('message', event => {
      const message = event.data;
      if (!message || typeof message.type !== 'string') return;
      if (message.type === 'captureStatus' && message.kind === 'heap') {
        captureHeapStatusElement.textContent = message.message || message.state || '';
        if (message.state === 'running') {
          document.getElementById('heapQueryViews').innerHTML = '';
        }
      } else if (message.type === 'captureStatus' && message.kind === 'thread-snapshot') {
        captureThreadStatusElement.textContent = message.message || message.state || '';
        if (message.state === 'running') {
          document.getElementById('threadQueryViews').innerHTML = '';
        }
      } else if (message.type === 'capture' && message.kind === 'heap') {
        renderDumpHeapCapture(message.result);
      } else if (message.type === 'capture' && message.kind === 'thread-snapshot') {
        renderDumpThreadCapture(message.result);
      } else if (message.type === 'query' && message.kind === 'heap') {
        renderQueryView('heapQueryViews', message.view, message.result, false);
      } else if (message.type === 'query' && message.kind === 'thread-snapshot') {
        renderQueryView('threadQueryViews', message.view, message.result, false);
      } else if (message.type === 'queryError' && message.kind === 'heap') {
        renderQueryView('heapQueryViews', message.view, message.message, true);
      } else if (message.type === 'queryError' && message.kind === 'thread-snapshot') {
        renderQueryView('threadQueryViews', message.view, message.message, true);
      }
    });
  </script>
</body>
</html>`;
}

function deferred<T>(): Deferred<T> {
    let resolvePromise!: (value: T) => void;
    let rejectPromise!: (error: Error) => void;
    const result: Deferred<T> = {
        promise: new Promise<T>((resolve, reject) => {
            resolvePromise = resolve;
            rejectPromise = reject;
        }),
        resolve: value => {
            if (!result.settled) {
                result.settled = true;
                resolvePromise(value);
            }
        },
        reject: error => {
            if (!result.settled) {
                result.settled = true;
                rejectPromise(error);
            }
        },
        settled: false,
    };
    void result.promise.catch(() => undefined);
    return result;
}

async function withTimeout<T>(promise: Promise<T>, timeoutMilliseconds: number): Promise<T> {
    let timer: NodeJS.Timeout | undefined;
    try {
        return await Promise.race([
            promise,
            new Promise<T>((_, reject) => {
                timer = setTimeout(() => reject(new Error(`Operation timed out after ${timeoutMilliseconds}ms.`)), timeoutMilliseconds);
            }),
        ]);
    } finally {
        if (timer) {
            clearTimeout(timer);
        }
    }
}

function isCounterValue(value: unknown): value is CounterValue {
    return isRecord(value)
        && typeof value.provider === "string"
        && typeof value.name === "string"
        && typeof value.value === "number";
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null;
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

/**
 * Risk-acknowledgement wording shown in the modal before a heap-snapshot capture is sent. Mirrors
 * the exact descriptor text Core's `InvocationSafetyRegistry.InspectHeapProfile` uses for
 * `inspect-heap --source live`/`--source gcdump` (both High risk / Acknowledge, unlike CPU
 * sampling's Moderate risk) so CLI/MCP users see consistent risk framing in the extension.
 */
function describeHeapCaptureRisk(source: HeapCaptureSource, label: string): string {
    const impact = source === "live"
        ? "A live ClrMD heap walk attaches with ptrace, suspends the target, and exposes heap type and object-graph metadata. This may expose possibly confidential data."
        : "GC dump capture induces a managed GC and exposes aggregate heap type metadata. This may expose possibly confidential data.";
    return `Capture heap snapshot (${label})? ${impact} This is a high-risk operation that requires explicit acknowledgement before it runs against the selected process.`;
}

function describeThreadCaptureRisk(): string {
    return "Capture thread snapshot? A live ClrMD thread walk attaches with ptrace, briefly suspends the target, "
        + "and exposes stack, type, and method names. This may expose possibly confidential data. This is a "
        + "high-risk operation that requires explicit acknowledgement before it runs against the selected process.";
}

function withRuntimeGuidance(message: string): string {
    if (/protocol mismatch|protocol version/i.test(message)) {
        return `${message} Update dotnet-diagnostics-cli to a version compatible with this extension.`;
    }
    if (/unsupported_capture_kind|capture kind '(heap|thread-snapshot)' is not supported/i.test(message)) {
        return `${message} Update dotnet-diagnostics-cli to a version that supports this capture kind.`;
    }
    if (/ENOENT|not found|cannot find the file/i.test(message)) {
        return `${message}\nInstall dotnet-diagnostics-cli explicitly with 'dotnet tool install -g dotnet-diagnostics-cli', or set the machine-scoped dotnetDiagnostics.cliPath.`;
    }
    if (/hostfxr|framework.*not found|runtime.*not found|You must install or update/i.test(message)) {
        return `${message}\nThe framework-dependent CLI requires a compatible .NET 10 runtime. Install the runtime or configure a self-contained CLI executable.`;
    }
    return message;
}

function escapeHtml(value: string): string {
    return value.replace(/[&<>"']/g, character => ({
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        "\"": "&quot;",
        "'": "&#39;",
    })[character]!);
}
