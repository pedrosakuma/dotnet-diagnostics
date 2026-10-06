import { randomBytes } from "node:crypto";
import { execFile as execFileCallback, spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface, type Interface as ReadlineInterface } from "node:readline";
import { promisify } from "node:util";
import * as vscode from "vscode";
import {
    describeStreamCompatibilityError,
    parseProcessList,
    parseProtocolFrame,
    type CounterValue,
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
            && !this.activeChild.terminalReceived
            && this.activeChild.process.exitCode === null
            && this.activeChild.process.signalCode === null)
        {
            this.panel?.reveal(vscode.ViewColumn.Active);
            return;
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
            return;
        }
        if (this.disposed) {
            return;
        }

        if (targets.length === 0) {
            void vscode.window.showInformationMessage("No .NET processes with a diagnostic IPC endpoint were found.");
            return;
        }

        const selected = await vscode.window.showQuickPick(
            targets.map(target => ({
                label: `${target.processId}  ${target.managedEntrypointAssemblyName ?? "Unknown .NET application"}`,
                description: `${target.runtimeVersion} · ${target.operatingSystem}/${target.processArchitecture}`,
                target,
            })),
            { placeHolder: "Select a .NET process for live runtime counters" },
        );
        if (!selected) {
            return;
        }
        if (this.disposed) {
            return;
        }

        this.selectedTarget = selected.target;
        this.openPanel(selected.target);
        await this.waitForPanelReady();
        await this.startStream(selected.target);
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
            await withTimeout(child.terminal.promise, 10_000);
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
        panel.webview.html = renderHtml(target, randomBytes(18).toString("base64url"));
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
            }
        }, undefined, []);
        panel.onDidDispose(() => {
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
        if (this.disposed || (this.activeChild && !this.activeChild.terminalReceived)) {
            return;
        }

        const existing = this.activeChild;
        if (existing && existing.process.exitCode === null && existing.process.signalCode === null) {
            await this.closeChild(existing);
        }

        let child: ChildProcessWithoutNullStreams;
        try {
            child = this.spawnStream();
        } catch (error) {
            const message = withRuntimeGuidance(errorMessage(error));
            this.postMessage({ type: "status", state: "error", message });
            this.output.appendLine(`Could not launch the CLI: ${errorMessage(error)}`);
            return;
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
        };
        this.activeChild = session;
        this.statusBar.text = "$(sync~spin) .NET Counters";
        this.postMessage({ type: "status", state: "starting", message: "Negotiating CLI protocol…" });

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
            if (!session.terminalReceived) {
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
            if (this.activeChild === session) {
                this.activeChild = undefined;
                this.statusBar.text = "$(pulse) .NET Counters";
            }
        });

        try {
            this.writeFrame(session, { type: "hello", protocolVersion: PROTOCOL_VERSION });
            await withTimeout(session.handshake.promise, 10_000);
            this.writeFrame(session, {
                type: "start",
                requestId: randomBytes(12).toString("hex"),
                processId: target.processId,
                providers: ["System.Runtime"],
                intervalSeconds: 1,
                observationCapacity: 256,
            });
            await withTimeout(session.started.promise, 30_000);
            this.postMessage({ type: "status", state: "running", message: "Live counters are streaming." });
        } catch (error) {
            if (!session.startupErrorReported) {
                this.postMessage({ type: "status", state: "error", message: withRuntimeGuidance(errorMessage(error)) });
            }
            this.output.appendLine(`Live counter startup failed: ${errorMessage(error)}`);
            await this.closeChild(session);
        }
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
                this.postMessage({ type: "session", sessionId: frame.sessionId });
                break;

            case "observation":
                if (frame.sessionId === session.sessionId
                    && isCounterValue(frame.counter)
                    && Number.isSafeInteger(frame.sequence)
                    && typeof frame.timestamp === "string") {
                    this.postMessage({
                        type: "observation",
                        sequence: frame.sequence,
                        timestamp: frame.timestamp,
                        counter: frame.counter,
                    });
                }
                break;

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
                    eventPipeEventsLost: frame.eventPipeEventsLost,
                    droppedObservations: frame.droppedObservations,
                    error: frame.error,
                });
                this.statusBar.text = "$(pulse) .NET Counters";
                break;

            case "error": {
                const message = typeof frame.message === "string" ? frame.message : "The CLI returned a protocol error.";
                const error = new Error(
                    frame.code === "protocol_version_unsupported"
                        ? `CLI protocol mismatch: ${message}`
                        : message,
                );
                if (!session.handshake.settled) {
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

        return [start, stop];
    }
}

export async function deactivate(): Promise<void> {
    await activeController?.shutdown();
    activeController = undefined;
}

function renderHtml(target: TargetProcess, nonce: string): string {
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
  </style>
</head>
<body>
  <header>
    <div><h2>Live runtime counters</h2><div>${escapeHtml(label)}</div></div>
    <div class="controls">
      <label for="metric">Counter</label><select id="metric" aria-label="Select counter"></select>
      <button id="start">Start</button><button id="stop" disabled>Stop</button>
    </div>
  </header>
  <div id="status" role="status">Connecting to CLI…</div>
  <div id="value">Waiting for first observation</div>
  <canvas id="chart" aria-label="Selected counter time series"></canvas>
  <div id="quality"></div>
  <div id="error" role="alert"></div>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const metricSelect = document.getElementById('metric');
    const startButton = document.getElementById('start');
    const stopButton = document.getElementById('stop');
    const statusElement = document.getElementById('status');
    const valueElement = document.getElementById('value');
    const qualityElement = document.getElementById('quality');
    const errorElement = document.getElementById('error');
    const canvas = document.getElementById('chart');
    const context = canvas.getContext('2d');
    const counters = new Map();
    const maxSeries = ${MAX_WEBVIEW_SERIES};
    const maxPoints = 120;
    let selectedKey = '';
    let lastSequence = 0;
    let sequenceGaps = 0;
    let seriesLimitReached = false;

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

    function addCounter(message) {
      const counter = message.counter;
      if (!counter || typeof counter.provider !== 'string' || typeof counter.name !== 'string' ||
          typeof counter.value !== 'number' || !Number.isFinite(counter.value)) return;
      if (message.sequence > lastSequence + 1 && lastSequence > 0) {
        sequenceGaps += message.sequence - lastSequence - 1;
      }
      lastSequence = Math.max(lastSequence, message.sequence);
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
      series.points.push({ value: counter.value, timestamp: message.timestamp });
      if (series.points.length > maxPoints) series.points.shift();
      if (key === selectedKey) {
        valueElement.textContent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(counter.value) +
          (series.unit ? ' ' + series.unit : '');
      }
      if (sequenceGaps > 0) {
        qualityElement.textContent = sequenceGaps + ' observations missing (sequence gaps); gaps are not zero values.';
      }
      draw();
    }

    window.addEventListener('message', event => {
      const message = event.data;
      if (!message || typeof message.type !== 'string') return;
      if (message.type === 'status') {
        statusElement.textContent = message.message || message.state;
        errorElement.textContent = message.state === 'error' ? (message.message || '') : '';
        updateButtons(message.state === 'running' || message.state === 'starting' || message.state === 'stopping');
      } else if (message.type === 'observation') {
        addCounter(message);
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
    window.addEventListener('resize', resizeCanvas);
    resizeCanvas();
    updateButtons(false);
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
