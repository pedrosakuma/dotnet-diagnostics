import { randomBytes } from "node:crypto";
import { execFile as execFileCallback, spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface, type Interface as ReadlineInterface } from "node:readline";
import { promisify } from "node:util";
import * as vscode from "vscode";
import {
    describeStreamCompatibilityError,
    isGcCollection,
    isCpuSampleSummary,
    parseProcessList,
    parseProtocolFrame,
    type CounterValue,
    type CpuSampleSummary,
    type ProtocolFrame,
    type TargetProcess,
} from "./protocol";

const execFile = promisify(execFileCallback);
const PROTOCOL_VERSION = 1;
const MAX_WEBVIEW_SERIES = 128;

interface Deferred<T> {
    promise: Promise<T>;
    resolve: (value: T) => void;
    reject: (error: Error) => void;
    settled: boolean;
}

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
    pendingCaptures: Map<string, Deferred<CpuSampleSummary>>;
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

        this.postMessage({ type: "captureStatus", state: "running", message: "Capturing CPU…" });

        let session: StreamChild;
        let ownsSession: boolean;
        try {
            ({ session, owns: ownsSession } = await this.acquireChild());
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            return;
        }

        const requestId = randomBytes(12).toString("hex");
        const pending = deferred<CpuSampleSummary>();
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
            const summary = await withTimeout(pending.promise, 60_000);
            this.postMessage({ type: "capture", summary });
            this.postMessage({ type: "captureStatus", state: "done", message: "CPU capture complete." });
        } catch (error) {
            this.postMessage({
                type: "captureStatus",
                state: "error",
                message: withRuntimeGuidance(errorMessage(error)),
            });
            this.output.appendLine(`CPU capture failed: ${errorMessage(error)}`);
        } finally {
            session.pendingCaptures.delete(requestId);
            if (ownsSession && !session.sessionId && !session.startInFlight) {
                // This connection exists only to serve the capture request — no live session was
                // started on it (and no `start` request is queued/in-flight behind this capture
                // response, which the single-threaded CLI read loop processes strictly in order),
                // so close it now instead of leaving an idle CLI child running. Mark the shutdown
                // as expected so the `close` handler doesn't report it as an unexpected CLI exit
                // over whatever status the capture itself just posted.
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
            // The CLI reads requests strictly in order on a shared connection. If a CPU capture is
            // in flight (e.g. triggered while this live session was streaming), this `cancel` frame
            // sits queued behind it and won't be read until the capture's ~10s sampling window and
            // response processing finish. Give that case extra headroom instead of racing a fixed
            // 10s timeout and force-killing the process while a capture is still legitimately busy.
            const timeoutMs = child.pendingCaptures.size > 0 ? 25_000 : 10_000;
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
        const configured = vscode.workspace
            .getConfiguration("dotnetDiagnostics")
            .get<string>("cliPath", "dotnet-diagnostics-cli")
            .trim();
        return configured || "dotnet-diagnostics-cli";
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
                pending.reject(new Error("The CLI connection closed before the capture completed."));
            }
            session.pendingCaptures.clear();
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
        const cliPath = this.getCliPath();
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
                if (isCpuSampleSummary(frame.result)) {
                    pending.resolve(frame.result);
                } else {
                    pending.reject(new Error("The CLI returned a capture result in an unexpected shape."));
                }
                break;
            }

            case "error": {
                const message = typeof frame.message === "string" ? frame.message : "The CLI returned a protocol error.";
                const error = new Error(
                    frame.code === "protocol_version_unsupported"
                        ? `CLI protocol mismatch: ${message}`
                        : message,
                );
                const requestId = typeof frame.requestId === "string" ? frame.requestId : undefined;
                const pendingCapture = requestId ? session.pendingCaptures.get(requestId) : undefined;
                if (pendingCapture) {
                    pendingCapture.reject(error);
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

        return [start, stop, captureCpu];
    }
}

export async function deactivate(): Promise<void> {
    await activeController?.shutdown();
    activeController = undefined;
}

function renderHtml(target: TargetProcess, nonce: string, historyDurationSeconds: number): string {
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
    #captureResult.empty { display:none; }
  </style>
</head>
<body>
  <header>
    <div><h2>Live runtime counters</h2><div>${escapeHtml(label)}</div></div>
    <div class="controls">
      <label for="metric">Counter</label><select id="metric" aria-label="Select counter"></select>
      <button id="start">Start</button><button id="stop" disabled>Stop</button>
      <button id="captureCpu">Capture CPU now</button>
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

  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const metricSelect = document.getElementById('metric');
    const startButton = document.getElementById('start');
    const stopButton = document.getElementById('stop');
    const captureCpuButton = document.getElementById('captureCpu');
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
    function parseTimeSpanToMs(value) {
      if (typeof value !== 'string') return NaN;
      const match = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value);
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
      } else if (message.type === 'captureStatus') {
        captureStatusElement.textContent = message.message || message.state || '';
        captureCpuButton.disabled = message.state === 'running';
      } else if (message.type === 'capture') {
        renderCapture(message.summary);
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

function withRuntimeGuidance(message: string): string {
    if (/protocol mismatch|protocol version/i.test(message)) {
        return `${message} Update dotnet-diagnostics-cli to a version compatible with this extension.`;
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
