import { randomBytes } from "node:crypto";
import { execFile as execFileCallback, spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { basename } from "node:path";
import { createInterface, type Interface as ReadlineInterface } from "node:readline";
import { promisify } from "node:util";
import * as vscode from "vscode";
import { ResultExporter, RESULT_EXPORT_BUTTONS, RESULT_EXPORT_SCRIPT, openShareable } from "./resultExport";
import { ADDRESS_NAVIGATION_HTML, AddressNavigationController, type AddressNavigationHost } from "./addressNavigation";
import { registerOnboarding } from "./onboarding";
import { QUERY_VIEW_RENDERER_SCRIPT, QUERY_VIEW_STYLES } from "./queryViewRenderer";
import { ProcessTree, type ProcessAction, type ProcessRow } from "./processTree";
import type { ProcessEntry } from "./processHistory";
import { CaptureHistory, type HistoryRow } from "./captureHistory";
import { CAPTURE_VIEW_RENDERER_SCRIPT } from "./captureViewRenderer";
import { CPU_PROFILE_SCRIPT, CPU_PROFILE_STYLES } from "./cpuProfileViewer";
import { WEBVIEW_FIELD_STYLES } from "./webviewFields";
import { LIVE_TIME_NAVIGATION_SCRIPT } from "./liveTimeNavigation";
import { LiveReadConsent, LIVE_READ_CONSENT_HTML, LIVE_READ_CONSENT_SCRIPT } from "./liveReadConsent";
import { ACTION_AVAILABILITY_SCRIPT, ACTION_AVAILABILITY_STYLES, PROFILE_ACTIONS_SCRIPT } from "./actionAvailability";
import {
    BoundedProtocolLineReader,
    LIVE_EVENT_BATCH_MAX_BYTES,
    LIVE_EVENT_CONTROL_LINE_MAX_BYTES,
    LIVE_EVENT_FRAME_MAX_BYTES,
    LIVE_EVENT_MAX_UPDATES_PER_SECOND,
    LIVE_EVENT_OBSERVATION_CAPACITY,
    LIVE_EVENT_SEARCH_MAX_CHARS,
    LIVE_EVENT_UI_MAX_COUNT,
    LIVE_EVENT_UI_MAX_MOUNTED_ROWS,
    LIVE_EVENT_UI_MAX_RETAINED_BYTES,
    LiveEventDeliveryQueue,
    isLiveEventObservation,
} from "./liveEvents";
import {
    CAPTURE_GROUPS, CAPTURE_KIND_SPECS, buildCaptureRequest, captureTimeoutMs, isCaptureViewKind, isCaptureViewResult,
    parseSources, validateInteger, validateSymbolPath,
    type CaptureKindSpec, type CaptureParams, type CaptureViewKind, type CaptureViewResult,
} from "./captureKinds";
import {
    describeStreamCompatibilityError,
    describeStreamExit,
    isGcCollection,
    isCpuSampleSummary,
    isExceptionCaptureResult,
    isDumpHeapCaptureResult,
    isHeapCaptureResult,
    isQueryResult,
    isThreadCaptureResult,
    buildQueryFrame,
    type QueryNavigation,
    parseProcessList,
    parseTypeFilter,
    SingleFlightGuard,
    parseProtocolFrame,
    type CounterValue,
    type CpuSampleSummary,
    type ExceptionCaptureResult,
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
const CPU_QUERY_VIEWS = new Set([
    "triage", "call-tree", "top-methods", "hot-path", "by-module", "by-namespace", "caller-callee",
]);

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
const PROTOCOL_VERSION = 2;
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
    | { kind: "cpu"; evidenceId: string; exportTrace?: boolean; deferred: Deferred<CpuSampleSummary> }
    | { kind: "exceptions" | "crash-guard"; deferred: Deferred<ExceptionCaptureResult> }
    | { kind: "heap"; evidenceId: string; deferred: Deferred<HeapCaptureResult> }
    | { kind: "thread-snapshot"; deferred: Deferred<ThreadCaptureResult> }
    | { kind: "diagnostics"; captureKind: CaptureViewKind; deferred: Deferred<CaptureViewResult> };

/** Heap snapshot source the user selects before a "Capture Heap Snapshot" request (see #1110). */
type HeapCaptureSource = "live" | "gcdump";


interface StreamChild {
    process: ChildProcessWithoutNullStreams;
    lines: ReadlineInterface | BoundedProtocolLineReader;
    handshake: Deferred<void>;
    protocolReady: boolean;
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
    eventsConnection: boolean;
    supportedLiveKinds?: string[];
}

class CounterPanelController implements vscode.Disposable {
    private readonly exporter = new ResultExporter();
    private cpuExport?: { session: StreamChild; handle: string; evidenceId: string; trace?: boolean };
    public liveCountersChanged?: (active: boolean) => void;
    private allocationProfile?: { session: StreamChild; handle: string; evidenceId: string };
    private readonly pendingExports = new Map<string, { session: StreamChild; deferred: Deferred<unknown> }>();
    private exporting = false;
    private panel?: vscode.WebviewPanel;
    private panelReady?: Deferred<void>;
    private selectedTarget?: TargetProcess;
    private activeChild?: StreamChild;
    private eventsChild?: StreamChild;
    private eventsConnectTask?: Promise<StreamChild>;
    private eventsStartTask?: Promise<void>;
    private eventsDisposalTask?: Promise<void>;
    private eventsQueue = new LiveEventDeliveryQueue();
    private eventsStreamSessionId?: string;
    private eventsQuality?: {
        eventPipeEventsLost: number | null;
        eventPipeLossAvailable: boolean;
        coreQueueDropped: number;
        cliDispatchQueueDropped: number;
    };
    private eventsQualityRevision = 0;
    private eventsQualityAcknowledgedRevision = 0;
    private eventsBatchTimer?: NodeJS.Timeout;
    private eventsLastBatchAt = 0;
    private eventsLastQualityAt = 0;
    private eventsQualityFinal = false;
    private eventsInFlightQualityRevision?: number;
    private eventsGeneration = 0;
    private readonly statusBar: vscode.StatusBarItem;
    private disposed = false;
    private shutdownTask?: Promise<void>;
    private childDisposalTask?: Promise<void>;
    private startCommandTask?: Promise<void>;
    private startStreamTask?: Promise<void>;
    private captureCpuTask?: Promise<void>;
    private captureExceptionsTask?: Promise<void>;
    private captureDiagnosticsTask?: Promise<void>;
    private captureHeapTask?: Promise<void>;
    private captureThreadSnapshotTask?: Promise<void>;
    /**
     * The most recent thread snapshot handle and the connection that owns it. Handles live in the
     * CLI process, so the connection is kept open after a thread capture (until it is closed by a
     * later capture, a stop, or disposal) to allow on-demand `thread-statics` queries.
     */
    private threadSnapshot?: { session: StreamChild; handle: string; evidenceId: string };
    private readonly threadStaticsGuard = new SingleFlightGuard();
    private readonly cpuQueryGuard = new SingleFlightGuard();
    /**
     * The live heap snapshot whose handle address navigation queries (issue #1168). The owning
     * connection stays open (like `threadSnapshot`) because handles live in the CLI process.
     */
    private heapSnapshot?: { session: StreamChild; handle: string; evidenceId: string };
    private readonly liveReadConsent = new LiveReadConsent(state => this.postMessage(state));
    private readonly addressNavigation = new AddressNavigationController(this.createAddressNavigationHost());
    private connectChildTask?: Promise<StreamChild>;
    private lastRecoveryNotice?: { message: string; at: number };

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

    public startEvents(): Promise<void> {
        if (!this.eventsStartTask) {
            this.eventsStartTask = this.startEventsCore().finally(() => {
                this.eventsStartTask = undefined;
            });
        }
        return this.eventsStartTask;
    }

    private async startEventsCore(): Promise<void> {
        const generation = this.eventsGeneration;
        if (this.disposed) return;
        if (this.eventsChild?.sessionId && !this.eventsChild.terminalReceived) {
            this.panel?.reveal(vscode.ViewColumn.Active);
            return;
        }

        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) return;

        const providersInput = await vscode.window.showInputBox({
            prompt: "Explicit EventSource provider names, comma-separated (1-8; provider and keyword choices affect event volume)",
            value: "System.Runtime",
            ignoreFocusOut: true,
        });
        if (providersInput === undefined || !this.isEventsStartCurrent(generation)) return;
        const providers = providersInput.split(",").map(name => name.trim()).filter(Boolean);
        const uniqueProviders = new Set(providers.map(name => name.toLocaleLowerCase("en-US")));
        if (providers.length < 1 || providers.length > 8
            || uniqueProviders.size !== providers.length
            || providers.some(name => name.length > 256 || /[\u0000-\u001f\u007f]/.test(name))) {
            void vscode.window.showErrorMessage("Choose 1-8 unique provider names, each no longer than 256 characters.");
            return;
        }

        const levelInput = await vscode.window.showInputBox({
            prompt: "EventSource level 0-5 (4 = Informational)",
            value: "4",
            validateInput: value => /^[0-5]$/.test(value) ? undefined : "Enter an integer from 0 to 5.",
            ignoreFocusOut: true,
        });
        if (levelInput === undefined || !this.isEventsStartCurrent(generation)) return;
        const eventLevel = Number(levelInput);

        const keywords = await vscode.window.showInputBox({
            prompt: "Signed 64-bit decimal keyword mask (0 enables no keyword bits; -1 can enable high-volume events)",
            value: "0",
            validateInput: value => isSignedInt64(value)
                ? undefined
                : "Enter a signed 64-bit decimal integer (the value is sent losslessly as text).",
            ignoreFocusOut: true,
        });
        if (keywords === undefined || !this.isEventsStartCurrent(generation)) return;

        const acknowledged = await vscode.window.showWarningMessage(
            "Live Events opens a continuous EventPipe/EventSource stream for the selected providers. "
            + "Event payloads and exception messages may contain secrets or personal data. They remain "
            + "in bounded in-memory windows and are not automatically saved or logged. Native Windows ETW "
            + "and system-wide providers are not included. Broader providers or keyword masks can increase "
            + "target/runtime work and event volume.",
            { modal: true },
            "Acknowledge and Start",
            "Cancel",
        );
        if (acknowledged !== "Acknowledge and Start" || !this.isEventsStartCurrent(generation)) return;
        if (this.selectedTarget !== target) {
            this.postEventsMessage({
                type: "eventsStatus",
                state: "stopped",
                message: "The selected process changed before Live Events could start.",
            });
            return;
        }

        if (this.eventsQueue.hasInFlightBatch || this.eventsQueue.retainedEventCount > 0
            || this.eventsQualityRevision > this.eventsQualityAcknowledgedRevision) {
            this.scheduleEventsBatch();
            this.postEventsMessage({
                type: "eventsStatus",
                state: "stopped",
                message: "Wait for the previous Live Events batch and quality update to be acknowledged before starting another stream.",
            });
            return;
        }

        this.eventsQueue = new LiveEventDeliveryQueue();
        this.eventsStreamSessionId = undefined;
        this.eventsQuality = undefined;
        this.eventsQualityRevision = 0;
        this.eventsQualityAcknowledgedRevision = 0;
        this.eventsInFlightQualityRevision = undefined;
        this.eventsLastQualityAt = 0;
        this.eventsQualityFinal = false;
        this.postEventsMessage({ type: "eventsStatus", state: "starting", message: "Starting EventPipe EventSource stream…" });

        let session: StreamChild;
        try {
            session = await this.connectEventsChild();
            if (!this.isEventsStartCurrent(generation) || this.selectedTarget !== target) {
                session.expectedShutdown = true;
                await this.closeChild(session);
                if (this.eventsChild === session) this.eventsChild = undefined;
                return;
            }
            this.eventsChild = session;
            if (session.started.settled) {
                session.started = deferred<void>();
                session.terminal = deferred<void>();
                session.terminalReceived = false;
                session.expectedShutdown = false;
            }
            const requestId = randomBytes(12).toString("hex");
            session.startInFlight = true;
            this.writeFrame(session, {
                type: "start",
                requestId,
                processId: target.processId,
                kinds: [{
                    kind: "event_source",
                    providers: providers.map(name => ({ name, eventLevel, keywords })),
                    observationCapacity: LIVE_EVENT_OBSERVATION_CAPACITY,
                    acknowledgeRisk: "high",
                }],
            });
            await withTimeout(session.started.promise, 30_000);
            if (!this.isEventsStartCurrent(generation)) return;
            this.postEventsMessage({ type: "eventsStatus", state: "running", message: "Live EventPipe events are arriving." });
        } catch (error) {
            if (!this.isEventsStartCurrent(generation)) return;
            const message = withRuntimeGuidance(errorMessage(error));
            this.postEventsMessage({ type: "eventsStatus", state: "error", message });
            if (this.eventsChild) await this.closeChild(this.eventsChild);
            this.eventsChild = undefined;
        } finally {
            if (this.eventsChild) this.eventsChild.startInFlight = false;
        }
    }

    public async stopEvents(): Promise<void> {
        this.eventsGeneration++;
        const child = this.eventsChild;
        if (!child) {
            this.postEventsMessage({ type: "eventsStatus", state: "stopped", message: "No Live Events stream is running." });
            return;
        }

        if (!child.sessionId) {
            this.postEventsMessage({ type: "eventsStatus", state: "stopping", message: "Cancelling Live Events startup…" });
            child.expectedShutdown = true;
            child.started.reject(new Error("Live Events startup was cancelled."));
            await this.closeChild(child);
            if (this.eventsChild === child) this.eventsChild = undefined;
            this.postEventsMessage({ type: "eventsStatus", state: "stopped", message: "Live Events stopped." });
            return;
        }

        this.postEventsMessage({ type: "eventsStatus", state: "stopping", message: "Stopping EventPipe EventSource stream…" });
        try {
            this.writeFrame(child, { type: "cancel", sessionId: child.sessionId });
            await withTimeout(child.terminal.promise, 10_000);
        } catch (error) {
            this.output.appendLine(`Could not confirm Live Events shutdown: ${errorMessage(error)}`);
            child.process.kill();
        } finally {
            child.expectedShutdown = true;
            await this.closeChild(child);
            if (this.eventsChild === child) this.eventsChild = undefined;
            this.postEventsMessage({ type: "eventsStatus", state: "stopped", message: "Live Events stopped." });
        }
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

    public async selectProcess(entry: ProcessEntry): Promise<void> {
        if (this.disposed) throw new Error("The diagnostics panel is disposed.");
        const selected = this.selectedTarget;
        const selectedIdentity = selected ? [
            selected.managedEntrypointAssemblyName ?? "", selected.runtimeVersion,
            `${selected.operatingSystem}/${selected.processArchitecture}`,
        ].join("|") : undefined;
        if (selected?.processId !== entry.processId || selectedIdentity !== entry.identity) {
            if (this.exporting) throw new Error("Wait for the current export before selecting another process.");
            const child = this.activeChild;
            if (child && (child.pendingCaptures.size > 0 || child.pendingQueries.size > 0 || child.startInFlight))
                throw new Error("Wait for the current capture or query before selecting another process.");
            await this.stopCounters();
            await this.stopEvents();
            this.resetEventsDeliveryState();
            const previous = this.panel;
            this.panel = undefined;
            this.exporter.clear();
            this.cpuExport = undefined;
            this.panelReady?.resolve();
            this.panelReady = undefined;
            previous?.dispose();
            await this.disposeChild();
        }
        const [operatingSystem, processArchitecture] = entry.platform.split("/");
        const target: TargetProcess = {
            processId: entry.processId, operatingSystem, processArchitecture,
            runtimeVersion: entry.runtimeVersion, managedEntrypointAssemblyName: entry.entrypoint,
        };
        this.selectedTarget = target;
        this.openPanel(target);
        await this.waitForPanelReady();
    }

    public async captureCpuTrace(): Promise<void> {
        const confirmation = await vscode.window.showWarningMessage(
            "Collect a raw CPU trace locally? Raw traces can contain secrets and sensitive application data. "
            + "They are not redacted and will never be uploaded automatically.",
            { modal: true }, "Acknowledge and Capture Trace", "Cancel");
        if (confirmation !== "Acknowledge and Capture Trace") return;
        if (this.captureCpuTask) throw new Error("Wait for the current CPU capture to finish.");
        this.captureCpuTask = this.captureCpuCore(true).finally(() => { this.captureCpuTask = undefined; });
        await this.captureCpuTask;
    }

    private async captureCpuCore(exportTrace = false): Promise<void> {
        if (this.disposed) {
            return;
        }

        if (this.cpuQueryGuard.inFlight) {
            this.postMessage({
                type: "captureStatus",
                kind: "cpu",
                state: "error",
                message: "Wait for the current CPU drilldown query to finish, then capture again.",
            });
            return;
        }

        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) {
            return;
        }

        const requestId = randomBytes(12).toString("hex");
        this.postMessage({
            type: "captureStatus", kind: "cpu", evidenceId: requestId,
            state: "running", message: "Capturing CPU…",
        });

        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "cpu",
                evidenceId: requestId,
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            return;
        }

        const pending: PendingCapture = { kind: "cpu", evidenceId: requestId, exportTrace, deferred: deferred<CpuSampleSummary>() };
        session.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(session, {
                type: "capture",
                requestId,
                kind: "cpu",
                processId: target.processId,
                durationSeconds: 10,
                topN: 10,
                ...(exportTrace ? { exportTrace: true } : {}),
            });
            const summary = await withTimeout(pending.deferred.promise, 60_000);
            this.postMessage({ type: "capture", kind: "cpu", evidenceId: requestId, summary });
            this.postMessage({
                type: "captureStatus", kind: "cpu", evidenceId: requestId,
                state: "done", message: "CPU capture complete.",
            });
            const currentExport = this.cpuExport;
            if (currentExport?.session === session && currentExport.evidenceId === requestId) {
                void this.queryCpu("triage", undefined, "cpu", requestId);
            }
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "cpu",
                evidenceId: requestId,
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            this.output.appendLine(`CPU capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0
                && !this.hasRetainedProfileFor(session)) {
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

    public captureExceptions(): Promise<void> {
        if (!this.captureExceptionsTask) {
            this.captureExceptionsTask = this.captureExceptionsCore().finally(() => {
                this.captureExceptionsTask = undefined;
            });
        }
        return this.captureExceptionsTask;
    }

    private async captureExceptionsCore(): Promise<void> {
        if (this.disposed) return;
        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) return;
        const selection = await vscode.window.showQuickPick([
            { label: "Managed exceptions", description: "Types, counts and retained messages (no stack frames)", captureKind: "exceptions" as const },
            { label: "Crash guard", description: "Exceptions and available stacks; process exit is not proof of an unhandled exception", captureKind: "crash-guard" as const },
        ], { placeHolder: "Capture exceptions for 10 seconds; messages can contain sensitive application data" });
        if (!selection || this.disposed) return;
        const kind = selection.captureKind;
        this.postMessage({ type: "captureStatus", kind: "exceptions", state: "starting", message: "Preparing exception capture…" });
        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            this.postMessage({ type: "captureStatus", kind: "exceptions", state: "error", message: withRuntimeGuidance(errorMessage(error)) });
            return;
        }

        if (session.pendingCaptures.size > 0 || session.pendingQueries.size > 0 || session.startInFlight) {
            this.postMessage({
                type: "captureStatus", kind: "exceptions", state: "error",
                message: "The CLI connection is busy. Wait for the current capture, query or session startup to finish, then capture exceptions before reproducing the problem.",
            });
            return;
        }

        const requestId = randomBytes(12).toString("hex");
        const pending: PendingCapture = { kind, deferred: deferred<ExceptionCaptureResult>() };
        session.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(session, {
                type: "capture", requestId, kind, processId: target.processId,
                durationSeconds: 10, maxRecent: 100, topN: 25,
            });
            this.postMessage({
                type: "captureStatus", kind: "exceptions", state: "running",
                message: "Exception capture requested (10 seconds after startup). Allow EventPipe ~1 second to start, then reproduce the problem now.",
            });
            const result = await withTimeout(pending.deferred.promise, 60_000);
            this.postMessage({ type: "capture", kind: "exceptions", result });
            this.postMessage({ type: "captureStatus", kind: "exceptions", state: "done", message: "Exception capture complete." });
        } catch (error) {
            const message = withRuntimeGuidance(errorMessage(error));
            this.postMessage({ type: "captureStatus", kind: "exceptions", state: "error", message });
            this.output.appendLine(`Exception capture failed: ${message}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0
                && !this.hasRetainedProfileFor(session)) {
                session.expectedShutdown = true;
                await this.closeChild(session);
            }
        }
    }

    public captureDiagnostics(kind?: CaptureViewKind): Promise<void> {
        if (!this.captureDiagnosticsTask) {
            this.captureDiagnosticsTask = this.captureDiagnosticsCore(kind).finally(() => {
                this.captureDiagnosticsTask = undefined;
            });
        }
        return this.captureDiagnosticsTask;
    }

    public captureAllocations(): Promise<void> {
        return this.captureDiagnostics("allocation");
    }

    private async captureDiagnosticsCore(requestedKind?: CaptureViewKind): Promise<void> {
        if (this.disposed) return;
        const target = this.selectedTarget ?? await this.ensureTargetAndPanel();
        if (!target || this.disposed) return;
        const choices: Array<vscode.QuickPickItem & { spec?: CaptureKindSpec }> = [];
        for (const group of CAPTURE_GROUPS) {
            if (!group.kinds.length) continue;
            choices.push({ label: group.label, kind: vscode.QuickPickItemKind.Separator });
            for (const kind of group.kinds) {
                const spec = CAPTURE_KIND_SPECS.find(item => item.kind === kind)!;
                choices.push({ label: spec.label, description: spec.description, spec });
            }
        }
        const selection = requestedKind ? { spec: CAPTURE_KIND_SPECS.find(spec => spec.kind === requestedKind) } : await vscode.window.showQuickPick(
            choices,
            { placeHolder: "All diagnostic captures; names, paths and SQL may contain sensitive data", matchOnDescription: true });
        if (!selection?.spec || this.disposed) return;
        const spec = selection.spec;
        if (spec.kind === "allocation" && this.cpuQueryGuard.inFlight) {
            vscode.window.showWarningMessage("Wait for the current profile query to finish before capturing allocations.");
            return;
        }
        const params = await promptCaptureParams(spec);
        if (!params || this.disposed) return;
        if (spec.risk === "high") {
            const acknowledged = await vscode.window.showWarningMessage(
                "Off-CPU capture uses high-risk kernel scheduler tracing (perf on Linux, ETW on Windows). "
                + "It requires elevated tracing permissions and can expose stack, symbol and method names.",
                { modal: true }, "Acknowledge and Capture", "Cancel");
            if (acknowledged !== "Acknowledge and Capture" || this.disposed) return;
        }
        const requestId = randomBytes(12).toString("hex");
        const status = (state: string, message: string): void =>
            this.postMessage({
                type: "captureStatus",
                kind: spec.kind === "allocation" ? "allocation" : "diagnostics",
                captureKind: spec.kind,
                evidenceId: requestId,
                state,
                message,
            });
        status("starting", `Preparing ${spec.label} capture...`);
        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            status("error", withRuntimeGuidance(errorMessage(error)));
            return;
        }
        if (session.pendingCaptures.size > 0 || session.pendingQueries.size > 0 || session.startInFlight) {
            status("error", "The CLI connection is busy. Wait for the current capture, query or session startup to finish.");
            return;
        }
        const pending: PendingCapture = { kind: "diagnostics", captureKind: spec.kind, deferred: deferred<CaptureViewResult>() };
        session.pendingCaptures.set(requestId, pending);
        try {
            this.writeFrame(session, buildCaptureRequest(requestId, spec, target.processId, params));
            status("running", `${spec.label} capture requested (${params.durationSeconds} seconds after startup). Allow time for tracing startup, then reproduce the problem.`);
            const result = await withTimeout(pending.deferred.promise, captureTimeoutMs(spec.kind, params.durationSeconds));
            if (spec.kind === "allocation") {
                this.allocationProfile = typeof result.handle === "string" && result.handle.length > 0 && result.handle.length <= 128
                    ? { session, handle: result.handle, evidenceId: requestId } : undefined;
                this.postMessage({ type: "capture", kind: "allocation", evidenceId: requestId, result });
                if (this.allocationProfile?.evidenceId === requestId) {
                    await this.queryCpu("call-tree", undefined, "allocation", requestId);
                } else this.postMessage({ type: "queryError", kind: "allocation", evidenceId: requestId, view: "call-tree",
                    message: "This CLI returned only allocation tables. Use a CLI build supporting allocation explorer handles." });
            } else this.postMessage({
                type: "capture", kind: "diagnostics", captureKind: spec.kind, evidenceId: requestId, result,
            });
            status("done", `${spec.label} capture complete.`);
        } catch (error) {
            const message = withRuntimeGuidance(errorMessage(error));
            status("error", message);
            this.output.appendLine(`${spec.label} capture failed: ${message}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0
                && !this.hasRetainedProfileFor(session)) {
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

        const requestId = randomBytes(12).toString("hex");
        this.postMessage({
            type: "captureStatus",
            kind: "heap",
            evidenceId: requestId,
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
                evidenceId: requestId,
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            return;
        }

        const pending: PendingCapture = { kind: "heap", evidenceId: requestId, deferred: deferred<HeapCaptureResult>() };
        session.pendingCaptures.set(requestId, pending);
        this.heapSnapshot = undefined;
        this.liveReadConsent.clear();
        let keepSession = false;
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
            const handle = result.data?.handle;
            if (handle && sourcePick.source !== "gcdump") {
                this.heapSnapshot = { session, handle, evidenceId: requestId };
                keepSession = true;
            }
            this.postMessage({ type: "capture", kind: "heap", evidenceId: requestId, source: sourcePick.source, result });
            this.postMessage({
                type: "captureStatus",
                kind: "heap",
                evidenceId: requestId,
                state: "done",
                message: "Heap snapshot capture complete.",
            });
            // Follow-up `query` drilldown requests (issue #1116), sent sequentially on this same
            // connection — matching `DumpAnalysisPanelController`'s sequential-not-concurrent
            // discipline, since the CLI's request loop processes one request at a time per
            // connection. Skipped for `gcdump`: see the opt-in comment above for why none of these
            // views apply to a gcdump-origin snapshot.
            if (handle && sourcePick.source !== "gcdump") {
                await this.runHeapQueries(session, handle, queryOptIns, requestId);
            }
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                kind: "heap",
                evidenceId: requestId,
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            this.output.appendLine(`Heap snapshot capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !keepSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0
                && !this.hasRetainedProfileFor(session)) {
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
    private async sendQuery(
        session: StreamChild,
        handle: string,
        view: string,
        typeFilter?: string,
        navigation?: QueryNavigation,
        rootMethodFilter?: string,
    ): Promise<QueryResult> {
        const requestId = randomBytes(12).toString("hex");
        const pending = deferred<QueryResult>();
        session.pendingQueries.set(requestId, pending);
        try {
            this.writeFrame(session, buildQueryFrame(requestId, handle, view, typeFilter, navigation, rootMethodFilter));
            return await withTimeout(pending.promise, 60_000);
        } finally {
            session.pendingQueries.delete(requestId);
        }
    }

    private createAddressNavigationHost(): AddressNavigationHost {
        return {
            currentHeapHandle: () => {
                const snapshot = this.heapSnapshot;
                return snapshot && !snapshot.session.closing && snapshot.session.process.exitCode === null && snapshot.session.process.signalCode === null
                    ? snapshot.handle
                    : undefined;
            },
            acknowledge: async (view, address) => this.liveReadConsent.acknowledge(view, () => {
                const snapshot = this.heapSnapshot;
                return snapshot && !snapshot.session.closing && snapshot.session.process.exitCode === null
                    && snapshot.session.process.signalCode === null ? snapshot : undefined;
            }, async () => {
                const choice = await vscode.window.showWarningMessage(
                `Reading ${view === "gcroot" ? "the GC root path of" : "the object at"} ${address} from the LIVE process re-attaches via ptrace and suspends the target while ClrMD walks the heap. This is a High-risk read and is not part of the captured snapshot.`,
                { modal: true },
                "Acknowledge and Read",
                "Allow for This Snapshot Session",
                "Cancel",
                );
                return choice === "Allow for This Snapshot Session" ? "session"
                    : choice === "Acknowledge and Read" ? "once" : "cancel";
            }),
            acknowledgeSensitiveValues: confirmObjectValueReveal,
            query: async (handle, view, address, acknowledgeRisk, includeSensitiveValues, arrayStartIndex, arrayCount) => {
                const snapshot = this.heapSnapshot;
                if (!snapshot || snapshot.handle !== handle) {
                    throw new Error("The heap snapshot changed; capture again to navigate it.");
                }
                return await this.sendQuery(snapshot.session, handle, view, undefined, { address, acknowledgeRisk, includeSensitiveValues, arrayStartIndex, arrayCount });
            },
            post: message => this.postMessage(message),
            copy: async address => {
                await vscode.env.clipboard.writeText(address);
                vscode.window.setStatusBarMessage(`Copied ${address}`, 2000);
            },
            log: text => this.output.appendLine(text),
        };
    }

    /**
     * Issues the 7 always-available heap `query` views, plus whichever of the 3 opt-in views the
     * user selected before capture, sequentially (not concurrently — see `sendQuery`'s doc
     * comment) against the just-captured `handle`. Each view's result (or failure) is posted to
     * the webview independently so one slow/failing view doesn't block the others from rendering.
     */
    private async runHeapQueries(
        session: StreamChild,
        handle: string,
        optIns: HeapQueryOptIns,
        evidenceId: string,
    ): Promise<void> {
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
                if (this.heapSnapshot?.evidenceId !== evidenceId) break;
                this.postMessage({ type: "query", kind: "heap", evidenceId, view, result });
            } catch (error) {
                if (this.heapSnapshot?.evidenceId !== evidenceId) break;
                this.postMessage({ type: "queryError", kind: "heap", evidenceId, view, message: errorMessage(error) });
                this.output.appendLine(`Heap query view '${view}' failed: ${errorMessage(error)}`);
            }
        }
    }

    /**
     * Issues the 4 thread `query` views sequentially against the just-captured `handle`, mirroring
     * `runHeapQueries`.
     */
    private async runThreadQueries(session: StreamChild, handle: string, evidenceId: string): Promise<void> {
        const isCurrent = (): boolean => this.threadSnapshot?.session === session
            && this.threadSnapshot.evidenceId === evidenceId
            && !session.closing && session.process.exitCode === null && session.process.signalCode === null;
        for (const view of THREAD_QUERY_VIEWS) {
            try {
                if (!isCurrent()) break;
                const result = await this.sendQuery(session, handle, view);
                if (!isCurrent()) break;
                this.postMessage({ type: "query", kind: "thread-snapshot", evidenceId, view, result });
            } catch (error) {
                if (!isCurrent()) break;
                this.postMessage({ type: "queryError", kind: "thread-snapshot", evidenceId, view, message: errorMessage(error) });
                this.output.appendLine(`Thread query view '${view}' failed: ${errorMessage(error)}`);
            }
        }
    }

    private async queryCpu(
        view: string,
        rootMethodFilter?: string,
        kind: "cpu" | "allocation" = "cpu",
        expectedEvidenceId?: string,
    ): Promise<void> {
        if (!CPU_QUERY_VIEWS.has(view) || (kind === "allocation" && view === "triage")) {
            this.postMessage({ type: "queryError", kind, evidenceId: expectedEvidenceId, view, message: "Unsupported profile query view." });
            return;
        }
        if (rootMethodFilter !== undefined
            && (rootMethodFilter.length === 0 || rootMethodFilter.length > 512 || /[\u0000-\u001f\u007f]/.test(rootMethodFilter))) {
            this.postMessage({
                type: "queryError", kind, evidenceId: expectedEvidenceId, view,
                message: "The method filter must be 1–512 characters without control characters.",
            });
            return;
        }

        const outcome = await this.cpuQueryGuard.run(async () => {
            const saved = kind === "allocation" ? this.allocationProfile : this.cpuExport;
            const evidenceId = expectedEvidenceId ?? saved?.evidenceId;
            this.postMessage({
                type: kind === "cpu" ? "cpuQueryState" : "allocationQueryState",
                evidenceId,
                state: "running",
            });
            try {
                if (expectedEvidenceId !== undefined && saved?.evidenceId !== expectedEvidenceId) {
                    throw new Error("This profile was replaced or is no longer the current retained capture. Select the current evidence before loading a view.");
                }
                if (!saved || saved.session.closing || saved.session.process.exitCode !== null || saved.session.process.signalCode !== null) {
                    throw new Error(`Capture ${kind} first. Drilldown requires this panel's retained CLI connection.`);
                }
                const result = await this.sendQuery(saved.session, saved.handle, view, undefined, undefined, rootMethodFilter);
                const current = kind === "allocation" ? this.allocationProfile : this.cpuExport;
                if (current !== saved || saved.session.closing || saved.session.process.exitCode !== null || saved.session.process.signalCode !== null) {
                    throw new Error("This profile was replaced or disconnected while the view was loading. Select current evidence and retry.");
                }
                if (kind === "allocation" && result.weightUnit !== "allocation-tick-estimated-bytes") {
                    if (this.allocationProfile === saved) this.allocationProfile = undefined;
                    throw new Error("The CLI did not confirm allocation-byte weighting; update the CLI before exploring this profile.");
                }
                this.postMessage({ type: "query", kind, evidenceId: saved.evidenceId, rootMethodFilter, view, result });
            } catch (error) {
                if (isRecord(error) && error.code === "unknown_handle") {
                    if (kind === "cpu" && this.cpuExport === saved) this.cpuExport = undefined;
                    if (kind === "allocation" && this.allocationProfile === saved) this.allocationProfile = undefined;
                }
                const message = errorMessage(error);
                this.postMessage({ type: "queryError", kind, evidenceId, rootMethodFilter, view, message });
                this.output.appendLine(`${kind} query view '${view}' failed: ${message}`);
            } finally {
                this.postMessage({
                    type: kind === "cpu" ? "cpuQueryState" : "allocationQueryState",
                    evidenceId,
                    state: "idle",
                });
            }
        });
        if (!outcome.started) {
            this.output.appendLine("Ignored a duplicate CPU query while one is already in flight.");
        }
    }

    private hasRetainedProfileFor(session: StreamChild): boolean {
        return this.cpuExport?.session === session || this.allocationProfile?.session === session;
    }

    /**
     * Runs the on-demand `thread-statics` query for `typeFilter` against the last thread snapshot
     * (issue #1126). The view needs an exact type name, so it is never auto-queried.
     */
    private async queryThreadStatics(typeFilter: string, evidenceId: string): Promise<void> {
        const outcome = await this.threadStaticsGuard.run(async () => {
            this.postMessage({ type: "threadStaticsState", evidenceId, state: "running" });
            try {
                await this.queryThreadStaticsCore(typeFilter, evidenceId);
            } finally {
                this.postMessage({ type: "threadStaticsState", evidenceId, state: "idle" });
            }
        });
        if (!outcome.started) {
            this.output.appendLine("Ignored a duplicate thread-statics request while one is already in flight.");
        }
    }

    private async queryThreadStaticsCore(typeFilter: string, evidenceId: string): Promise<void> {
        const snapshot = this.threadSnapshot;
        const view = "thread-statics";
        if (!snapshot || snapshot.evidenceId !== evidenceId || snapshot.session.closing
            || snapshot.session.process.exitCode !== null || snapshot.session.process.signalCode !== null) {
            this.postMessage({
                type: "queryError", kind: "thread-snapshot", evidenceId, view,
                message: "This thread snapshot was replaced or is no longer available. Capture a thread snapshot first.",
            });
            return;
        }
        try {
            const result = await this.sendQuery(snapshot.session, snapshot.handle, view, typeFilter);
            if (this.threadSnapshot !== snapshot || snapshot.session.closing) return;
            this.postMessage({ type: "query", kind: "thread-snapshot", evidenceId, view, result });
        } catch (error) {
            if (this.threadSnapshot !== snapshot) return;
            this.postMessage({ type: "queryError", kind: "thread-snapshot", evidenceId, view, message: errorMessage(error) });
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

        const evidenceId = randomBytes(12).toString("hex");
        const status = (state: string, message: string, retainedEvidenceInvalidated = false): void =>
            this.postMessage({
                type: "captureStatus", kind: "thread-snapshot", evidenceId, state, message,
                ...(retainedEvidenceInvalidated ? { retainedEvidenceInvalidated: true } : {}),
            });
        status("starting", "Preparing thread snapshot capture…");

        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            status("error", withRuntimeGuidance(errorMessage(error)));
            return;
        }

        const pending: PendingCapture = { kind: "thread-snapshot", deferred: deferred<ThreadCaptureResult>() };
        session.pendingCaptures.set(evidenceId, pending);
        this.threadSnapshot = undefined;
        status("running", "Capturing thread snapshot…", true);
        let keepSession = false;
        try {
            this.writeFrame(session, {
                type: "capture",
                requestId: evidenceId,
                kind: "thread-snapshot",
                processId: target.processId,
                maxFramesPerThread: 64,
                acknowledgeRisk: "high",
            });
            // A thread snapshot suspends the whole target during a ClrMD walk, same ceiling
            // reasoning as heap's `live` source — see captureHeapCore's matching comment.
            const result = await withTimeout(pending.deferred.promise, 120_000);
            // Follow-up `query` drilldown requests (issue #1116) — same sequential discipline as
            // runHeapQueries/captureHeapCore.
            const handle = result.data?.handle;
            if (handle) {
                this.threadSnapshot = { session, handle, evidenceId };
                keepSession = true;
            }
            this.postMessage({
                type: "capture", kind: "thread-snapshot", evidenceId,
                retainedHandleAvailable: Boolean(handle), result,
            });
            if (handle) await this.runThreadQueries(session, handle, evidenceId);
            status("done", "Thread snapshot capture complete.");
        } catch (error) {
            status("error", withRuntimeGuidance(errorMessage(error)), true);
            this.output.appendLine(`Thread snapshot capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(evidenceId);
            if (ownsSession && !keepSession && !session.sessionId && !session.startInFlight && session.pendingCaptures.size === 0
                && !this.hasRetainedProfileFor(session)) {
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
        this.eventsGeneration++;
        this.resetEventsDeliveryState();
        this.statusBar.dispose();
        this.panel?.dispose();
        this.shutdownTask = Promise.all([this.disposeChild(), this.disposeEventsChild()]).then(() => undefined);
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
            if (isRecord(message) && message.type === "exportResult") {
                void this.exporter.handle(message);
                return;
            }
            if (isRecord(message) && (message.type === "exportSummary" || message.type === "exportTrace")) {
                void this.exportCpu(message.type === "exportTrace" ? "trace" : "summary");
                return;
            }
            if (!isRecord(message) || typeof message.type !== "string") {
                return;
            }

            if (message.type === "ready") {
                this.panelReady?.resolve();
            } else if (message.type === "revokeLiveReads") {
                this.liveReadConsent.revoke();
            } else if (message.type === "start" && this.selectedTarget) {
                void this.startStream(this.selectedTarget);
            } else if (message.type === "stop") {
                void this.stopCounters();
            } else if (message.type === "startEvents") {
                void this.startEvents();
            } else if (message.type === "stopEvents") {
                void this.stopEvents();
            } else if (message.type === "ackEventBatch") {
                this.acknowledgeEventBatch(message.sessionId, message.batchId);
            } else if (message.type === "captureCpu") {
                void this.captureCpu();
            } else if (message.type === "captureCpuTrace") {
                void this.captureCpuTrace().catch(error => vscode.window.showErrorMessage(errorMessage(error)));
            } else if (message.type === "captureExceptions") {
                void this.captureExceptions();
            } else if (message.type === "captureDiagnostics") {
                void this.captureDiagnostics();
            } else if (message.type === "captureGc") {
                if (message.kind !== "gc" && message.kind !== "gc-activities") {
                    this.postMessage({
                        type: "captureStatus", kind: "diagnostics", state: "error",
                        message: "Choose Garbage collections or GC activities before capturing.",
                    });
                } else {
                    void this.captureDiagnostics(message.kind);
                }
            } else if (message.type === "captureAllocation") {
                void this.captureAllocations();
            } else if (message.type === "captureHeap") {
                void this.captureHeap();
            } else if (message.type === "captureThreadSnapshot") {
                void this.captureThreadSnapshot();
            } else if (message.type === "queryThreadStatics") {
                const typeFilter = parseTypeFilter(message.typeFilter);
                if (typeFilter === undefined || typeof message.evidenceId !== "string") {
                    this.postMessage({
                        type: "queryError", kind: "thread-snapshot",
                        evidenceId: typeof message.evidenceId === "string" ? message.evidenceId : undefined,
                        view: "thread-statics", message: "An exact type name and current snapshot are required.",
                    });
                } else {
                    void this.queryThreadStatics(typeFilter, message.evidenceId);
                }
            } else if (message.type === "queryCpu" || message.type === "queryAllocation") {
                const view = typeof message.view === "string" ? message.view : "";
                const rootMethodFilter = message.rootMethodFilter === undefined
                    ? undefined
                    : typeof message.rootMethodFilter === "string" ? message.rootMethodFilter.trim() : "";
                const evidenceId = typeof message.evidenceId === "string" ? message.evidenceId : undefined;
                void this.queryCpu(view, rootMethodFilter, message.type === "queryAllocation" ? "allocation" : "cpu", evidenceId);
            } else if (message.type === "navigationSelection") {
                void this.exporter.handle(message);
            } else if (message.type === "copyAddress" || message.type === "navigateAddress") {
                if (message.type === "navigateAddress") this.exporter.set(undefined);
                void this.addressNavigation.handle(message);
            }
        }, undefined, []);
        panel.onDidDispose(() => {
            configListener.dispose();
            if (this.panel !== panel) return;
            this.eventsGeneration++;
            this.resetEventsDeliveryState();
            this.panel = undefined;
            this.liveReadConsent.clear();
            this.exporter.clear();
            this.selectedTarget = undefined;
            this.panelReady?.resolve();
            this.panelReady = undefined;
            void this.disposeChild();
            void this.disposeEventsChild();
            if (this.eventsBatchTimer) clearTimeout(this.eventsBatchTimer);
            this.eventsBatchTimer = undefined;
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
                const message = errorMessage(error);
                const cancelled = isCancellationMessage(message);
                this.postMessage({
                    type: "status",
                    state: cancelled ? "stopped" : "error",
                    message: cancelled ? "Live counters start cancelled." : withRuntimeGuidance(message),
                });
            }
            const message = errorMessage(error);
            this.output.appendLine(isCancellationMessage(message)
                ? "Live counter startup was cancelled."
                : `Live counter startup failed: ${message}`);
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
        return this.connectStreamChild(false);
    }

    private async connectEventsChild(): Promise<StreamChild> {
        if (!this.eventsConnectTask) {
            this.eventsConnectTask = (async () => {
                const existing = this.eventsChild;
                if (existing && !existing.closing
                    && existing.process.exitCode === null && existing.process.signalCode === null) {
                    return existing;
                }
                if (existing) await this.closeChild(existing);
                return this.connectStreamChild(true);
            })().then(session => {
                this.eventsChild = session;
                return session;
            }).finally(() => {
                this.eventsConnectTask = undefined;
            });
        }
        return this.eventsConnectTask;
    }

    private async connectStreamChild(eventsConnection: boolean): Promise<StreamChild> {
        let child: ChildProcessWithoutNullStreams;
        try {
            child = this.spawnStream();
        } catch (error) {
            throw new Error(withRuntimeGuidance(errorMessage(error)));
        }
        const session: StreamChild = {
            process: child,
            lines: eventsConnection
                ? new BoundedProtocolLineReader(child.stdout, LIVE_EVENT_CONTROL_LINE_MAX_BYTES)
                : createInterface({ input: child.stdout }),
            handshake: deferred<void>(),
            protocolReady: false,
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
            eventsConnection,
        };
        if (!eventsConnection) {
            this.activeChild = session;
            this.statusBar.text = "$(sync~spin) .NET Counters";
        }

        if (session.lines instanceof BoundedProtocolLineReader) {
            session.lines.on("line", (line: string, byteLength: number) => this.handleFrame(session, line, byteLength));
            session.lines.on("oversized", () => {
                const error = new Error(`The CLI emitted a control frame larger than ${LIVE_EVENT_CONTROL_LINE_MAX_BYTES} UTF-8 bytes.`);
                this.failPending(session, error);
                session.startupErrorReported = true;
                this.postEventsMessage({ type: "eventsStatus", state: "error", message: error.message });
                session.process.kill();
            });
        } else {
            session.lines.on("line", line => this.handleFrame(session, line));
        }
        child.stdin.on("error", error => this.failPending(session, error));
        child.stderr.on("data", (chunk: Buffer | string) => {
            session.stderrTail = (session.stderrTail + chunk.toString()).slice(-8_192);
        });
        child.once("error", error => {
            this.failPending(session, error);
        });
        child.once("close", (code, signal) => {
            if (this.heapSnapshot?.session === session) this.liveReadConsent.clear();
            session.lines.close();
            if (!session.terminalReceived && !session.expectedShutdown) {
                const details = session.stderrTail.trim();
                const reason = describeStreamExit(session.protocolReady, code, signal, details);
                this.failPending(session, new Error(reason));
                if (!session.startupErrorReported && session.eventsConnection) {
                    this.postEventsMessage({
                        type: "eventsStatus",
                        state: "error",
                        message: withRuntimeGuidance(reason),
                    });
                } else if (!session.startupErrorReported) {
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
            for (const pending of this.pendingExports.values()) {
                if (pending.session === session) pending.deferred.reject(new Error("The CLI connection closed before the export completed."));
            }
            if (this.cpuExport?.session === session) this.cpuExport = undefined;
            if (this.allocationProfile?.session === session) this.allocationProfile = undefined;
            this.postMessage({ type: "profileAvailability" });
            if (this.activeChild === session) {
                this.activeChild = undefined;
                this.liveCountersChanged?.(false);
                this.statusBar.text = "$(pulse) .NET Counters";
            }
            if (this.eventsChild === session) this.eventsChild = undefined;
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

    private handleFrame(session: StreamChild, line: string, byteLength?: number): void {
        let frame: ProtocolFrame;
        try {
            frame = parseProtocolFrame(line);
        } catch (error) {
            const compatibilityError = session.protocolReady
                ? "The CLI emitted invalid JSONL on an established protocol connection. Check the .NET Diagnostics output for details."
                : describeStreamCompatibilityError(line);
            if (session.protocolReady) this.output.appendLine(`Invalid CLI protocol frame (last 2048 characters): ${line.slice(-2048)}`);
            this.failPending(session, new Error(compatibilityError));
            session.startupErrorReported = true;
            if (session.eventsConnection) {
                this.postEventsMessage({
                    type: "eventsStatus",
                    state: "error",
                    message: compatibilityError,
                });
            } else {
                this.postMessage({
                    type: "status",
                    state: "error",
                    message: compatibilityError,
                });
            }
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
                if (session.eventsConnection) {
                    if (!Array.isArray(frame.supportedLiveKinds)
                        || !frame.supportedLiveKinds.includes("event_source")) {
                        const error = new Error("The configured CLI does not advertise the event_source live kind. Update the CLI to a build with Live Events support.");
                        session.handshake.reject(error);
                        session.startupErrorReported = true;
                        this.postEventsMessage({ type: "eventsStatus", state: "error", message: error.message });
                        session.process.kill();
                        return;
                    }
                    session.supportedLiveKinds = frame.supportedLiveKinds.filter((kind): kind is string => typeof kind === "string");
                }
                session.protocolReady = true;
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
                if (session.eventsConnection) {
                    this.eventsStreamSessionId = frame.sessionId;
                    this.postEventsMessage({
                        type: "eventsStatus",
                        state: "running",
                        sessionId: frame.sessionId,
                        message: "Live EventPipe events are arriving.",
                    });
                } else {
                    this.postMessage({
                        type: "session",
                        sessionId: frame.sessionId,
                        kinds: Array.isArray(frame.kinds) ? frame.kinds : ["counters"],
                    });
                }
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
                if (session.eventsConnection && kind === "event_source") {
                    const serializedBytes = byteLength ?? Buffer.byteLength(line, "utf8");
                    if (serializedBytes > LIVE_EVENT_FRAME_MAX_BYTES) {
                        this.failEventsProtocol(session, `The CLI emitted a Live Events observation larger than ${LIVE_EVENT_FRAME_MAX_BYTES} UTF-8 bytes.`);
                        break;
                    }
                    if (!isLiveEventObservation(frame)) {
                        this.failEventsProtocol(session, "The CLI emitted a Live Events observation with an unsupported or unbounded schema.");
                        break;
                    }
                    const accepted = this.eventsQueue.enqueue(frame, serializedBytes);
                    if (!accepted) {
                        this.eventsQualityRevision++;
                    }
                    this.scheduleEventsBatch();
                } else if (kind === "counters" && isCounterValue(frame.counter)) {
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
                if (frame.status === "targetExited" && this.heapSnapshot?.session === session)
                    this.liveReadConsent.clear();
                session.sessionId = undefined;
                session.terminal.resolve();
                if (session.eventsConnection) {
                    this.eventsQualityFinal = true;
                    this.postEventsMessage({
                        type: "eventsStatus",
                        state: frame.status === "failed" ? "error" : "stopped",
                        message: frame.status === "targetExited" ? "Target process exited." : "Live Events stopped.",
                    });
                } else {
                    this.postMessage({
                        type: "terminal",
                        status: frame.status,
                        kinds: Array.isArray(frame.kinds) ? frame.kinds : ["counters"],
                        eventPipeEventsLost: frame.eventPipeEventsLost,
                        droppedObservations: frame.droppedObservations,
                        error: frame.error,
                    });
                    this.statusBar.text = "$(pulse) .NET Counters";
                }
                break;

            case "quality":
                if (session.eventsConnection && frame.sessionId === session.sessionId
                    && frame.kind === "event_source" && isLiveEventQuality(frame)) {
                    this.eventsQuality = {
                        eventPipeEventsLost: frame.eventPipeEventsLost,
                        eventPipeLossAvailable: frame.eventPipeLossAvailable,
                        coreQueueDropped: frame.coreQueueDropped,
                        cliDispatchQueueDropped: frame.cliDispatchQueueDropped,
                    };
                    this.eventsQualityRevision++;
                    this.scheduleEventsBatch();
                }
                break;

            case "capture": {
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pending = requestId ? session.pendingCaptures.get(requestId) : undefined;
                if (!pending) {
                    break;
                }
                if (pending.kind === "diagnostics" && frame.kind === pending.captureKind
                    && isCaptureViewResult(frame.result) && frame.result.kind === pending.captureKind) {
                    pending.deferred.resolve(frame.result);
                } else if (pending.kind === "cpu" && isCpuSampleSummary(frame.result)) {
                    this.cpuExport = typeof frame.exportHandle === "string" && frame.exportHandle.length > 0 && frame.exportHandle.length <= 128
                        ? { session, handle: frame.exportHandle, evidenceId: pending.evidenceId, trace: pending.exportTrace === true } : undefined;
                    pending.deferred.resolve(frame.result);
                } else if ((pending.kind === "exceptions" || pending.kind === "crash-guard")
                    && isExceptionCaptureResult(frame.result)) {
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
            case "export": {
                const pending = typeof frame.requestId === "string" ? this.pendingExports.get(frame.requestId) : undefined;
                if (pending?.session === session) pending.deferred.resolve(frame.result);
                break;
            }

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
                const error = Object.assign(new Error(
                    frame.code === "protocol_version_unsupported"
                        ? `CLI protocol mismatch: ${message}`
                        // An older CLI build that predates #1110/#1112 does not recognize the
                        // "heap"/"thread-snapshot" capture kinds; surface a concise upgrade hint
                        // instead of the raw CLI error, matching the protocol-version-mismatch
                        // compatibility message.
                        : frame.code === "unsupported_capture_kind"
                            ? withRuntimeGuidance(message)
                            : message,
                ), { code: frame.code });
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pendingCapture = requestId ? session.pendingCaptures.get(requestId) : undefined;
                const pendingQuery = requestId ? session.pendingQueries.get(requestId) : undefined;
                const pendingExport = requestId ? this.pendingExports.get(requestId) : undefined;
                if (pendingExport?.session === session) {
                    pendingExport.deferred.reject(error);
                } else if (pendingCapture) {
                    pendingCapture.deferred.reject(error);
                } else if (pendingQuery) {
                    pendingQuery.reject(error);
                } else if (session.eventsConnection) {
                    this.failPending(session, error);
                    this.postEventsMessage({ type: "eventsStatus", state: "error", message });
                    this.output.appendLine(`Live Events CLI protocol error: ${message}`);
                    session.process.kill();
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
        if (isRecord(frame) && ["capture", "query", "start"].includes(String(frame.type))
            && [...this.pendingExports.values()].some(pending => pending.session === session))
            throw new Error("Wait for the current export to finish.");
        if (!session.process.stdin.writable) {
            throw new Error("CLI stdin is closed.");
        }
        session.process.stdin.write(`${JSON.stringify(frame)}\n`);
    }

    private postMessage(message: object): void {
        if (isRecord(message) && message.state === "error" && typeof message.message === "string"
            && isCancellationMessage(message.message)) {
            message = { ...message, state: "stopped" };
        }
        this.exporter.observe(message);
        const retained = (profile: { session: StreamChild } | undefined): boolean => !!profile
            && !profile.session.closing && profile.session.process.exitCode === null && profile.session.process.signalCode === null;
        void this.panel?.webview.postMessage({ ...message, profiles: {
            cpu: retained(this.cpuExport), trace: retained(this.cpuExport) && this.cpuExport?.trace === true,
            allocation: retained(this.allocationProfile),
        } });
        if (isRecord(message) && (message.type === "status" || message.type === "terminal"))
            this.liveCountersChanged?.(message.type === "status" && ["running", "stopping"].includes(String(message.state)));
        if (isRecord(message) && message.state === "error" && typeof message.message === "string") {
            this.offerRecoveryActions(message.message, message.type === "captureStatus");
        }
    }

    private async exportCpu(format: "summary" | "trace"): Promise<void> {
        if (this.exporting) return;
        this.exporting = true;
        this.postMessage({ type: "cpuExportState", state: "running" });
        try {
            const saved = this.cpuExport;
            if (!saved || saved.session.closing || saved.session.process.exitCode !== null)
                throw new Error("Capture CPU with an updated CLI first. Export handles live only while this panel's CLI connection remains open.");
            if (saved.session.pendingCaptures.size || saved.session.pendingQueries.size || saved.session.startInFlight)
                throw new Error("Wait for the current capture or query to finish.");
            let outputPath: string | undefined;
            if (format === "trace") {
                const choice = await vscode.window.showWarningMessage(
                    "Export this unredacted raw trace locally? It may contain secrets and sensitive application data. "
                    + "Only share it after your own review. No upload will be performed.",
                    { modal: true }, "Export Locally", "Cancel");
                if (choice !== "Export Locally") return;
                const destination = await vscode.window.showSaveDialog({ filters: { "EventPipe trace": ["nettrace"] } });
                if (!destination) return;
                if (destination.scheme !== "file") throw new Error("Raw traces can only be exported to a local file.");
                outputPath = destination.fsPath;
            }
            if (this.cpuExport !== saved || saved.session.closing) throw new Error("The CPU capture connection changed. Capture again.");
            if (saved.session.pendingCaptures.size || saved.session.pendingQueries.size || saved.session.startInFlight)
                throw new Error("Wait for the current capture or query to finish.");
            const requestId = randomBytes(12).toString("hex"), result = deferred<unknown>();
            this.pendingExports.set(requestId, { session: saved.session, deferred: result });
            try {
                this.writeFrame(saved.session, { type: "export", requestId, handle: saved.handle, format,
                    ...(format === "trace" ? { outputPath, acknowledgeRisk: "critical" } : {}) });
                const value = await withTimeout(result.promise, 60_000);
                if (format === "summary") {
                    if (!isRecord(value) || value.Schema !== "dotnet-diagnostics-mcp/investigation-summary/v1"
                        || !isRecord(value.Provenance) || !isRecord(value.Findings)
                        || typeof value.Findings.TotalSamples !== "number" || !Number.isSafeInteger(value.Findings.TotalSamples)
                        || value.Findings.TotalSamples < 0 || !Array.isArray(value.Findings.TopHotspots))
                        throw new Error("The CLI returned an unsupported CPU investigation summary.");
                    await openShareable(value, "json");
                }
                else {
                    if (!isRecord(value) || value.kind !== "trace" || value.outputPath !== outputPath
                        || typeof value.totalBytes !== "number" || !Number.isSafeInteger(value.totalBytes) || value.totalBytes < 0
                        || typeof value.sha256 !== "string" || !/^[a-f0-9]{64}$/i.test(value.sha256))
                        throw new Error("The CLI did not return a verified trace export receipt.");
                    await vscode.window.showInformationMessage("Raw trace exported locally. It is unredacted; no upload was performed.");
                }
            } catch (error) {
                if (isRecord(error) && error.code === "unknown_handle" && this.cpuExport === saved)
                    this.cpuExport = undefined;
                if (!result.settled) {
                    saved.session.expectedShutdown = true;
                    await this.closeChild(saved.session);
                }
                throw error;
            } finally { this.pendingExports.delete(requestId); }
        } catch (error) {
            await vscode.window.showErrorMessage(`CPU export failed: ${errorMessage(error)}`);
        } finally {
            this.exporting = false;
            this.postMessage({ type: "cpuExportState", state: "idle" });
        }
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

    private disposeEventsChild(): Promise<void> {
        if (!this.eventsDisposalTask) {
            this.eventsDisposalTask = this.disposeEventsChildCore().finally(() => {
                this.eventsDisposalTask = undefined;
            });
        }
        return this.eventsDisposalTask;
    }

    private async disposeEventsChildCore(): Promise<void> {
        const child = this.eventsChild;
        if (!child) return;
        if (this.eventsBatchTimer) clearTimeout(this.eventsBatchTimer);
        this.eventsBatchTimer = undefined;
        if (child.sessionId) {
            try {
                this.writeFrame(child, { type: "cancel", sessionId: child.sessionId });
                await withTimeout(child.terminal.promise, 10_000);
            } catch (error) {
                this.output.appendLine(`Forcing Live Events CLI shutdown: ${errorMessage(error)}`);
                child.process.kill();
            }
        }
        await this.closeChild(child);
        if (this.eventsChild === child) this.eventsChild = undefined;
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
        if (this.heapSnapshot?.session === session) this.liveReadConsent.clear();
        if (this.cpuExport?.session === session) this.cpuExport = undefined;
        if (this.allocationProfile?.session === session) this.allocationProfile = undefined;
        this.postMessage({ type: "profileAvailability" });
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
        if (this.eventsChild === session) {
            this.eventsChild = undefined;
        }
    }

    private postEventsMessage(message: object): void {
        if (isRecord(message) && message.state === "error" && typeof message.message === "string"
            && isCancellationMessage(message.message)) {
            message = { ...message, state: "stopped" };
        }
        void this.panel?.webview.postMessage(message);
        if (isRecord(message) && message.state === "error" && typeof message.message === "string") {
            this.offerRecoveryActions(message.message, true);
        }
    }

    private offerRecoveryActions(message: string, captureFailure: boolean): void {
        if (isCancellationMessage(message)) return;
        const checkEnvironment = /protocol|permission|denied|ptrace|access denied|diagnostic (?:ipc|socket)/i.test(message)
            || /ENOENT|hostfxr|framework.*not found|runtime.*not found|dotnet-diagnostics-cli.*not found/i.test(message);
        const actions = [
            ...(checkEnvironment ? ["Check Environment"] : []),
            ...((captureFailure || checkEnvironment) && !/wait for the current/i.test(message)
                ? ["Show Output"] : []),
        ];
        if (actions.length === 0) return;
        const now = Date.now();
        if (this.lastRecoveryNotice?.message === message && now - this.lastRecoveryNotice.at < 2_000) return;
        this.lastRecoveryNotice = { message, at: now };
        void vscode.window.showErrorMessage(message, ...actions).then(action => {
            if (action === "Check Environment") {
                void vscode.commands.executeCommand("dotnetDiagnostics.checkEnvironment");
            } else if (action === "Show Output") {
                this.output.show(true);
            }
        });
    }

    private isEventsStartCurrent(generation: number): boolean {
        return !this.disposed && generation === this.eventsGeneration;
    }

    private resetEventsDeliveryState(): void {
        if (this.eventsBatchTimer) clearTimeout(this.eventsBatchTimer);
        this.eventsBatchTimer = undefined;
        this.eventsQueue = new LiveEventDeliveryQueue();
        this.eventsStreamSessionId = undefined;
        this.eventsQuality = undefined;
        this.eventsQualityRevision = 0;
        this.eventsQualityAcknowledgedRevision = 0;
        this.eventsInFlightQualityRevision = undefined;
        this.eventsLastBatchAt = 0;
        this.eventsLastQualityAt = 0;
        this.eventsQualityFinal = false;
    }

    private failEventsProtocol(session: StreamChild, message: string): void {
        const error = new Error(message);
        this.failPending(session, error);
        session.startupErrorReported = true;
        this.postEventsMessage({ type: "eventsStatus", state: "error", message });
        session.process.kill();
    }

    private acknowledgeEventBatch(sessionId: unknown, batchId: unknown): void {
        if (typeof sessionId !== "string" || sessionId !== this.eventsStreamSessionId
            || !Number.isSafeInteger(batchId) || !this.eventsQueue.acknowledge(Number(batchId))) {
            return;
        }
        if (this.eventsInFlightQualityRevision !== undefined) {
            this.eventsQualityAcknowledgedRevision = Math.max(
                this.eventsQualityAcknowledgedRevision,
                this.eventsInFlightQualityRevision,
            );
            this.eventsInFlightQualityRevision = undefined;
        }
        this.scheduleEventsBatch();
    }

    private scheduleEventsBatch(): void {
        if (!this.panel || !this.eventsStreamSessionId || this.eventsQueue.hasInFlightBatch || this.eventsBatchTimer) {
            return;
        }
        const qualityDirty = this.eventsQuality !== undefined
            && this.eventsQualityRevision > this.eventsQualityAcknowledgedRevision;
        const now = Date.now();
        const qualityDue = qualityDirty
            && (this.eventsQualityFinal || now - this.eventsLastQualityAt >= 1_000);
        if (this.eventsQueue.retainedEventCount === 0 && !qualityDue) {
            return;
        }
        const intervalMs = 1_000 / LIVE_EVENT_MAX_UPDATES_PER_SECOND;
        const eventWaitMs = this.eventsQueue.retainedEventCount > 0
            ? Math.max(0, intervalMs - (now - this.eventsLastBatchAt))
            : 0;
        const qualityWaitMs = qualityDirty && !this.eventsQualityFinal
            ? Math.max(0, 1_000 - (now - this.eventsLastQualityAt))
            : 0;
        const waitMs = this.eventsQueue.retainedEventCount > 0
            ? Math.max(eventWaitMs, qualityDue ? 0 : Math.min(qualityWaitMs, eventWaitMs))
            : qualityWaitMs;
        this.eventsBatchTimer = setTimeout(() => {
            this.eventsBatchTimer = undefined;
            this.sendEventsBatch();
        }, waitMs);
    }

    private sendEventsBatch(): void {
        const sessionId = this.eventsStreamSessionId;
        if (!sessionId || !this.panel || this.eventsQueue.hasInFlightBatch) return;
        const includeQuality = this.eventsQuality !== undefined
            && this.eventsQualityRevision > this.eventsQualityAcknowledgedRevision
            && (this.eventsQualityFinal || Date.now() - this.eventsLastQualityAt >= 1_000);
        const batch = this.eventsQueue.takeBatch(includeQuality);
        if (!batch) return;
        const qualityRevision = includeQuality ? this.eventsQualityRevision : undefined;
        this.eventsInFlightQualityRevision = qualityRevision;
        this.eventsLastBatchAt = Date.now();
        if (includeQuality) this.eventsLastQualityAt = this.eventsLastBatchAt;
        const message = {
            type: "eventsBatch",
            sessionId,
            batchId: batch.batchId,
            events: batch.events,
            ...(includeQuality && this.eventsQuality
                ? { quality: { ...this.eventsQuality, hostDeliveryDropped: this.eventsQueue.dropped } }
                : {}),
        };
        if (batch.events.length > 64 || Buffer.byteLength(JSON.stringify(message), "utf8") > LIVE_EVENT_BATCH_MAX_BYTES) {
            const child = this.eventsChild;
            if (child) {
                this.failEventsProtocol(child, `The Live Events webview batch exceeded the ${LIVE_EVENT_BATCH_MAX_BYTES}-byte bridge limit.`);
            } else {
                this.output.appendLine(`A Live Events batch exceeded the ${LIVE_EVENT_BATCH_MAX_BYTES}-byte bridge limit.`);
            }
            return;
        }
        void this.panel.webview.postMessage(message);
    }
}

function isLiveEventQuality(value: Record<string, unknown>): value is Record<string, unknown> & {
    eventPipeEventsLost: number | null;
    eventPipeLossAvailable: boolean;
    coreQueueDropped: number;
    cliDispatchQueueDropped: number;
} {
    return (value.eventPipeEventsLost === null || (Number.isSafeInteger(value.eventPipeEventsLost) && Number(value.eventPipeEventsLost) >= 0))
        && typeof value.eventPipeLossAvailable === "boolean"
        && value.eventPipeLossAvailable === (value.eventPipeEventsLost !== null)
        && Number.isSafeInteger(value.coreQueueDropped) && Number(value.coreQueueDropped) >= 0
        && Number.isSafeInteger(value.cliDispatchQueueDropped) && Number(value.cliDispatchQueueDropped) >= 0;
}

function isSignedInt64(value: string): boolean {
    if (!/^-?(0|[1-9]\d*)$/.test(value)) return false;
    try {
        const parsed = BigInt(value);
        return parsed >= -(1n << 63n) && parsed <= (1n << 63n) - 1n;
    } catch {
        return false;
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
    private readonly exporter = new ResultExporter();
    private readonly panel: vscode.WebviewPanel;
    private disposed = false;
    /** Tracked so `dispose()` can forcibly end an in-flight capture-only connection when the panel is closed mid-analysis. */
    private activeChild: DumpAnalysisChild | undefined;
    /** Handle of the thread snapshot; the connection stays open (until `dispose`) so `thread-statics` can be queried on demand. */
    private threadHandle: string | undefined;
    /** Heap snapshot handle address navigation queries; the connection stays open for it too (issue #1168). */
    private heapHandle: string | undefined;
    private readonly addressNavigation = new AddressNavigationController(this.createAddressNavigationHost());
    private readonly threadStaticsGuard = new SingleFlightGuard();

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
            if (isRecord(message) && (message.type === "exportResult" || message.type === "navigationSelection")) {
                void this.exporter.handle(message);
                return;
            }
            if (isRecord(message) && (message.type === "copyAddress" || message.type === "navigateAddress")) {
                if (message.type === "navigateAddress") this.exporter.set(undefined);
                void this.addressNavigation.handle(message);
                return;
            }
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
            if (!this.threadHandle && !this.heapHandle) {
                await this.close(child);
            }
        }
    }

    /** See `CounterPanelController.queryThreadStatics`. */
    private async queryThreadStatics(typeFilter: string): Promise<void> {
        const outcome = await this.threadStaticsGuard.run(async () => {
            this.postMessage({ type: "threadStaticsState", state: "running" });
            try {
                await this.queryThreadStaticsCore(typeFilter);
            } finally {
                this.postMessage({ type: "threadStaticsState", state: "idle" });
            }
        });
        if (!outcome.started) {
            this.output.appendLine("Ignored a duplicate thread-statics request while one is already in flight.");
        }
    }

    private async queryThreadStaticsCore(typeFilter: string): Promise<void> {
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
                this.heapHandle = handle;
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
    private async sendQuery(child: DumpAnalysisChild, handle: string, view: string, typeFilter?: string, navigation?: QueryNavigation): Promise<QueryResult> {
        const requestId = randomBytes(12).toString("hex");
        const pending = deferred<QueryResult>();
        child.pendingQueries.set(requestId, pending);
        try {
            this.writeFrame(child, buildQueryFrame(requestId, handle, view, typeFilter, navigation));
            return await withTimeout(pending.promise, 60_000);
        } finally {
            child.pendingQueries.delete(requestId);
        }
    }

    private createAddressNavigationHost(): AddressNavigationHost {
        return {
            currentHeapHandle: () => this.activeChild && !this.activeChild.terminated ? this.heapHandle : undefined,
            // A dump read is offline (Moderate/Warn in the CLI): nothing is attached or suspended, so
            // no High-risk acknowledgement is requested or sent.
            acknowledge: async () => true,
            acknowledgeSensitiveValues: confirmObjectValueReveal,
            query: async (handle, view, address, _acknowledgeRisk, includeSensitiveValues, arrayStartIndex, arrayCount) => {
                const child = this.activeChild;
                if (!child || child.terminated || handle !== this.heapHandle) {
                    throw new Error("The dump analysis connection is no longer available.");
                }
                return await this.sendQuery(child, handle, view, undefined, { address, includeSensitiveValues, arrayStartIndex, arrayCount });
            },
            post: message => this.postMessage(message),
            copy: async address => {
                await vscode.env.clipboard.writeText(address);
                vscode.window.setStatusBarMessage(`Copied ${address}`, 2000);
            },
            log: text => this.output.appendLine(text),
        };
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
            this.exporter.observe(message);
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
    const history = new CaptureHistory(resolveCliPath, output);
    const processes = new ProcessTree(context.workspaceState, resolveCliPath, async (process, action) => {
        if (action === "save") {
            await history.save(process);
            return;
        }
        await controller.selectProcess(process);
        if (action === "counters") await controller.startCounters();
        else if (action === "cpu") await controller.captureCpu();
        else if (action === "heap") await controller.captureHeap();
        else if (action === "threads") await controller.captureThreadSnapshot();
    }, output);
    const guarded = async (operation: () => Promise<void>): Promise<void> => {
        try { await operation(); }
        catch (error) {
            const message = errorMessage(error);
            output.appendLine(message);
            void vscode.window.showErrorMessage(message);
        }
    };
    const processCommand = (row: ProcessRow | undefined, action: ProcessAction, fallback: () => Promise<void>): Promise<void> =>
        row ? guarded(() => processes.invoke(row, action)) : fallback();
    const actions = new DiagnosticsActionsProvider();
    controller.liveCountersChanged = active => actions.setLiveCountersActive(active);
    const actionsView = vscode.window.createTreeView("dotnetDiagnostics.actions", {
        treeDataProvider: actions,
        showCollapseAll: false,
    });
    context.subscriptions.push(
        output,
        controller,
        actionsView,
        actions,
        processes,
        history,
        vscode.window.createTreeView("dotnetDiagnostics.processes", { treeDataProvider: processes }),
        vscode.window.createTreeView("dotnetDiagnostics.history", { treeDataProvider: history }),
        vscode.commands.registerCommand("dotnetDiagnostics.selectProcess", (row: ProcessRow) => guarded(() => processes.invoke(row, "select"))),
        vscode.commands.registerCommand("dotnetDiagnostics.refreshProcesses", () => guarded(() => processes.refresh())),
        vscode.commands.registerCommand("dotnetDiagnostics.filterProcesses", () => guarded(() => processes.setFilter())),
        vscode.commands.registerCommand("dotnetDiagnostics.refreshHistory", (row?: HistoryRow) => guarded(() => history.refresh(row && "more" in row ? row.more : undefined))),
        vscode.commands.registerCommand("dotnetDiagnostics.openCapture", (row: HistoryRow) => guarded(() => history.open(row))),
        vscode.commands.registerCommand("dotnetDiagnostics.deleteCapture", (row: HistoryRow) => guarded(() => history.delete(row))),
        vscode.commands.registerCommand("dotnetDiagnostics.saveSnapshot", (row?: ProcessRow) => guarded(() => processes.invoke(row, "save"))),
        vscode.commands.registerCommand("dotnetDiagnostics.startCounters", (row?: ProcessRow) => processCommand(row, "counters", () => controller.startCounters())),
        vscode.commands.registerCommand("dotnetDiagnostics.stopCounters", () => controller.stopCounters()),
        vscode.commands.registerCommand("dotnetDiagnostics.captureCpu", (row?: ProcessRow) => processCommand(row, "cpu", () => controller.captureCpu())),
        vscode.commands.registerCommand("dotnetDiagnostics.captureCpuTrace", () => guarded(() => controller.captureCpuTrace())),
        vscode.commands.registerCommand("dotnetDiagnostics.startEvents", () => guarded(() => controller.startEvents())),
        vscode.commands.registerCommand("dotnetDiagnostics.captureExceptions", () => controller.captureExceptions()),
        vscode.commands.registerCommand("dotnetDiagnostics.captureDiagnostics", (kind?: unknown) => guarded(async () => {
            if (kind !== undefined && (typeof kind !== "string" || !isCaptureViewKind(kind)))
                throw new Error("Select a supported diagnostic capture.");
            await controller.captureDiagnostics(kind);
        })),
        vscode.commands.registerCommand("dotnetDiagnostics.captureHeap", (row?: ProcessRow) => processCommand(row, "heap", () => controller.captureHeap())),
        vscode.commands.registerCommand("dotnetDiagnostics.captureThreadSnapshot", (row?: ProcessRow) => processCommand(row, "threads", () => controller.captureThreadSnapshot())),
        vscode.commands.registerCommand("dotnetDiagnostics.analyzeDumpFile", () => analyzeDumpFile(output, context)),
    );
    registerOnboarding(context, output);
}

class DiagnosticsActionsProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly children = new WeakMap<vscode.TreeItem, vscode.TreeItem[]>();
    private readonly changed = new vscode.EventEmitter<vscode.TreeItem | undefined>();
    public readonly onDidChangeTreeData = this.changed.event;
    private liveCountersActive = false;
    public setLiveCountersActive(active: boolean): void {
        if (active === this.liveCountersActive) return;
        this.liveCountersActive = active;
        this.changed.fire(undefined);
    }
    public dispose(): void { this.changed.dispose(); }
    public getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    public getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
        if (element) return this.children.get(element) ?? [];
        const start = new vscode.TreeItem("Start Live Counters", vscode.TreeItemCollapsibleState.None);
        start.description = "Choose a running .NET process";
        start.iconPath = new vscode.ThemeIcon("play");
        start.command = {
            command: "dotnetDiagnostics.startCounters",
            title: "Start Live Counters",
        };

        const stop = new vscode.TreeItem("Stop Live Counters", vscode.TreeItemCollapsibleState.None);
        stop.description = this.liveCountersActive ? "Stop the active capture" : "No live counters running";
        stop.tooltip = this.liveCountersActive ? "Stop the active live counters session." : "Start Live Counters first.";
        stop.iconPath = new vscode.ThemeIcon("debug-stop");
        if (this.liveCountersActive) stop.command = {
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

        const captureExceptions = new vscode.TreeItem("Capture Exceptions", vscode.TreeItemCollapsibleState.None);
        captureExceptions.description = "Collect exception types, messages and available stacks";
        captureExceptions.iconPath = new vscode.ThemeIcon("warning");
        captureExceptions.command = { command: "dotnetDiagnostics.captureExceptions", title: "Capture Exceptions" };
        const captureDiagnostics = new vscode.TreeItem("Capture Diagnostics", vscode.TreeItemCollapsibleState.None);
        captureDiagnostics.description = "Allocation, off-CPU, contention, thread pool, GC, requests, Kestrel, DB, networking";
        captureDiagnostics.iconPath = new vscode.ThemeIcon("search");
        captureDiagnostics.command = { command: "dotnetDiagnostics.captureDiagnostics", title: "Capture Diagnostics" };
        const action = (label: string, command: string, description: string, args?: unknown[]): vscode.TreeItem => {
            const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
            item.command = { command, title: label, arguments: args };
            item.description = description;
            item.tooltip = `${label}: ${description}`;
            return item;
        };
        const group = (id: string, label: string, children: vscode.TreeItem[]): vscode.TreeItem => {
            const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.Collapsed);
            item.id = `dotnetDiagnostics.group.${id}`;
            this.children.set(item, children);
            return item;
        };
        const groups = CAPTURE_GROUPS.map(spec => {
            const captures = spec.kinds.map(kind => {
                const capture = CAPTURE_KIND_SPECS.find(item => item.kind === kind)!;
                return action(`Capture ${capture.label}`, "dotnetDiagnostics.captureDiagnostics", capture.description, [kind]);
            });
            if (spec.id === "cpu") captures.push(captureCpu,
                action("Capture CPU + raw trace", "dotnetDiagnostics.captureCpuTrace", "Unredacted trace; explicit confirmation before collection"));
            if (spec.id === "memory") captures.splice(1, 0, captureHeap);
            if (spec.id === "threads") captures.unshift(captureThreadSnapshot);
            if (spec.id === "exceptions") captures.push(captureExceptions);
            return group(spec.id, spec.label, captures);
        });
        const events = action("Start Live Events", "dotnetDiagnostics.startEvents", "Independent EventPipe stream; explicit providers and consent");
        return [group("monitoring", "Monitoring", [start, stop, events]), ...groups, captureDiagnostics, analyzeDumpFile];
    }
}

export async function deactivate(): Promise<void> {
    await activeController?.shutdown();
    activeController = undefined;
}

async function confirmObjectValueReveal(address: string): Promise<boolean> {
    return await vscode.window.showWarningMessage(
        `Reveal bounded string, field and array values for object ${address}? Values may contain passwords, tokens or personal data. This authorizes only this request, separately from live-read permission. A live read may suspend the process again; dump reads are offline. Values are hidden when you close or navigate away and are not cached in navigation history or included in redacted exports.`,
        { modal: true }, "Reveal Values Once", "Cancel",
    ) === "Reveal Values Once";
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
    body { color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); padding: 0 1.2rem 1.5rem; }
    header { margin:.8rem 0; }
    .controls { display:flex; gap:.45rem; align-items:center; flex-wrap:wrap; }
    .panel-actions { display:flex; gap:.45rem; align-items:center; flex-wrap:wrap; margin:.7rem 0; }
    .workspace-controls, .workspace-view-controls { display:flex; gap:.45rem; align-items:center; flex-wrap:wrap; margin:.6rem 0; }
    .workspace-evidence-status { color:var(--vscode-descriptionForeground); margin:.45rem 0; }
    #workspaceCanvas > section { margin-top:.6rem; }
    #workspaceCanvas > section[hidden], #workspaceGcCapture[hidden] { display:none; }
    #workspaceLoadView[hidden] { display:none; }
    #resultThreads.workspace-thread-snapshot .evidence-view-switcher, #resultThreads .statics-form[hidden] { display:none; }
    .cpu-export-actions { border:1px solid var(--vscode-panel-border); border-radius:4px; padding:.4rem .6rem; }
    button { color:var(--vscode-button-foreground); background:var(--vscode-button-background); border:0; border-radius:2px; padding:.45rem .75rem; }
    button:hover { background:var(--vscode-button-hoverBackground); }
    button:disabled { opacity:.55; }
    .export-controls { margin:.6rem 0; }
    ${ACTION_AVAILABILITY_STYLES}
    .result-tabs { display:flex; gap:.2rem; overflow-x:auto; position:sticky; top:0; z-index:2; padding:.45rem 0; background:var(--vscode-editor-background); border-bottom:1px solid var(--vscode-panel-border); }
    .result-tabs button { flex:0 0 auto; background:transparent; color:var(--vscode-foreground); border-bottom:2px solid transparent; }
    .result-tabs button[aria-selected="true"] { color:var(--vscode-textLink-foreground); border-bottom-color:var(--vscode-focusBorder); }
    .query-view { margin:.8rem 0; padding:.75rem; border:1px solid var(--vscode-panel-border); border-radius:4px; }
    .query-view h3, .query-view h4 { margin:.1rem 0 .5rem; }
    .query-view-section { margin-top:.75rem; }
    .query-view-table-wrap { max-width:100%; overflow-x:auto; }
    .query-view-table { min-width:32rem; }
    .query-view-table.cpu-query-table { min-width:48rem; table-layout:fixed; }
    .cpu-query-table .cpu-query-symbol { width:31%; }
    .cpu-query-table .cpu-query-module { width:17%; }
    .cpu-query-table .cpu-query-number { width:6.5rem; }
    .cpu-query-table .cpu-query-action { width:9rem; }
    .cpu-query-table th.cpu-query-number { overflow-wrap:normal; word-break:normal; }
    .cpu-query-table td.cpu-query-number { white-space:nowrap; }
    .cpu-query-table .cpu-query-symbol, .cpu-query-table .cpu-query-module { overflow-wrap:normal; word-break:normal; }
    .cpu-query-table .cpu-query-symbol span, .cpu-query-table .cpu-query-module span { display:inline-block; white-space:nowrap; overflow-wrap:normal; }
    .cpu-query-table .cpu-query-action button { white-space:nowrap; padding:.35rem .45rem; font-size:.8rem; }
    .cpu-query-group-table .cpu-query-module { width:52%; }
    .cpu-query-group-table .cpu-query-number { width:24%; }
    .cpu-query-summary-table .cpu-query-category { width:56%; overflow-wrap:anywhere; word-break:normal; }
    .cpu-query-summary-table .cpu-query-number { width:22%; }
    .query-view-provenance { color:var(--vscode-descriptionForeground); }
    .query-view-error { border-color:var(--vscode-errorForeground); }
    ${CPU_PROFILE_STYLES}
    #status, #quality { color:var(--vscode-descriptionForeground); margin:.6rem 0; }
    #value { font-size:1.5rem; font-weight:600; margin:.6rem 0; }
    canvas { width:100%; height:280px; border-bottom:1px solid var(--vscode-panel-border); }
    #error { color:var(--vscode-errorForeground); white-space:pre-wrap; }
    section.panel { margin-top:.8rem; }
    section.panel h3 { margin-bottom:.3rem; }
    .result-tab-panel[hidden] { display:none; }
    table { border-collapse:collapse; width:100%; font-size:.9rem; }
    th, td { text-align:left; padding:.25rem .6rem; border-bottom:1px solid var(--vscode-panel-border); }
    #gcHeadline { margin:.3rem 0 .6rem; color:var(--vscode-descriptionForeground); }
    #captureStatus { color:var(--vscode-descriptionForeground); margin:.3rem 0; }
    .empty { display:none; }
    .statics-form { margin:.5rem 0; display:flex; gap:.5rem; align-items:center; flex-wrap:wrap; }
    .statics-error { color: var(--vscode-errorForeground); }
    .events-warning { border-left:3px solid var(--vscode-editorWarning-foreground); padding:.4rem .7rem; margin:.6rem 0; }
    .events-controls { display:flex; gap:.45rem; align-items:center; flex-wrap:wrap; margin:.7rem 0; }
    .events-table-wrap { max-height:32rem; overflow:auto; border:1px solid var(--vscode-panel-border); }
    #eventsTable { table-layout:fixed; }
    #eventsTable th:nth-child(1) { width:10rem; }
    #eventsTable th:nth-child(2) { width:9rem; }
    #eventsTable th:nth-child(3) { width:7rem; }
    #eventsTable td { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
    #eventsTable tr[aria-selected="true"] { background:var(--vscode-list-activeSelectionBackground); color:var(--vscode-list-activeSelectionForeground); }
    #eventsQuality, #eventsStatus { color:var(--vscode-descriptionForeground); margin:.5rem 0; }
    #eventDetails { margin:.75rem 0; padding:.75rem; border:1px solid var(--vscode-panel-border); border-radius:4px; }
    #eventDetails pre { white-space:pre-wrap; overflow-wrap:anywhere; }
    ${QUERY_VIEW_STYLES}
    ${WEBVIEW_FIELD_STYLES}
  </style>
</head>
<body>
  <header>
    <h2>.NET Diagnostics</h2>
    <div>${escapeHtml(label)}</div>
  </header>
  <div id="status" role="status">Live counters are ready. Select Start to begin collection; other captures run only when requested.</div>
  <nav class="result-tabs" role="tablist" aria-label="Diagnostic results">
    <button id="resultTabLive" role="tab" aria-selected="true" aria-controls="resultLive">Live</button>
    <button id="resultTabWorkspace" role="tab" aria-selected="false" aria-controls="resultWorkspace">Workspace</button>
    <button id="resultTabExceptions" role="tab" aria-selected="false" aria-controls="resultExceptions">Exceptions</button>
    <button id="resultTabDiagnostics" role="tab" aria-selected="false" aria-controls="resultDiagnostics">Diagnostics</button>
    <button id="resultTabEvents" role="tab" aria-selected="false" aria-controls="resultEvents">Events</button>
  </nav>
  <section id="resultWorkspace" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabWorkspace" hidden>
    <div class="workspace-controls">
      <label for="workspaceDomain">Domain</label>
      <select id="workspaceDomain">
        <option value="cpu">CPU</option>
        <option value="memory">Memory &amp; GC</option>
        <option value="threads">Threads &amp; waits</option>
      </select>
      <label for="workspaceEvidenceKind">Evidence</label>
      <select id="workspaceEvidenceKind" aria-label="Select current evidence kind"></select>
    </div>
    <div id="workspaceEvidenceStatus" class="workspace-evidence-status" role="status">No evidence selected.</div>
    <div class="workspace-view-controls">
      <label for="workspaceView">View</label>
      <select id="workspaceView" aria-label="Select a view"></select>
      <button id="workspaceLoadView" type="button" hidden>Load view</button>
    </div>
    <div id="workspaceGcCapture" hidden>
      <div class="panel-actions"><button id="captureGc" type="button">Capture GC evidence…</button></div>
      <div id="workspaceGcStatus" role="status"></div>
    </div>
    <div id="workspaceCaptureStatus" role="status"></div>
    <div id="workspaceThreadViewStatus" role="status"></div>
    <div id="workspaceCanvas"></div>
  </section>
  <section id="resultLive" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabLive">
    <div class="panel-actions">
      <label for="metric">Counter</label><select id="metric" aria-label="Select counter"></select>
      <button id="start">Start</button><button id="stop" disabled>Stop</button>
    </div>
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
  </section>
  <section id="resultCpu" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabWorkspace" data-export-kind="cpu" data-evidence-id="" hidden>
    <div class="panel-actions">
      <button id="captureCpu" type="button">Capture CPU sample</button>
      <details class="cpu-export-actions">
        <summary>CPU exports</summary>
        <div class="controls export-controls">
          <button id="captureCpuTrace" type="button">Capture CPU + raw trace</button>
          <button id="exportSummary" type="button">Export redacted investigation summary</button>
          <button id="exportTrace" type="button">Export raw trace locally</button>
        </div>
      </details>
    </div>
    <div id="captureStatus" role="status"></div>
    <p id="cpuEvidencePrerequisite" role="status">Capture CPU to enable measurement exports.</p>
    ${RESULT_EXPORT_BUTTONS}
    <p id="cpuSamplingDisclaimer" class="query-view-provenance">
      CPU percentages are shares of captured stack samples, not measured CPU time or utilization.
      Total includes descendants; Self excludes descendants. Percentages use the full captured-sample denominator and do not change with focus or search.
    </p>
    <div id="captureResult" class="empty">
      <div id="captureHeadline"></div>
      <details id="cpuHotspotSummary">
      <summary>Capture hotspot summary</summary>
      <table id="captureTable">
        <thead><tr><th>Module</th><th>Method</th><th>Inclusive</th><th>Exclusive</th><th></th></tr></thead>
        <tbody id="captureBody"></tbody>
      </table>
      </details>
      <div id="cpuQueryStatus" role="status"></div>
      <div id="cpuQueryViews" class="query-views"></div>
    </div>
  </section>
  <section id="resultAllocation" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabAllocation" hidden>
    <div class="panel-actions"><button id="captureAllocation" type="button">Capture allocations…</button></div>
    <p class="query-view-provenance">Sampled allocation traffic, not a heap snapshot or exact object tracker.
      Start with a short capture: collection, runtime rundown and trace conversion can be expensive.
      Navigation reuses this capture without collecting again. All types are combined in the profile.</p>
    <div id="captureAllocationStatus" role="status"></div>
    <details><summary>Allocation types, sites and capture quality</summary><div id="allocationCaptureViews"></div></details>
    <p id="allocationEvidencePrerequisite" role="status">Capture allocations to enable profile exploration.</p>
    <div id="allocationExplorer" hidden>
    <div id="allocationQueryStatus" role="status"></div>
    <div id="allocationQueryViews" class="query-views"></div>
    </div>
  </section>
  <section id="resultExceptions" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabExceptions" hidden>
    <div class="panel-actions"><button id="captureExceptions" type="button">Capture exceptions…</button></div>
    <div id="captureExceptionsStatus" role="status"></div>
    <div id="exceptionCaptureViews" class="query-views"></div>
  </section>
  <section id="resultDiagnostics" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabDiagnostics" hidden>
    <div class="panel-actions"><button id="captureDiagnostics" type="button">Capture diagnostics…</button></div>
    <div id="captureDiagnosticsStatus" role="status"></div>
    <div id="diagnosticsCaptureViews"></div>
  </section>
  <section id="resultHeap" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabHeap" hidden>
    ${LIVE_READ_CONSENT_HTML}
    <div class="panel-actions"><button id="captureHeap" type="button">Capture heap snapshot…</button></div>
    <div id="captureHeapStatus"></div>
    <div id="captureHeapResult" class="empty">
      <div id="captureHeapHeadline"></div>
      <div id="heapWorkspace" class="heap-workspace" data-focused="false">
      <div id="heapTypeList" class="heap-type-list">
      <table id="captureHeapTable">
        <thead><tr><th>Type</th><th>Instances</th><th>Bytes</th></tr></thead>
        <tbody id="captureHeapBody"></tbody>
      </table>
      </div>
      <section id="heapDetailPane" class="heap-detail-pane" aria-label="Heap details" tabindex="-1">
      <button id="backToHeapTypes" type="button">Back to types</button>
      <div id="heapQueryViews" class="query-views"></div>
      ${ADDRESS_NAVIGATION_HTML}
      </section>
      </div>
    </div>
  </section>
  <section id="resultThreads" hidden>
    <div class="panel-actions"><button id="captureThreadSnapshot" type="button">Capture thread snapshot…</button></div>
    <div id="captureThreadStatus"></div>
    <div id="captureThreadResult" class="empty">
      <div id="captureThreadHeadline"></div>
      <details><summary>Inline thread summary</summary>
      <table id="captureThreadTable">
        <thead><tr><th>Thread</th><th>State</th><th>Wait reason</th><th>Top frame</th></tr></thead>
        <tbody id="captureThreadBody"></tbody>
      </table>
      </details>
      <div class="statics-form">
        <input id="threadStaticsType" type="text" maxlength="512" placeholder="Exact type name for thread statics, e.g. MyApp.Cache" />
        <button id="threadStaticsButton" type="button">Query thread statics</button>
        <span id="threadStaticsError" class="statics-error"></span>
      </div>
      <div id="threadQueryViews" class="query-views"></div>
    </div>
  </section>
  <section id="resultEvents" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabEvents" hidden>
    <div class="events-warning" role="note">Event payloads may contain secrets or personal data. They are held only in bounded in-memory windows and are not automatically saved or logged. Native Windows ETW is not collected.</div>
    <div class="events-controls">
      <button id="startEvents" type="button">Start Live Events…</button>
      <button id="stopEvents" type="button" disabled>Stop</button>
      <button id="eventsFollow" type="button" disabled>Pause following</button>
      <button id="eventsClear" type="button">Clear</button>
      <label for="eventsSearch">Search</label><input id="eventsSearch" type="search" maxlength="${LIVE_EVENT_SEARCH_MAX_CHARS}" placeholder="Search retained events" />
    </div>
    <div id="eventsStatus" role="status">Not running.</div>
    <div id="eventsQuality" role="status"></div>
    <div class="events-controls">
      <button id="eventsPrevious" type="button" disabled>Newer</button>
      <span id="eventsPage" aria-live="polite">No events</span>
      <button id="eventsNext" type="button" disabled>Older</button>
    </div>
    <div class="events-table-wrap">
      <table id="eventsTable">
        <thead><tr><th>Source time (UTC)</th><th>Provider</th><th>Event</th><th>Level</th><th>Summary</th></tr></thead>
        <tbody id="eventsBody"></tbody>
      </table>
    </div>
    <section id="eventDetails" aria-label="Selected event details"><p>Select an event to inspect its bounded payload.</p></section>
  </section>

  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    ${ACTION_AVAILABILITY_SCRIPT}
    ${PROFILE_ACTIONS_SCRIPT}
    const updateEvidenceActions = installProfileActions();
    ${RESULT_EXPORT_SCRIPT}
    for (const type of ["captureCpuTrace", "exportSummary", "exportTrace"])
      document.getElementById(type).addEventListener("click", () => vscode.postMessage({ type }));
    const metricSelect = document.getElementById('metric');
    const startButton = document.getElementById('start');
    const stopButton = document.getElementById('stop');
    const captureCpuButton = document.getElementById('captureCpu');
    const captureExceptionsButton = document.getElementById('captureExceptions');
    const captureExceptionsStatus = document.getElementById('captureExceptionsStatus');
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
    const captureDiagnosticsStatus = document.getElementById('captureDiagnosticsStatus');
    const captureDiagnosticsButton = document.getElementById('captureDiagnostics');
    const captureResultElement = document.getElementById('captureResult');
    const captureHeadline = document.getElementById('captureHeadline');
    const captureBody = document.getElementById('captureBody');
    const cpuQueryStatus = document.getElementById('cpuQueryStatus');
    const cpuQueryViews = document.getElementById('cpuQueryViews');
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
    ${QUERY_VIEW_RENDERER_SCRIPT}
    ${CAPTURE_VIEW_RENDERER_SCRIPT}
    ${CPU_PROFILE_SCRIPT}
    ${LIVE_TIME_NAVIGATION_SCRIPT}
    ${LIVE_READ_CONSENT_SCRIPT}
    let cpuProfileResult;
    const cpuProfileState = { mode: 'tree', search: '', focus: [], selected: undefined };
    let allocationProfileResult;
    const allocationProfileState = { mode: 'tree', search: '', focus: [], selected: undefined };
    const resultTabs = Array.from(document.querySelectorAll('.result-tabs [role="tab"]'));
    const workspaceDomain = document.getElementById('workspaceDomain');
    const workspaceEvidenceKind = document.getElementById('workspaceEvidenceKind');
    const workspaceEvidenceStatus = document.getElementById('workspaceEvidenceStatus');
    const workspaceView = document.getElementById('workspaceView');
    const workspaceLoadView = document.getElementById('workspaceLoadView');
    const workspaceCanvas = document.getElementById('workspaceCanvas');
    const workspaceGcCapture = document.getElementById('workspaceGcCapture');
    const workspaceGcStatus = document.getElementById('workspaceGcStatus');
    const workspaceCaptureStatus = document.getElementById('workspaceCaptureStatus');
    const workspaceThreadViewStatus = document.getElementById('workspaceThreadViewStatus');
    const genericCaptureViews = document.getElementById('diagnosticsCaptureViews');
    const workspacePanels = {
      cpu: document.getElementById('resultCpu'),
      allocation: document.getElementById('resultAllocation'),
      heap: document.getElementById('resultHeap'),
      'thread-snapshot': document.getElementById('resultThreads'),
    };
    workspacePanels['thread-snapshot'].classList.add('workspace-thread-snapshot');
    for (const panel of Object.values(workspacePanels)) {
      panel.classList.remove('result-tab-panel');
      panel.removeAttribute('role');
      panel.removeAttribute('aria-labelledby');
      workspaceCanvas.appendChild(panel);
    }
    const threadSnapshotViewLabels = {
      snapshot: 'Snapshot summary',
      deadlocks: 'Inferred deadlock candidates',
      'unique-stacks': 'Grouped snapshot stacks',
      'wait-chains': 'Snapshot wait chains',
      threadpool: 'Snapshot thread-pool query',
      'thread-statics': 'Thread statics',
    };
    const genericCaptureLabels = {
      allocation: 'Allocation profile',
      off_cpu: 'Off-CPU blocking',
      contention: 'Lock contention',
      threadpool: 'Thread-pool capture',
      gc: 'Garbage collections',
      'gc-activities': 'GC and activities',
      requests: 'In-flight requests',
      kestrel: 'Kestrel',
      db: 'Database',
      networking: 'Networking',
    };
    const isThreadCapture = kind => ['off_cpu', 'contention', 'threadpool'].includes(kind);
    const usesWorkspaceForGenericCapture = kind => kind === 'gc' || kind === 'gc-activities' || isThreadCapture(kind);
    let genericCaptureKind;
    let threadSnapshotEvidenceId;
    let threadSnapshotQueryAvailable = false;
    let pendingThreadSnapshotEvidenceId;
    let threadSnapshotView = 'snapshot';
    workspaceGcCapture.removeAttribute('hidden');
    workspaceCanvas.appendChild(workspaceGcCapture);
    let selectedWorkspaceEvidence = 'cpu';
    const workspaceEvidenceStatusByKind = new Map();
    let cpuEvidenceId;
    let allocationEvidenceId;
    let heapEvidenceId;
    let heapEvidenceSource;
    let diagnosticsRequestId;
    let pendingCpuEvidenceId;
    let pendingAllocationEvidenceId;
    let pendingHeapEvidenceId;
    const cpuViewCache = new Map();
    const allocationViewCache = new Map();
    const profileViewLabels = {
      triage: 'Overview',
      'call-tree': 'Call tree',
      'top-methods': 'Top methods',
      'hot-path': 'Hot path',
      'by-module': 'By module',
      'by-namespace': 'By namespace',
    };
    function setWorkspaceStatus(kind, message) {
      const statusKind = kind === 'gc-activities' ? 'gc' : kind;
      workspaceEvidenceStatusByKind.set(statusKind, message);
      if (selectedWorkspaceEvidence === statusKind || selectedWorkspaceEvidence === 'gc-activities' && statusKind === 'gc') {
        workspaceEvidenceStatus.textContent = message;
      }
    }
    function setWorkspaceEvidence(kind) {
      selectedWorkspaceEvidence = kind;
      workspaceDomain.value = kind === 'cpu' ? 'cpu'
        : ['heap', 'allocation', 'gc', 'gc-activities'].includes(kind) ? 'memory' : 'threads';
      workspaceEvidenceKind.replaceChildren();
      const choices = kind === 'cpu'
        ? [['cpu', 'CPU sample']]
        : workspaceDomain.value === 'memory'
          ? [['heap', 'Heap snapshot'], ['allocation', 'Allocation profile'],
            ['gc', 'Garbage collections'], ['gc-activities', 'GC activities']]
          : [['thread-snapshot', 'Thread snapshot'], ['off_cpu', 'Off-CPU blocking'],
            ['contention', 'Lock contention'], ['threadpool', 'Thread-pool capture']];
      for (const [value, label] of choices) {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        workspaceEvidenceKind.appendChild(option);
      }
      workspaceEvidenceKind.value = kind;
      for (const [panelKind, panel] of Object.entries(workspacePanels)) panel.hidden = panelKind !== kind;
      const sharedGcEvidence = kind === 'gc' || kind === 'gc-activities';
      workspaceGcCapture.hidden = !sharedGcEvidence;
      document.getElementById('captureGc').textContent = kind === 'gc-activities'
        ? 'Capture GC activities…' : 'Capture GC evidence…';
      workspaceCaptureStatus.textContent = '';
      const statusKind = kind === 'gc-activities' ? 'gc' : kind;
      workspaceEvidenceStatus.textContent = workspaceEvidenceStatusByKind.get(statusKind)
        || 'No completed evidence is displayed for this kind.';
      workspaceThreadViewStatus.textContent = '';
      if (usesWorkspaceForGenericCapture(kind)) {
        if (genericCaptureKind === kind) workspaceCanvas.appendChild(genericCaptureViews);
        genericCaptureViews.hidden = genericCaptureKind !== kind;
      } else if (genericCaptureViews.parentElement === workspaceCanvas) {
        genericCaptureViews.hidden = true;
      }
      workspaceView.replaceChildren();
      workspaceView.disabled = false;
      workspaceLoadView.hidden = true;
      if (kind === 'cpu' || kind === 'allocation') {
        for (const [view, label] of Object.entries(profileViewLabels)) {
          if (kind === 'allocation' && view === 'triage') continue;
          const option = document.createElement('option');
          option.value = view;
          option.textContent = label;
          workspaceView.appendChild(option);
        }
        const view = activeProfileViews[kind];
        if (!profileViewLabels[view]) {
          const option = document.createElement('option');
          option.value = view;
          option.textContent = cpuQueryLabels[view] || view;
          workspaceView.appendChild(option);
        }
        workspaceView.value = view;
        selectProfileView(kind, view, activeProfileFilters[kind]);
      } else if (kind === 'thread-snapshot') {
        for (const [view, label] of Object.entries(threadSnapshotViewLabels)) {
          const option = document.createElement('option');
          option.value = view;
          option.textContent = label;
          workspaceView.appendChild(option);
        }
        workspaceView.value = threadSnapshotView;
        selectThreadSnapshotView(threadSnapshotView);
      } else if (usesWorkspaceForGenericCapture(kind)) {
        const option = document.createElement('option');
        option.value = 'capture-result';
        option.textContent = 'Capture result';
        workspaceView.appendChild(option);
        workspaceView.value = 'capture-result';
        workspaceView.disabled = true;
      } else {
        const option = document.createElement('option');
        option.value = kind === 'heap' ? 'snapshot' : 'capture-result';
        workspaceView.appendChild(option);
        option.textContent = kind === 'heap' ? 'Snapshot totals' : 'Capture result';
        if (kind === 'heap' && heapEvidenceSource === 'live') {
          const details = document.createElement('option');
          details.value = 'details';
          details.textContent = 'Heap details';
          workspaceView.appendChild(details);
          workspaceView.disabled = false;
        } else workspaceView.disabled = true;
      }
    }
    function selectThreadSnapshotView(view) {
      threadSnapshotView = view;
      const queryViews = document.getElementById('threadQueryViews');
      const staticsForm = document.querySelector('#resultThreads .statics-form');
      const inlineSummary = document.getElementById('captureThreadHeadline');
      const inlineTable = document.getElementById('captureThreadTable')?.closest('details');
      const isSnapshot = view === 'snapshot';
      const isStatics = view === 'thread-statics';
      inlineSummary.hidden = !isSnapshot;
      if (inlineTable) inlineTable.hidden = !isSnapshot;
      staticsForm.hidden = !isStatics;
      const panelId = 'threadQueryViews-' + view;
      const panel = document.getElementById(panelId);
      queryViews.hidden = isSnapshot || !panel;
      if (!isSnapshot && panel) {
        const switcher = queryViews.querySelector('.evidence-view-switcher');
        const tab = switcher?.querySelector('[data-panel="' + panelId + '"]');
        if (tab) tab.click();
        workspaceThreadViewStatus.textContent = '';
      } else if (isStatics) {
        workspaceThreadViewStatus.textContent = threadSnapshotQueryAvailable
          ? 'Enter an exact type name and explicitly query this snapshot. Selecting this view does not run a query or reattach.'
          : 'This snapshot has no retained query handle; capture a thread snapshot before querying statics.';
      } else if (!isSnapshot) {
        workspaceThreadViewStatus.textContent = threadSnapshotEvidenceId
          ? 'This snapshot view has not returned yet. Automatic capture queries are still pending or unavailable; selecting it does not issue another read.'
          : 'Capture a thread snapshot before exploring its views.';
      } else {
        workspaceThreadViewStatus.textContent = threadSnapshotEvidenceId
          ? 'Point-in-time thread snapshot. Thread-pool query evidence is separate from Thread-pool event captures.'
          : 'Capture a thread snapshot to explore returned threads, locks and waits.';
      }
    }
    workspaceDomain.addEventListener('change', () => setWorkspaceEvidence(
      workspaceDomain.value === 'cpu' ? 'cpu' : workspaceDomain.value === 'memory' ? 'heap' : 'thread-snapshot'));
    workspaceEvidenceKind.addEventListener('change', () => setWorkspaceEvidence(workspaceEvidenceKind.value));
    workspaceView.addEventListener('change', () => {
      if (selectedWorkspaceEvidence === 'cpu' || selectedWorkspaceEvidence === 'allocation') {
        selectProfileView(selectedWorkspaceEvidence, workspaceView.value);
      } else if (selectedWorkspaceEvidence === 'heap') {
        navigateHeapDetails(workspaceView.value === 'details');
      } else if (selectedWorkspaceEvidence === 'thread-snapshot') {
        selectThreadSnapshotView(workspaceView.value);
      }
    });
    document.getElementById('backToHeapTypes').addEventListener('click', () => {
      if (selectedWorkspaceEvidence === 'heap') workspaceView.value = 'snapshot';
    });
    workspaceLoadView.addEventListener('click', () => loadSelectedProfileView());
    function selectResultTab(selected) {
      const selectedTab = typeof selected === 'string'
        ? resultTabs.find(button => button.id === 'resultTab' + selected[0].toUpperCase() + selected.slice(1))
        : selected;
      for (const button of resultTabs) {
        const panel = document.getElementById(button.getAttribute('aria-controls'));
        if (!panel) continue;
        const active = button === selectedTab;
        panel.hidden = !active;
        button.setAttribute('aria-selected', String(active));
        button.tabIndex = active ? 0 : -1;
      }
    }
    selectResultTab(resultTabs[0]);
    for (const [index, tab] of resultTabs.entries()) {
      tab.addEventListener('click', () => selectResultTab(tab));
      tab.addEventListener('keydown', event => {
        const next = event.key === 'ArrowRight' ? (index + 1) % resultTabs.length
          : event.key === 'ArrowLeft' ? (index + resultTabs.length - 1) % resultTabs.length
          : event.key === 'Home' ? 0
          : event.key === 'End' ? resultTabs.length - 1 : -1;
        if (next < 0) return;
        event.preventDefault();
        const nextTab = resultTabs[next];
        selectResultTab(nextTab);
        nextTab.focus();
      });
    }
    const eventsStatusElement = document.getElementById('eventsStatus');
    const eventsQualityElement = document.getElementById('eventsQuality');
    const eventsBody = document.getElementById('eventsBody');
    const eventDetails = document.getElementById('eventDetails');
    const eventsStartButton = document.getElementById('startEvents');
    const eventsStopButton = document.getElementById('stopEvents');
    const eventsFollowButton = document.getElementById('eventsFollow');
    const eventsSearch = document.getElementById('eventsSearch');
    const events = [];
    const eventsMaxCount = ${LIVE_EVENT_UI_MAX_COUNT};
    const eventsMaxBytes = ${LIVE_EVENT_UI_MAX_RETAINED_BYTES};
    const eventsPageSize = ${LIVE_EVENT_UI_MAX_MOUNTED_ROWS};
    const eventsBatchMaxBytes = ${LIVE_EVENT_BATCH_MAX_BYTES};
    const eventsEncoder = new TextEncoder();
    let eventsRetainedBytes = 0;
    let eventsUiEvicted = 0;
    let payloadTruncatedEvents = 0;
    let metadataTruncatedEvents = 0;
    let eventsSessionId;
    let selectedEvent;
    let eventsPageIndex = 0;
    let eventsFollowing = true;
    let eventsQuality = null;
    let lastEventsQualityRenderAt = 0;
    let eventsQualityRenderTimer;
    function setEventsStatus(state, text, sessionId) {
      eventsStatusElement.textContent = text || state || '';
      if (state === 'starting') {
        eventsSessionId = undefined;
        eventsQuality = null;
        eventsQualityElement.textContent = '';
      }
      const running = state === 'running' || state === 'starting' || state === 'stopping';
      eventsStartButton.disabled = running;
      eventsStopButton.disabled = state !== 'running';
      if (state === 'running') {
        eventsSessionId = sessionId || eventsSessionId;
        eventsFollowButton.disabled = false;
      } else if (state === 'stopped' || state === 'error') {
        eventsSessionId = undefined;
        eventsFollowButton.disabled = true;
      }
    }
    function eventSize(event) {
      return eventsEncoder.encode(JSON.stringify(event)).length * 2 + 1024;
    }
    function eventSearchText(event) {
      return [event.provider, event.eventName, event.level, ...Object.entries(event.payload).flat()].join(' ').toLocaleLowerCase();
    }
    function renderEventDetails(event) {
      eventDetails.replaceChildren();
      if (!event) {
        const empty = document.createElement('p');
        empty.textContent = 'Select an event to inspect its bounded payload.';
        eventDetails.appendChild(empty);
        return;
      }
      const heading = document.createElement('h3');
      heading.textContent = event.provider + ' · ' + event.eventName + ' (' + event.eventId + ', v' + event.version + ')';
      eventDetails.appendChild(heading);
      const timing = document.createElement('p');
      timing.textContent = 'Source: ' + event.timestamp + ' · Received: ' + event.receivedAt
        + (event.threadId === null ? '' : ' · Thread ' + event.threadId);
      eventDetails.appendChild(timing);
      const exceptionFields = ['ExceptionType', 'ExceptionMessage', 'ExceptionEIP', 'ExceptionHRESULT', 'ExceptionFlags', 'ClrInstanceID'];
      const payloadNames = Object.keys(event.payload).sort();
      const isManagedException = event.provider === 'Microsoft-Windows-DotNETRuntime'
        && event.eventId === 80 && event.version === 1
        && payloadNames.length === exceptionFields.length
        && payloadNames.every((name, index) => name === exceptionFields.slice().sort()[index]);
      if (isManagedException) {
        const note = document.createElement('p');
        note.textContent = 'Schema-verified managed exception event. Exception messages may contain sensitive data.';
        eventDetails.appendChild(note);
      }
      const payload = document.createElement('pre');
      payload.textContent = payloadNames.length
        ? payloadNames.map(name => name + ': ' + event.payload[name]).join('\\n')
        : event.payloadFieldsUnavailable
          ? '(payload bytes were present, but decoded fields are unavailable)'
          : '(no supported payload fields)';
      eventDetails.appendChild(payload);
      const quality = document.createElement('p');
      quality.textContent = [
        event.payloadFieldsUnavailable ? 'Decoded payload fields unavailable; payload completeness is unknown' : '',
        event.metadataTruncated ? 'Event metadata truncated' : '',
        event.payloadTruncated ? 'Payload truncated or contained unsupported values' : '',
        event.omittedPayloadFields ? event.omittedPayloadFields + ' payload fields omitted' : '',
        event.omittedPayloadCharacters ? event.omittedPayloadCharacters + ' payload characters omitted' : '',
        event.unsupportedPayloadValues ? event.unsupportedPayloadValues + ' unsupported payload values' : '',
      ].filter(Boolean).join(' · ') || 'Payload projection complete for supported fields.';
      eventDetails.appendChild(quality);
    }
    function renderEvents() {
      const search = eventsSearch.value.slice(0, ${LIVE_EVENT_SEARCH_MAX_CHARS}).trim().toLocaleLowerCase();
      const filtered = search ? events.filter(item => eventSearchText(item).includes(search)) : events;
      const pageCount = Math.max(1, Math.ceil(filtered.length / eventsPageSize));
      eventsPageIndex = Math.min(eventsPageIndex, pageCount - 1);
      const end = Math.max(0, filtered.length - eventsPageIndex * eventsPageSize);
      const start = Math.max(0, end - eventsPageSize);
      const page = filtered.slice(start, end).reverse();
      eventsBody.replaceChildren();
      for (const item of page) {
        const row = document.createElement('tr');
        row.tabIndex = 0;
        row.setAttribute('aria-selected', String(item === selectedEvent));
        const cells = [item.timestamp, item.provider, item.eventName + ' (' + item.eventId + ')', item.level,
          Object.entries(item.payload).slice(0, 2).map(([name, value]) => name + ': ' + value).join(' · ')
          || (item.payloadTruncated ? 'Payload fields omitted' : '')];
        for (const text of cells) {
          const cell = document.createElement('td');
          cell.textContent = text;
          row.appendChild(cell);
        }
        row.addEventListener('click', () => {
          selectedEvent = item;
          renderEvents();
        });
        row.addEventListener('keydown', event => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            selectedEvent = item;
            renderEvents();
          }
        });
        eventsBody.appendChild(row);
      }
      document.getElementById('eventsPage').textContent = filtered.length
        ? 'Showing ' + (start + 1) + '–' + end + ' of ' + filtered.length
          + ' matching events (' + events.length + ' retained)'
        : 'No matching events (' + events.length + ' retained)';
      document.getElementById('eventsPrevious').disabled = eventsPageIndex === 0;
      document.getElementById('eventsNext').disabled = end >= filtered.length;
      renderEventDetails(selectedEvent);
      renderEventsQuality();
    }
    function renderEventsQuality() {
      const clear = document.getElementById('eventsClear');
      clear.setAttribute('aria-disabled', String(events.length === 0));
      clear.title = events.length === 0 ? 'No retained events to clear.' : '';
      if (!eventsQuality) return;
      const now = Date.now();
      const waitMs = 1000 - (now - lastEventsQualityRenderAt);
      if (waitMs > 0) {
        if (eventsQualityRenderTimer === undefined) {
          eventsQualityRenderTimer = setTimeout(() => {
            eventsQualityRenderTimer = undefined;
            renderEventsQuality();
          }, waitMs);
        }
        return;
      }
      if (eventsQualityRenderTimer !== undefined) {
        clearTimeout(eventsQualityRenderTimer);
        eventsQualityRenderTimer = undefined;
      }
      lastEventsQualityRenderAt = now;
        eventsQualityElement.textContent = [
          eventsQuality.eventPipeLossAvailable
            ? eventsQuality.eventPipeEventsLost + ' EventPipe source events lost'
            : 'EventPipe source loss count unknown',
          eventsQuality.coreQueueDropped + ' Core queue drops',
          eventsQuality.cliDispatchQueueDropped + ' CLI dispatch drops',
          eventsQuality.hostDeliveryDropped + ' extension host delivery drops',
          eventsUiEvicted + ' UI-window evictions',
          payloadTruncatedEvents + ' payload-truncated events',
          metadataTruncatedEvents + ' metadata-truncated events',
        ].join(' · ') + ' (layer counts are separate, not a sum).';
    }
    function receiveEventsBatch(message) {
      if (typeof message.sessionId !== 'string' || !Number.isSafeInteger(message.batchId)
        || !Array.isArray(message.events) || message.events.length > 64) return;
      if (eventsSessionId && eventsSessionId !== message.sessionId) return;
      if (eventsEncoder.encode(JSON.stringify(message)).length > eventsBatchMaxBytes) return;
      eventsSessionId = message.sessionId;
      for (const item of message.events) {
        if (item.payloadTruncated) payloadTruncatedEvents = Math.min(Number.MAX_SAFE_INTEGER, payloadTruncatedEvents + 1);
        if (item.metadataTruncated) metadataTruncatedEvents = Math.min(Number.MAX_SAFE_INTEGER, metadataTruncatedEvents + 1);
      }
      if (message.quality && typeof message.quality.eventPipeLossAvailable === 'boolean'
        && Number.isSafeInteger(message.quality.coreQueueDropped)
        && Number.isSafeInteger(message.quality.cliDispatchQueueDropped)
        && Number.isSafeInteger(message.quality.hostDeliveryDropped)) {
        eventsQuality = message.quality;
      }
      for (const item of message.events) {
        const bytes = eventSize(item);
        events.push(item);
        eventsRetainedBytes += bytes;
      }
      while (events.length > eventsMaxCount || eventsRetainedBytes > eventsMaxBytes) {
        const removed = events.shift();
        eventsUiEvicted = Math.min(Number.MAX_SAFE_INTEGER, eventsUiEvicted + 1);
        if (removed === selectedEvent) selectedEvent = undefined;
        eventsRetainedBytes -= eventSize(removed);
      }
      if (!selectedEvent) renderEventDetails(undefined);
      if (eventsFollowing) {
        eventsPageIndex = 0;
        renderEvents();
      } else {
        renderEventsQuality();
      }
      vscode.postMessage({ type: 'ackEventBatch', sessionId: message.sessionId, batchId: message.batchId });
    }
    eventsStartButton.addEventListener('click', () => vscode.postMessage({ type: 'startEvents' }));
    eventsStopButton.addEventListener('click', () => vscode.postMessage({ type: 'stopEvents' }));
    document.getElementById('eventsClear').addEventListener('click', () => {
      events.length = 0;
      eventsRetainedBytes = 0;
      selectedEvent = undefined;
      eventsPageIndex = 0;
      renderEvents();
    });
    eventsFollowButton.addEventListener('click', () => {
      eventsFollowing = !eventsFollowing;
      eventsFollowButton.textContent = eventsFollowing ? 'Pause following' : 'Follow latest';
      if (eventsFollowing) eventsPageIndex = 0;
      renderEvents();
    });
    eventsSearch.addEventListener('input', () => { eventsPageIndex = 0; renderEvents(); });
    document.getElementById('eventsPrevious').addEventListener('click', () => {
      eventsPageIndex = Math.max(0, eventsPageIndex - 1);
      renderEvents();
    });
    document.getElementById('eventsNext').addEventListener('click', () => {
      eventsPageIndex++;
      eventsFollowing = false;
      eventsFollowButton.textContent = 'Follow latest';
      renderEvents();
    });
    renderEvents();
    function activateCaptureTab(name) {
      if (name === 'cpu' || name === 'allocation' || name === 'heap' || name === 'gc' || name === 'gc-activities'
        || name === 'thread-snapshot' || isThreadCapture(name)) {
        selectResultTab('Workspace');
        setWorkspaceEvidence(name);
      } else selectResultTab(name);
    }
    function setGenericCaptureOwner(kind, evidenceId) {
      if (genericCaptureKind && genericCaptureKind !== kind) {
        const currentLabel = genericCaptureLabels[kind] || kind;
        setWorkspaceStatus(genericCaptureKind, 'Not current: the shared generic capture result was replaced by ' + currentLabel + '.');
        if (genericCaptureKind === 'gc' || genericCaptureKind === 'gc-activities') {
          workspaceGcStatus.textContent = 'Replaced: the shared generic capture result now contains ' + currentLabel + '.';
        }
      }
      genericCaptureKind = kind;
      genericCaptureViews.dataset.evidenceId = evidenceId || '';
      genericCaptureViews.dataset.captureKind = kind;
      const label = genericCaptureLabels[kind] || kind;
      setWorkspaceStatus(kind, 'Current generic capture result: ' + label + '.');
      genericCaptureViews.hidden = false;
      if (usesWorkspaceForGenericCapture(kind)) workspaceCanvas.appendChild(genericCaptureViews);
      else document.getElementById('resultDiagnostics').appendChild(genericCaptureViews);
      captureDiagnosticsStatus.textContent = usesWorkspaceForGenericCapture(kind)
        ? 'Current result is in Workspace → ' + (isThreadCapture(kind) ? 'Threads & waits' : 'Memory & GC') + '.'
        : 'Current result is in Diagnostics.';
    }
    const cpuQueryLabels = {
      triage: 'CPU overview',
      'call-tree': 'Call tree',
      'top-methods': 'Top methods',
      'hot-path': 'Hot path',
      'by-module': 'By module',
      'by-namespace': 'By namespace',
      'caller-callee': 'Callers and callees',
    };
    function appendCpuQuerySymbol(cell, value) {
      const segments = String(value).split(/(?<=[.,:;<>(),])|(?<=[a-z0-9])(?=[A-Z])/);
      for (let index = 0; index < segments.length; index++) {
        const segment = segments[index];
        if (!segment) continue;
        const segmentNode = document.createElement('span');
        segmentNode.textContent = segment;
        cell.appendChild(segmentNode);
        const punctuationBreak = /[.,:;<>(),]$/.test(segment);
        const camelCaseBreak = /[a-z0-9]$/.test(segment) && /^[A-Z]/.test(segments[index + 1] || '');
        if (punctuationBreak || camelCaseBreak) {
          cell.appendChild(document.createElement('wbr'));
        }
      }
    }
    function renderCpuQuery(view, result, isError, kind = 'cpu') {
      const allocation = kind === 'allocation';
      const target = allocation ? document.getElementById('allocationQueryViews') : cpuQueryViews;
      const profileResult = allocation ? allocationProfileResult : cpuProfileResult;
      const profileState = allocation ? allocationProfileState : cpuProfileState;
      const queryType = allocation ? 'queryAllocation' : 'queryCpu';
      const unit = allocation ? 'estimated bytes' : 'samples';
      const section = document.createElement('section');
      section.className = isError ? 'query-view query-view-error' : 'query-view';
      const heading = document.createElement('h3');
      heading.textContent = cpuQueryLabels[view] || view;
      section.appendChild(heading);
      if ((view !== 'call-tree' || isError) && profileResult) {
        const back = document.createElement('button');
        back.type = 'button';
        back.textContent = allocation ? 'Back to allocation profile' : 'Back to CPU profile';
        back.addEventListener('click', () => {
          activeProfileViews[kind] = 'call-tree';
          activeProfileFilters[kind] = profileResultFilters[kind];
          const selector = document.getElementById('workspaceView');
          if (selector) selector.value = 'call-tree';
          renderCpuQuery('call-tree', profileResult, false, kind);
        });
        section.appendChild(back);
      }
      if (isError) {
        const message = document.createElement('p');
        message.textContent = String(result);
        section.appendChild(message);
        target.replaceChildren(section);
        return;
      }
      const data = result && result.data && typeof result.data === 'object' ? result.data : {};
      if (typeof result.summary === 'string') {
        const summary = document.createElement('p');
        summary.className = 'query-view-provenance';
        summary.textContent = view === 'call-tree'
          ? result.summary.replace('The handle retains the full tree.', 'Only loaded nodes are displayed here.')
          : result.summary;
        section.appendChild(summary);
      }
      function table(title, rows, columns, action) {
        const wrapper = document.createElement('section');
        wrapper.className = 'query-view-section';
        const tableTitle = document.createElement('h4');
        tableTitle.textContent = title;
        wrapper.appendChild(tableTitle);
        const tableNode = document.createElement('table');
        tableNode.className = 'query-view-table cpu-query-table'
          + (title === 'Modules' || title === 'Namespaces' ? ' cpu-query-group-table' : '')
          + (title === 'Wait categories' ? ' cpu-query-summary-table' : '')
          + (action ? ' cpu-query-action-table' : '');
        const head = document.createElement('tr');
        for (const [label] of columns) {
          const cell = document.createElement('th');
          cell.scope = 'col';
          cell.textContent = allocation ? label.replace(/samples/gi, 'estimated bytes') : label;
          cell.className = cpuQueryColumnClass(label);
          head.appendChild(cell);
        }
        if (action) {
          const cell = document.createElement('th');
          cell.scope = 'col';
          cell.textContent = 'Drill down';
          cell.className = 'cpu-query-action';
          head.appendChild(cell);
        }
        const thead = document.createElement('thead');
        thead.appendChild(head);
        tableNode.appendChild(thead);
        const body = document.createElement('tbody');
        const safeRows = Array.isArray(rows)
          ? rows.filter(item => item !== null && typeof item === 'object').slice(0, 100)
          : [];
        for (const item of safeRows) {
          const row = document.createElement('tr');
          for (const [label, getValue] of columns) {
            const cell = document.createElement('td');
            const value = getValue(item);
            cell.className = cpuQueryColumnClass(label);
            if ((label === 'Method' || label === 'Module' || label === 'Namespace')
              && value !== undefined && value !== null) {
              appendCpuQuerySymbol(cell, value);
            } else {
              cell.textContent = value === undefined || value === null ? '—' : String(value);
            }
            row.appendChild(cell);
          }
          if (action) {
            const cell = document.createElement('td');
            cell.className = 'cpu-query-action';
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = 'Callers / callees';
            button.setAttribute('data-profile-query', '');
            button.addEventListener('click', () => {
              const rootMethodFilter = action(item);
              activeProfileViews[kind] = 'caller-callee';
              activeProfileFilters[kind] = rootMethodFilter;
              vscode.postMessage({
                type: queryType, evidenceId: allocation ? allocationEvidenceId : cpuEvidenceId,
                view: 'caller-callee', rootMethodFilter,
              });
            });
            cell.appendChild(button);
            row.appendChild(cell);
          }
          body.appendChild(row);
        }
        if (safeRows.length === 0) {
          const row = document.createElement('tr');
          const cell = document.createElement('td');
          cell.colSpan = columns.length + (action ? 1 : 0);
          cell.textContent = 'No ' + unit + ' were available for this view.';
          row.appendChild(cell);
          body.appendChild(row);
        }
        tableNode.appendChild(body);
        const tableWrap = document.createElement('div');
        tableWrap.className = 'query-view-table-wrap';
        tableWrap.appendChild(tableNode);
        wrapper.appendChild(tableWrap);
        section.appendChild(wrapper);
      }
      if (view === 'triage') {
        table('Top busy methods', data.topBusyMethods, [
          ['Method', row => row.method], ['Module', row => row.module],
          ['Self samples', row => row.exclusiveSamples], ['Inclusive samples', row => row.inclusiveSamples],
        ], row => row.method);
        table('Wait categories', data.topWaitCategories, [
          ['Category', row => row.waitReason], ['Samples', row => row.exclusiveSamples],
          ['Share', row => typeof row.exclusivePercent === 'number' ? row.exclusivePercent.toFixed(1) + '%' : undefined],
        ]);
        if (data.hotPathLeaf) table('Dominant hot-path leaf', [data.hotPathLeaf], [
          ['Method', row => row.method], ['Module', row => row.module], ['Samples', row => row.exclusiveSamples],
        ]);
      } else if (view === 'top-methods') {
        table(allocation ? 'Methods ranked by estimated allocation bytes' : 'Methods ranked by sampled time', data.methods, [
          ['Method', row => row.method], ['Module', row => row.module],
          ['Self samples', row => row.exclusiveSamples], ['Inclusive samples', row => row.inclusiveSamples],
          ['Self share', row => typeof row.exclusivePercent === 'number' ? row.exclusivePercent.toFixed(1) + '%' : undefined],
        ], row => row.method);
      } else if (view === 'by-module' || view === 'by-namespace') {
        table(view === 'by-module' ? 'Modules' : 'Namespaces', data.groups, [
          [view === 'by-module' ? 'Module' : 'Namespace', row => row.group],
          ['Self samples', row => row.exclusiveSamples], ['Inclusive samples', row => row.inclusiveSamples],
        ]);
      } else if (view === 'hot-path') {
        table('Dominant call chain', data.frames, [
          ['Method', row => row.method], ['Module', row => row.module],
          ['Inclusive samples', row => row.inclusiveSamples], ['Self samples', row => row.exclusiveSamples],
          ['Share of parent', row => typeof row.fractionOfParentPercent === 'number' ? row.fractionOfParentPercent.toFixed(1) + '%' : undefined],
        ], row => row.method);
      } else if (view === 'caller-callee') {
        table('Callers', data.callers, [
          ['Method', row => row.method], ['Module', row => row.module], ['Samples', row => row.samples],
          ['Share', row => typeof row.percent === 'number' ? row.percent.toFixed(1) + '%' : undefined],
        ]);
        table('Callees', data.callees, [
          ['Method', row => row.method], ['Module', row => row.module], ['Samples', row => row.samples],
          ['Share', row => typeof row.percent === 'number' ? row.percent.toFixed(1) + '%' : undefined],
        ]);
      } else if (view === 'call-tree') {
        if (profileResult !== result) {
          profileState.focus = [];
          profileState.selected = undefined;
          profileState.expanded = undefined;
        }
        if (allocation) allocationProfileResult = result;
        else cpuProfileResult = result;
        profileResultFilters[kind] = activeProfileFilters[kind];
        target.replaceChildren(section);
        renderCpuProfile(section, data, (queryView, method) => {
          activeProfileViews[kind] = queryView;
          activeProfileFilters[kind] = method;
          vscode.postMessage({
            type: queryType, evidenceId: allocation ? allocationEvidenceId : cpuEvidenceId, view: queryView,
            ...(method === undefined ? {} : { rootMethodFilter: method }),
          });
        }, profileState, allocation ? 'allocation-tick-estimated-bytes' : 'samples');
      }
      target.replaceChildren(section);
    }
    function cpuQueryColumnClass(label) {
      if (label === 'Method') return 'cpu-query-symbol';
      if (label === 'Module' || label === 'Namespace') return 'cpu-query-module';
      if (label === 'Category') return 'cpu-query-category';
      return 'cpu-query-number';
    }
    const profileAvailability = { cpu: false, allocation: false };
    const profileQueryBusy = { cpu: false, allocation: false };
    const profileCaptureBusy = { cpu: false, allocation: false };
    const activeProfileViews = { cpu: 'triage', allocation: 'call-tree' };
    const activeProfileFilters = { cpu: undefined, allocation: undefined };
    const profileResultFilters = { cpu: undefined, allocation: undefined };
    function profileCache(kind) { return kind === 'allocation' ? allocationViewCache : cpuViewCache; }
    function profileEvidenceId(kind) { return kind === 'allocation' ? allocationEvidenceId : cpuEvidenceId; }
    function profileViewTarget(kind) {
      return kind === 'allocation' ? document.getElementById('allocationQueryViews') : cpuQueryViews;
    }
    function profileQueryKey(view, rootMethodFilter) {
      return view + ':' + (rootMethodFilter || '');
    }
    function cacheProfileQuery(kind, view, rootMethodFilter, result) {
      const cache = profileCache(kind);
      cache.set(profileQueryKey(view, rootMethodFilter), result);
      while (cache.size > 8) cache.delete(cache.keys().next().value);
    }
    function selectProfileView(kind, view, rootMethodFilter) {
      activeProfileViews[kind] = view;
      activeProfileFilters[kind] = rootMethodFilter;
      const cache = profileCache(kind);
      const cached = cache.get(profileQueryKey(view, rootMethodFilter));
      if (cached) {
        workspaceLoadView.hidden = true;
        renderCpuQuery(view, cached, false, kind);
        return;
      }
      const target = profileViewTarget(kind);
      const section = document.createElement('section');
      section.className = 'query-view';
      const message = document.createElement('p');
      message.className = 'query-view-provenance';
      message.textContent = profileEvidenceId(kind)
        ? 'This view is not loaded for the current capture. Selecting it did not query or read the target.'
        : 'Capture this profile before loading a view.';
      section.appendChild(message);
      target.replaceChildren(section);
      workspaceLoadView.hidden = false;
      workspaceLoadView.disabled = !profileEvidenceId(kind) || !profileAvailability[kind]
        || profileQueryBusy[kind] || profileCaptureBusy[kind];
      workspaceLoadView.textContent = profileQueryBusy[kind] || profileCaptureBusy[kind] ? 'Busy…'
        : profileAvailability[kind] ? 'Load view' : 'Retained profile unavailable';
    }
    function loadSelectedProfileView() {
      const kind = selectedWorkspaceEvidence;
      if (kind !== 'cpu' && kind !== 'allocation') return;
      const evidenceId = profileEvidenceId(kind);
      if (!evidenceId || !profileAvailability[kind] || profileQueryBusy[kind] || profileCaptureBusy[kind]) return;
      const view = workspaceView.value;
      const rootMethodFilter = activeProfileFilters[kind];
      const cache = profileCache(kind);
      if (cache.has(profileQueryKey(view, rootMethodFilter))) {
        selectProfileView(kind, view, rootMethodFilter);
        return;
      }
      workspaceLoadView.disabled = true;
      workspaceLoadView.textContent = 'Loading…';
      const status = kind === 'cpu' ? cpuQueryStatus : document.getElementById('allocationQueryStatus');
      status.textContent = 'Loading ' + (profileViewLabels[view] || view) + ' from the retained capture…';
      vscode.postMessage({
        type: kind === 'allocation' ? 'queryAllocation' : 'queryCpu',
        evidenceId,
        view,
        ...(rootMethodFilter === undefined ? {} : { rootMethodFilter }),
      });
    }
    setWorkspaceEvidence('cpu');
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
    const liveTimeNavigation = installLiveTimeNavigation(canvas, () => counters.get(selectedKey)?.points || [], gcRows, draw);

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
      liveTimeNavigation.update();
      const series = counters.get(selectedKey);
      if (!series || series.points.length === 0) return;
      const points = liveTimeNavigation.filter(series.points);
      const values = points.map(point => point.value).filter(Number.isFinite);
      if (values.length === 0) return;
      const min = Math.min(...values);
      const max = Math.max(...values);
      const range = Math.max(max - min, Math.abs(max) * 0.01, 1e-9);
      const left = 44, right = width - 12, top = 14, bottom = height - 26;
      context.strokeStyle = getComputedStyle(document.body).getPropertyValue('--vscode-charts-blue') || '#3794ff';
      context.lineWidth = 2;
      context.beginPath();
      const firstTime = points[0].receivedAtMs;
      const timeSpan = points[points.length - 1].receivedAtMs - firstTime;
      points.forEach((point, index) => {
        const x = left + (right - left) * (timeSpan > 0 ? (point.receivedAtMs - firstTime) / timeSpan : 1);
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
      liveTimeNavigation.update();
      row.tabIndex = 0;
      const inspect = () => {
        let detail = document.getElementById('selectedGcEvent');
        if (!detail) {
          detail = document.createElement('p');
          detail.id = 'selectedGcEvent';
          detail.setAttribute('role', 'status');
          gcHeadline.parentElement.appendChild(detail);
        }
        detail.textContent = 'Selected GC event: ' + cells.join(' | ') + '. Pause is suspension evidence, not total GC elapsed time.';
      };
      row.addEventListener('click', inspect);
      row.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); inspect(); }
      });
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
      resetEvidenceTable(document.getElementById('captureHeapTable'));
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
      renderHeapTypeNavigation(topTypes);
      enhanceEvidenceTable(document.getElementById('captureHeapTable'));
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
      updateThreadNavigation('snapshot', data || {});
    }

    // The thread-statics view needs an exact type name, so it is requested on demand rather than
    // auto-queried; the extension host validates the message again.
    function setThreadStaticsBusy(busy) {
      threadStaticsButton.disabled = busy || !threadSnapshotQueryAvailable;
      threadStaticsButton.textContent = busy ? 'Querying…' : 'Query thread statics';
    }

    threadStaticsButton.addEventListener('click', () => {
      const typeFilter = threadStaticsInput.value.trim();
      if (!typeFilter) {
        threadStaticsError.textContent = 'Enter an exact type name.';
        return;
      }
      if (!threadSnapshotEvidenceId || !threadSnapshotQueryAvailable) {
        threadStaticsError.textContent = 'A current thread snapshot with a retained query handle is required.';
        return;
      }
      if (threadStaticsButton.disabled) return;
      threadStaticsError.textContent = '';
      setThreadStaticsBusy(true);
      vscode.postMessage({ type: 'queryThreadStatics', evidenceId: threadSnapshotEvidenceId, typeFilter });
    });

    window.addEventListener('message', event => {
      const message = event.data;
      if (!message || typeof message.type !== 'string') return;
      if (message.profiles && typeof message.profiles === 'object') {
        profileAvailability.cpu = message.profiles.cpu === true;
        profileAvailability.allocation = message.profiles.allocation === true;
        if ((selectedWorkspaceEvidence === 'cpu' || selectedWorkspaceEvidence === 'allocation')
          && !profileAvailability[selectedWorkspaceEvidence] && workspaceLoadView.hidden === false) {
          workspaceLoadView.disabled = true;
          workspaceLoadView.textContent = 'Retained profile unavailable';
        }
      }
      if (message.type === 'eventsStatus') {
        setEventsStatus(message.state, message.message, message.sessionId);
        return;
      }
      if (message.type === 'eventsBatch') {
        receiveEventsBatch(message);
        return;
      }
      if (handleAddressNavigationMessage(message)) return;
      if (message.type === 'liveReadConsent') { renderLiveReadConsent(message); return; }
      if (message.type === 'threadStaticsState') {
        if (message.evidenceId !== threadSnapshotEvidenceId) return;
        setThreadStaticsBusy(message.state === 'running');
      } else if (message.type === 'cpuQueryState') {
        if (message.evidenceId !== cpuEvidenceId) return;
        const cpuQueryBusy = message.state === 'running';
        profileQueryBusy.cpu = cpuQueryBusy;
        cpuQueryStatus.textContent = cpuQueryBusy ? 'Loading CPU drilldown…' : '';
        if (!cpuQueryBusy && workspaceLoadView.hidden === false && selectedWorkspaceEvidence === 'cpu') {
          workspaceLoadView.disabled = false;
          workspaceLoadView.textContent = 'Load view';
        }
      } else if (message.type === 'captureStatus' && message.kind === 'cpu') {
        if (typeof message.evidenceId === 'string') pendingCpuEvidenceId = message.evidenceId;
        activateCaptureTab('cpu');
        captureStatusElement.textContent = message.message || message.state || '';
        profileCaptureBusy.cpu = message.state === 'running' || message.state === 'starting';
        if (selectedWorkspaceEvidence === 'cpu' && workspaceLoadView.hidden === false) {
          workspaceLoadView.disabled = profileCaptureBusy.cpu || profileQueryBusy.cpu || !profileAvailability.cpu;
          workspaceLoadView.textContent = profileCaptureBusy.cpu ? 'Capture in progress…'
            : workspaceLoadView.disabled ? 'Retained profile unavailable' : 'Load view';
        }
      } else if (message.type === 'status') {
        const liveStatus = message.message || message.state;
        statusElement.textContent = liveStatus.startsWith('Live counters') ? liveStatus : 'Live counters: ' + liveStatus;
        errorElement.textContent = message.state === 'error' ? (message.message || '') : '';
        updateButtons(message.state === 'running' || message.state === 'starting' || message.state === 'stopping');
      } else if (message.type === 'observation') {
        trackSequence(message.sequence);
        if (message.kind === 'gc') {
          addGcPause(message);
        } else {
          addCounter(message);
        }
      } else if (message.type === 'allocationQueryState') {
        if (message.evidenceId !== allocationEvidenceId) return;
        const busy = message.state === 'running';
        profileQueryBusy.allocation = busy;
        document.getElementById('allocationQueryStatus').textContent = busy ? 'Loading allocation details…' : '';
        if (!busy && workspaceLoadView.hidden === false && selectedWorkspaceEvidence === 'allocation') {
          workspaceLoadView.disabled = false;
          workspaceLoadView.textContent = 'Load view';
        }
      } else if (message.type === 'captureStatus' && message.kind === 'allocation') {
        if (typeof message.evidenceId === 'string') pendingAllocationEvidenceId = message.evidenceId;
        activateCaptureTab('allocation');
        document.getElementById('captureAllocationStatus').textContent = message.message || message.state || '';
        profileCaptureBusy.allocation = message.state === 'running' || message.state === 'starting';
        if (selectedWorkspaceEvidence === 'allocation' && workspaceLoadView.hidden === false) {
          workspaceLoadView.disabled = profileCaptureBusy.allocation || profileQueryBusy.allocation || !profileAvailability.allocation;
          workspaceLoadView.textContent = profileCaptureBusy.allocation ? 'Capture in progress…'
            : workspaceLoadView.disabled ? 'Retained profile unavailable' : 'Load view';
        }
      } else if (message.type === 'capture' && message.kind === 'allocation') {
        if (message.evidenceId !== pendingAllocationEvidenceId) return;
        activateCaptureTab('allocation');
        profileCaptureBusy.allocation = false;
        allocationEvidenceId = typeof message.evidenceId === 'string' ? message.evidenceId : undefined;
        allocationViewCache.clear();
        activeProfileViews.allocation = 'call-tree';
        activeProfileFilters.allocation = undefined;
        document.getElementById('resultAllocation').dataset.evidenceId = allocationEvidenceId || '';
        setWorkspaceStatus('allocation', 'Current evidence: allocation profile · ' + (message.result?.kind || 'capture complete')
          + '. Values are estimated sampled allocation bytes, not CPU samples or retained heap.');
        allocationProfileResult = undefined;
        allocationProfileState.focus = [];
        allocationProfileState.selected = undefined;
        allocationProfileState.search = '';
        allocationProfileState.expanded = undefined;
        document.getElementById('allocationQueryViews').replaceChildren();
        renderCaptureView('allocationCaptureViews', message.result);
        setWorkspaceEvidence('allocation');
      } else if (message.type === 'query' && message.kind === 'allocation') {
        if (message.evidenceId !== allocationEvidenceId) return;
        if (message.result?.weightUnit !== 'allocation-tick-estimated-bytes') {
          renderCpuQuery(message.view, 'CLI did not confirm allocation-byte weighting.', true, 'allocation');
        } else {
          cacheProfileQuery('allocation', message.view, message.rootMethodFilter, message.result);
          if (selectedWorkspaceEvidence === 'allocation'
            && message.view === activeProfileViews.allocation
            && message.rootMethodFilter === activeProfileFilters.allocation) {
            if (profileViewLabels[message.view]) workspaceView.value = message.view;
            workspaceLoadView.hidden = true;
            renderCpuQuery(message.view, message.result, false, 'allocation');
          }
        }
      } else if (message.type === 'queryError' && message.kind === 'allocation') {
        if (message.evidenceId !== allocationEvidenceId) return;
        if (!profileAvailability.allocation) {
          setWorkspaceStatus('allocation', 'Cached allocation views may remain visible; the retained profile is unavailable for additional queries.');
        }
        if (selectedWorkspaceEvidence === 'allocation'
          && message.view === activeProfileViews.allocation
          && message.rootMethodFilter === activeProfileFilters.allocation) {
          workspaceLoadView.hidden = false;
          workspaceLoadView.disabled = !profileAvailability.allocation;
          workspaceLoadView.textContent = profileAvailability.allocation ? 'Retry view load' : 'Retained profile unavailable';
          renderCpuQuery(message.view, message.message, true, 'allocation');
        }
      } else if (message.type === 'captureStatus' && message.kind === 'exceptions') {
        activateCaptureTab('exceptions');
        captureExceptionsStatus.textContent = message.message || message.state || '';
        captureExceptionsButton.disabled = message.state === 'running' || message.state === 'starting';
        if (message.state === 'running') document.getElementById('exceptionCaptureViews').replaceChildren();
      } else if (message.type === 'captureStatus' && message.kind === 'diagnostics') {
        if (typeof message.evidenceId === 'string') diagnosticsRequestId = message.evidenceId;
        const gcCapture = message.captureKind === 'gc' || message.captureKind === 'gc-activities';
        const threadCapture = isThreadCapture(message.captureKind);
        activateCaptureTab(gcCapture || threadCapture ? message.captureKind : 'diagnostics');
        captureDiagnosticsStatus.textContent = message.message || message.state || '';
        captureDiagnosticsButton.disabled = message.state === 'running' || message.state === 'starting';
        if (gcCapture) {
          document.getElementById('workspaceGcStatus').textContent = message.message || message.state || '';
          document.getElementById('captureGc').disabled = message.state === 'running' || message.state === 'starting';
        }
        if (threadCapture) workspaceCaptureStatus.textContent = message.message || message.state || '';
      } else if (message.type === 'capture' && message.kind === 'diagnostics') {
        if (message.evidenceId !== diagnosticsRequestId) return;
        const captureKind = String(message.captureKind || '');
        setGenericCaptureOwner(captureKind, message.evidenceId);
        if (usesWorkspaceForGenericCapture(captureKind)) {
          activateCaptureTab(captureKind);
          if (captureKind === 'gc' || captureKind === 'gc-activities') {
            workspaceGcStatus.textContent = 'Current generic capture: ' + (genericCaptureLabels[captureKind] || captureKind) + '.';
          } else {
            workspaceCaptureStatus.textContent = 'Current generic capture: ' + (genericCaptureLabels[captureKind] || captureKind) + '.';
          }
        } else {
          activateCaptureTab('diagnostics');
        }
        genericCaptureViews.replaceChildren();
        renderCaptureView('diagnosticsCaptureViews', message.result);
      } else if (message.type === 'captureStatus' && message.kind === 'heap') {
        if (typeof message.evidenceId === 'string') pendingHeapEvidenceId = message.evidenceId;
        activateCaptureTab('heap');
        captureHeapStatusElement.textContent = message.message || message.state || '';
        if (message.state === 'running') {
          setWorkspaceStatus('heap', 'Heap recapture in progress. A prior visual may remain; query availability and consent follow the current retained owner.');
        } else if (message.state === 'error') {
          setWorkspaceStatus('heap', 'Heap recapture failed. A prior visual may remain; queryability is not inferred from that display.');
        }
        captureHeapButton.disabled = message.state === 'running';
        if (message.state === 'running') {
          // Clear any query-view sections left over from a previous capture so a stale
          // opt-in/gcdump-origin section can't be mistaken for data from the capture in flight.
          document.getElementById('heapQueryViews').innerHTML = '';
          resetAddressNavigation();
        }
      } else if (message.type === 'captureStatus' && message.kind === 'thread-snapshot') {
        if (typeof message.evidenceId !== 'string') return;
        if (message.state === 'starting') pendingThreadSnapshotEvidenceId = message.evidenceId;
        if (message.evidenceId !== pendingThreadSnapshotEvidenceId) return;
        activateCaptureTab('thread-snapshot');
        captureThreadStatusElement.textContent = message.message || message.state || '';
        captureThreadSnapshotButton.disabled = message.state === 'running' || message.state === 'starting';
        if (message.retainedEvidenceInvalidated === true) {
          threadSnapshotEvidenceId = undefined;
          threadSnapshotQueryAvailable = false;
          setThreadStaticsBusy(false);
          threadSnapshotView = 'snapshot';
          workspaceView.value = 'snapshot';
          setWorkspaceStatus('thread-snapshot',
            'The previous thread snapshot handle was invalidated when recapture began; any remaining summary is display-only.');
          selectThreadSnapshotView('snapshot');
        } else if (message.state === 'error') {
          setWorkspaceStatus('thread-snapshot', threadSnapshotEvidenceId
            ? 'Thread snapshot capture failed before invalidating the current retained snapshot.'
            : 'Thread snapshot capture failed; no current retained snapshot is available.');
        }
        if (message.state === 'running') {
          document.getElementById('threadQueryViews').innerHTML = '';
          document.getElementById('threadExplorer')?.remove();
          workspaceThreadViewStatus.textContent = 'Snapshot recapture invalidated prior query views; waiting for the new snapshot.';
        }
      } else if (message.type === 'captureStatus') {
        captureStatusElement.textContent = message.message || message.state || '';
        captureCpuButton.disabled = message.state === 'running';
      } else if (message.type === 'capture' && message.kind === 'exceptions') {
        activateCaptureTab('exceptions');
        renderQueryView('exceptionCaptureViews', 'exceptions', message.result, false);
        document.getElementById('exceptionCaptureViews-exceptions').open = true;
      } else if (message.type === 'capture' && message.kind === 'heap') {
        if (message.evidenceId !== pendingHeapEvidenceId) return;
        heapEvidenceSource = message.source;
        activateCaptureTab('heap');
        heapEvidenceId = typeof message.evidenceId === 'string' ? message.evidenceId : undefined;
        renderHeapCapture(message.source, message.result);
        setWorkspaceStatus('heap', 'Current displayed evidence: heap snapshot (' + String(message.source || 'source unavailable')
          + '). Retained live-query availability is governed by the snapshot owner and consent state.');
      } else if (message.type === 'capture' && message.kind === 'thread-snapshot') {
        if (message.evidenceId !== pendingThreadSnapshotEvidenceId) return;
        threadSnapshotEvidenceId = message.evidenceId;
        threadSnapshotQueryAvailable = message.retainedHandleAvailable === true;
        setThreadStaticsBusy(false);
        threadSnapshotView = 'snapshot';
        activateCaptureTab('thread-snapshot');
        renderThreadCapture(message.result);
        setWorkspaceStatus('thread-snapshot', threadSnapshotQueryAvailable
          ? 'Current evidence: point-in-time thread snapshot. Snapshot thread-pool query evidence is separate from thread-pool event captures.'
          : 'Current evidence: point-in-time thread snapshot; no retained query handle is available. Snapshot thread-pool query evidence is separate from thread-pool event captures.');
        selectThreadSnapshotView('snapshot');
      } else if (message.type === 'capture') {
        if (message.evidenceId !== pendingCpuEvidenceId) return;
        activateCaptureTab('cpu');
        profileCaptureBusy.cpu = false;
        cpuEvidenceId = typeof message.evidenceId === 'string' ? message.evidenceId : undefined;
        cpuViewCache.clear();
        activeProfileViews.cpu = 'triage';
        activeProfileFilters.cpu = undefined;
        document.getElementById('resultCpu').dataset.evidenceId = cpuEvidenceId || '';
        setWorkspaceStatus('cpu', 'Current evidence: CPU profile · ' + String(message.summary?.totalSamples ?? 'sample count unavailable')
          + ' captured stack samples. Percentages are sample shares, not measured CPU time or utilization; Total includes descendants and Self excludes them.');
        cpuProfileResult = undefined;
        cpuProfileState.focus = [];
        cpuProfileState.selected = undefined;
        cpuProfileState.search = '';
        cpuProfileState.expanded = undefined;
        cpuQueryViews.replaceChildren();
        cpuQueryStatus.textContent = '';
        renderCapture(message.summary);
        document.getElementById('cpuSamplingDisclaimer').textContent =
          'CPU percentages are shares of captured stack samples, not measured CPU time or utilization. '
          + 'Total includes descendants; Self excludes descendants. Full capture denominator: '
          + String(message.summary?.totalSamples ?? 'unavailable') + ' captured stack samples. Focus and search do not renormalize values.';
        setWorkspaceEvidence('cpu');
      } else if (message.type === 'query' && message.kind === 'cpu') {
        if (message.evidenceId !== cpuEvidenceId) return;
        cpuQueryStatus.textContent = '';
        cacheProfileQuery('cpu', message.view, message.rootMethodFilter, message.result);
        if (selectedWorkspaceEvidence === 'cpu'
          && message.view === activeProfileViews.cpu
          && message.rootMethodFilter === activeProfileFilters.cpu) {
          if (profileViewLabels[message.view]) workspaceView.value = message.view;
          workspaceLoadView.hidden = true;
          renderCpuQuery(message.view, message.result, false);
        }
      } else if (message.type === 'queryError' && message.kind === 'cpu') {
        if (message.evidenceId !== cpuEvidenceId) return;
        cpuQueryStatus.textContent = '';
        if (!profileAvailability.cpu) {
          setWorkspaceStatus('cpu', 'Cached CPU views may remain visible; the retained profile is unavailable for additional queries.');
        }
        if (selectedWorkspaceEvidence === 'cpu'
          && message.view === activeProfileViews.cpu
          && message.rootMethodFilter === activeProfileFilters.cpu) {
          workspaceLoadView.hidden = false;
          workspaceLoadView.disabled = !profileAvailability.cpu;
          workspaceLoadView.textContent = profileAvailability.cpu ? 'Retry view load' : 'Retained profile unavailable';
          renderCpuQuery(message.view, message.message, true);
        }
      } else if (message.type === 'query' && message.kind === 'heap') {
        if (message.evidenceId !== heapEvidenceId) return;
        renderQueryView('heapQueryViews', message.view, message.result, false);
      } else if (message.type === 'query' && message.kind === 'thread-snapshot') {
        if (message.evidenceId !== threadSnapshotEvidenceId) return;
        renderQueryView('threadQueryViews', message.view, message.result, false);
        if (selectedWorkspaceEvidence === 'thread-snapshot' && threadSnapshotView === message.view)
          selectThreadSnapshotView(message.view);
      } else if (message.type === 'queryError' && message.kind === 'heap') {
        if (message.evidenceId !== heapEvidenceId) return;
        renderQueryView('heapQueryViews', message.view, message.message, true);
      } else if (message.type === 'queryError' && message.kind === 'thread-snapshot') {
        if (message.evidenceId !== threadSnapshotEvidenceId) return;
        renderQueryView('threadQueryViews', message.view, message.message, true);
        if (selectedWorkspaceEvidence === 'thread-snapshot' && threadSnapshotView === message.view)
          selectThreadSnapshotView(message.view);
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
          ? 'Live counters stopped: target process exited.'
          : message.status === 'failed' ? 'Live counters failed.' : 'Live counters stopped.';
        if (typeof message.error === 'string' && message.error.length > 0) errorElement.textContent = message.error;
      }
      updateEvidenceActions(message);
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
    eventsFollowButton.disabled = true;
    captureCpuButton.addEventListener('click', () => vscode.postMessage({ type: 'captureCpu' }));
    document.getElementById('captureGc').addEventListener('click', () => vscode.postMessage({
      type: 'captureGc', kind: selectedWorkspaceEvidence,
    }));
    captureExceptionsButton.addEventListener('click', () => vscode.postMessage({ type: 'captureExceptions' }));
    captureDiagnosticsButton.addEventListener('click', () => vscode.postMessage({ type: 'captureDiagnostics' }));
    document.getElementById('captureAllocation').addEventListener('click', () => vscode.postMessage({ type: 'captureAllocation' }));
    captureHeapButton.addEventListener('click', () => vscode.postMessage({ type: 'captureHeap' }));
    captureThreadSnapshotButton.addEventListener('click', () => vscode.postMessage({ type: 'captureThreadSnapshot' }));
    window.addEventListener('resize', resizeCanvas);
    // The webview can be laid out while hidden (zero size); re-measure whenever the canvas gets a real size.
    if (typeof ResizeObserver === 'function') new ResizeObserver(() => resizeCanvas()).observe(canvas);
    resizeCanvas();
    updateButtons(false);
    updateEvidenceActions({});
    // Keeps the rolling history window sliding forward in wall-clock time even if the stream goes
    // quiet for a while, instead of only evicting stale points/rows when fresh data arrives.
    setInterval(() => {
      renderEventsQuality();
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
    .statics-form { margin:.5rem 0; display:flex; gap:.5rem; align-items:center; flex-wrap:wrap; }
    .statics-error { color: var(--vscode-errorForeground); }
    ${QUERY_VIEW_STYLES}
    ${WEBVIEW_FIELD_STYLES}
    .status { color:var(--vscode-descriptionForeground); margin:.3rem 0; }
  </style>
</head>
<body>
  <header>
    <h2>Dump analysis</h2>
    <div>${escapeHtml(dumpFileName)}</div>
    ${RESULT_EXPORT_BUTTONS}
  </header>
  <div id="error" role="alert"></div>

  <section class="panel">
    <h3>Heap snapshot</h3>
    <div id="captureHeapStatus" class="status"></div>
    <div id="captureHeapResult" class="empty">
      <div id="captureHeapHeadline"></div>
      <div id="heapWorkspace" class="heap-workspace" data-focused="false">
      <div id="heapTypeList" class="heap-type-list">
      <button id="openHeapDetails" type="button">Explore heap details</button>
      <table id="captureHeapTable">
        <thead><tr><th>Type</th><th>Instances</th><th>Bytes</th></tr></thead>
        <tbody id="captureHeapBody"></tbody>
      </table>
      </div>
      <section id="heapDetailPane" class="heap-detail-pane" aria-label="Heap details" tabindex="-1">
      <button id="backToHeapTypes" type="button">Back to types</button>
      <div id="heapQueryViews" class="query-views"></div>
      ${ADDRESS_NAVIGATION_HTML}
      </section>
      </div>
    </div>
  </section>

  <section class="panel">
    <h3>Thread snapshot</h3>
    <div id="captureThreadStatus" class="status"></div>
    <div id="captureThreadResult" class="empty">
      <div id="captureThreadHeadline"></div>
      <details><summary>Inline thread summary</summary>
      <table id="captureThreadTable">
        <thead><tr><th>Thread</th><th>State</th><th>Wait reason</th><th>Top frame</th></tr></thead>
        <tbody id="captureThreadBody"></tbody>
      </table>
      </details>
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
    ${RESULT_EXPORT_SCRIPT}
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
    ${QUERY_VIEW_RENDERER_SCRIPT}
    // Both views come from the same dump, so thread-view addresses can navigate the heap too.
    addressLinkContainers.add('threadQueryViews');

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
      resetEvidenceTable(document.getElementById('captureHeapTable'));
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
      renderHeapTypeNavigation(topTypes);
      enhanceEvidenceTable(document.getElementById('captureHeapTable'));
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
      updateThreadNavigation('snapshot', data || {});
    }

    // The thread-statics view needs an exact type name, so it is requested on demand rather than
    // auto-queried; the extension host validates the message again.
    function setThreadStaticsBusy(busy) {
      threadStaticsButton.disabled = busy;
      threadStaticsButton.textContent = busy ? 'Querying…' : 'Query thread statics';
    }

    threadStaticsButton.addEventListener('click', () => {
      const typeFilter = threadStaticsInput.value.trim();
      if (!typeFilter) {
        threadStaticsError.textContent = 'Enter an exact type name.';
        return;
      }
      if (threadStaticsButton.disabled) return;
      threadStaticsError.textContent = '';
      setThreadStaticsBusy(true);
      vscode.postMessage({ type: 'queryThreadStatics', typeFilter });
    });

    window.addEventListener('message', event => {
      const message = event.data;
      if (!message || typeof message.type !== 'string') return;
      if (handleAddressNavigationMessage(message)) return;
      if (message.type === 'threadStaticsState') {
        setThreadStaticsBusy(message.state === 'running');
      } else if (message.type === 'captureStatus' && message.kind === 'heap') {
        captureHeapStatusElement.textContent = message.message || message.state || '';
        if (message.state === 'running') {
          document.getElementById('heapQueryViews').innerHTML = '';
          resetAddressNavigation();
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

function isCancellationMessage(message: string): boolean {
    return /\bcancell?(?:ed|ation)\b/i.test(message);
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

async function promptCaptureParams(spec: CaptureKindSpec): Promise<CaptureParams | undefined> {
    const duration = await vscode.window.showInputBox({
        prompt: `${spec.label}: capture duration in seconds (1-300)`, value: "10",
        validateInput: value => validateInteger(value, 1, 300, "Duration"),
    });
    if (duration === undefined) return undefined;
    const top = await vscode.window.showInputBox({
        prompt: `${spec.label}: rows per table (1-100)`, value: String(spec.defaultTopN),
        validateInput: value => validateInteger(value, 1, 100, "Rows per table"),
    });
    if (top === undefined) return undefined;
    const params: CaptureParams = { durationSeconds: Number(duration), topN: Number(top) };
    if (spec.supportsSymbolPath) {
        const symbols = await vscode.window.showInputBox({ prompt: "Optional symbol path", validateInput: validateSymbolPath });
        if (symbols === undefined) return undefined;
        if (symbols.trim()) params.symbolPath = symbols.trim();
    }
    if (spec.supportsSources) {
        const sources = await vscode.window.showInputBox({
            prompt: "Optional comma-separated ActivitySource filters; empty captures all sources",
            validateInput: value => parseSources(value).error,
        });
        if (sources === undefined) return undefined;
        params.sources = parseSources(sources).sources;
    }
    if (spec.supportsThreshold) {
        const threshold = await vscode.window.showInputBox({
            prompt: "Long-running request threshold in milliseconds (0-3600000)", value: "1000",
            validateInput: value => validateInteger(value, 0, 3_600_000, "Threshold"),
        });
        if (threshold === undefined) return undefined;
        params.thresholdMs = Number(threshold);
    }
    return params;
}

function describeThreadCaptureRisk(): string {
    return "Capture thread snapshot? A live ClrMD thread walk attaches with ptrace, briefly suspends the target, "
        + "and exposes stack, type, and method names. This may expose possibly confidential data. This is a "
        + "high-risk operation that requires explicit acknowledgement before it runs against the selected process.";
}

function withRuntimeGuidance(message: string): string {
    if (/protocol mismatch|protocol version/i.test(message)) {
        return `${message} Update dotnet-diagnostics-cli to a version compatible with this extension. Run '.NET Diagnostics: Check Environment' or '.NET Diagnostics: Open Getting Started' for the guided steps.`;
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
