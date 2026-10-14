import * as vscode from "vscode";
import { randomBytes } from "node:crypto";
import {
    captureDeleteArgs, captureListArgs, captureShowArgs, parseCaptureList, parseCaptureShow,
    parseEnvelope, persistCaptureArgs, runHistoryCli, parseProcessInventory, validateTarget,
    type CaptureSummary, type ProcessEntry,
} from "./processHistory";
import { queryHistoryView, renderHistoryHtml } from "./captureHistoryPanel";
import { ResultExporter } from "./resultExport";
import { compareSaved } from "./baselineCompare";

export type HistoryRow = { capture: CaptureSummary } | { more: string } | { message: string };

export class CaptureHistory implements vscode.TreeDataProvider<HistoryRow>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    public readonly onDidChangeTreeData = this.changed.event;
    private rows: HistoryRow[] = [];
    private loaded = false;
    private refreshTask?: Promise<void>;
    private readonly panels = new Set<vscode.WebviewPanel>();

    public constructor(private readonly cliPath: () => string, private readonly output: vscode.OutputChannel) {}
    public dispose(): void {
        for (const panel of this.panels) panel.dispose();
        this.changed.dispose();
    }
    public getTreeItem(row: HistoryRow): vscode.TreeItem {
        if ("message" in row) return new vscode.TreeItem(row.message, vscode.TreeItemCollapsibleState.None);
        if ("more" in row) {
            const item = new vscode.TreeItem("Load next 50 captures", vscode.TreeItemCollapsibleState.None);
            item.command = { command: "dotnetDiagnostics.refreshHistory", title: "Load next captures", arguments: [row] };
            return item;
        }
        const c = row.capture;
        const item = new vscode.TreeItem(c.name || c.captureId, vscode.TreeItemCollapsibleState.None);
        item.description = `${c.createdUtc} - ${c.state}`;
        item.tooltip = `Saved capture ${c.captureId}; ${c.artifacts.length} artifact(s). Offline evidence only.`;
        item.contextValue = "dotnetDiagnostics.savedCapture";
        item.iconPath = new vscode.ThemeIcon("history");
        item.command = { command: "dotnetDiagnostics.openCapture", title: "Open saved capture", arguments: [row] };
        return item;
    }
    public async getChildren(): Promise<HistoryRow[]> {
        if (!this.loaded) await this.refresh();
        return this.rows;
    }
    public refresh(after?: string): Promise<void> {
        if (!this.refreshTask)
            this.refreshTask = this.refreshCore(after).finally(() => { this.refreshTask = undefined; });
        return this.refreshTask;
    }
    private async refreshCore(after?: string): Promise<void> {
        try {
            const page = parseCaptureList(await runHistoryCli(this.cliPath(), captureListArgs(after)));
            const previous = after ? this.rows.filter((row): row is { capture: CaptureSummary } => "capture" in row) : [];
            this.rows = [...previous, ...page.captures.map(capture => ({ capture }))].slice(0, 500);
            if (page.nextAfterCaptureId && this.rows.length < 500) this.rows.push({ more: page.nextAfterCaptureId });
            else if (page.nextAfterCaptureId)
                this.rows.push({ message: "Showing the first 500 saved captures. Use the CLI for older captures." });
            if (!this.rows.length) this.rows.push({ message: "No saved captures. Use Capture and Save Snapshot on a process." });
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            this.rows = [{ message: `Cannot list saved captures: ${message}` }];
            this.output.appendLine(`Capture history failed: ${message}`);
            throw error;
        } finally {
            this.loaded = true;
            this.changed.fire();
        }
    }
    public async open(row: HistoryRow): Promise<void> {
        if (!("capture" in row)) throw new Error("Select a saved capture.");
        const detail = parseCaptureShow(await runHistoryCli(this.cliPath(), captureShowArgs(row.capture.captureId)));
        if (detail.state !== "sealed") throw new Error("Only sealed captures can be reopened. Recover interrupted captures with the CLI first.");
        const panel = vscode.window.createWebviewPanel("dotnetDiagnostics.savedCapture",
            `Saved capture - ${detail.name}`, vscode.ViewColumn.Active,
            { enableScripts: true, retainContextWhenHidden: true, localResourceRoots: [] });
        this.panels.add(panel);
        let disposed = false;
        let querying = false;
        const exporter = new ResultExporter();
        let selectedQuery: { artifactId: string; requestId: number } | undefined;
        let selectionRevision = 0;
        const listener = panel.webview.onDidReceiveMessage(async message => {
            if (message?.type === "selectionChanged") {
                selectionRevision++;
                selectedQuery = undefined;
                exporter.clear();
                return;
            }
            if (await exporter.handle(message)) return;
            if (message?.type === "compareBaseline") {
                if (querying || !selectedQuery || message.artifactId !== selectedQuery.artifactId
                    || message.requestId !== selectedQuery.requestId) {
                    await vscode.window.showErrorMessage("Select a successfully loaded artifact view and wait for the current operation before comparing.");
                    return;
                }
                const candidate = selectedQuery;
                querying = true;
                try {
                    const result = await compareSaved(this.cliPath(), detail, candidate.artifactId);
                    if (result && !disposed && selectedQuery === candidate) {
                        exporter.set({ metrics: result.metrics });
                        await panel.webview.postMessage({ type: "comparison", ...candidate, result });
                    }
                } catch (error) {
                    await vscode.window.showErrorMessage(`Baseline comparison failed: ${error instanceof Error ? error.message : String(error)}`);
                } finally { querying = false; }
                return;
            }
            if (!message || message.type !== "query" || typeof message.artifactId !== "string"
                || typeof message.view !== "string" || !Number.isSafeInteger(message.requestId)) return;
            if (querying) {
                await panel.webview.postMessage({ type: "result", requestId: message.requestId, ok: false,
                    view: message.view, message: "Wait for the current offline query to finish." });
                return;
            }
            querying = true;
            const revision = selectionRevision;
            selectedQuery = undefined;
            exporter.clear();
            try {
                const result = await queryHistoryView(this.cliPath(), detail, message.artifactId, message.view);
                if (revision !== selectionRevision) return;
                if (result.ok) { selectedQuery = { artifactId: message.artifactId, requestId: message.requestId }; exporter.set(result.data); }
                if (!disposed) await panel.webview.postMessage({ type: "result", requestId: message.requestId, ...result });
            } finally { querying = false; }
        });
        panel.onDidDispose(() => { disposed = true; listener.dispose(); this.panels.delete(panel); });
        panel.webview.html = renderHistoryHtml(randomBytes(18).toString("base64url"), panel.webview.cspSource, detail);
    }
    public async delete(row: HistoryRow): Promise<void> {
        if (!("capture" in row)) throw new Error("Select a saved capture to delete.");
        const confirmation = await vscode.window.showWarningMessage(
            `Permanently delete saved capture "${row.capture.name}" (${row.capture.captureId}) and its artifacts?`,
            { modal: true }, "Delete Capture", "Cancel");
        if (confirmation !== "Delete Capture") return;
        const response = parseEnvelope(await runHistoryCli(this.cliPath(), captureDeleteArgs(row.capture.captureId, true)));
        if (typeof response.data !== "object" || response.data === null || !("deleted" in response.data) || response.data.deleted !== true)
            throw new Error("The CLI did not confirm capture deletion.");
        await this.refresh();
    }
    public async save(process: ProcessEntry): Promise<void> {
        const selected = await vscode.window.showQuickPick([
            { label: "Live heap snapshot", captureKind: "heap-live" as const },
            { label: "GC dump snapshot", captureKind: "heap-gcdump" as const },
            { label: "Thread snapshot", captureKind: "thread-snapshot" as const },
        ], { placeHolder: "Collect a NEW saved snapshot (does not convert the current panel result)" });
        if (!selected) return;
        const warning = selected.captureKind === "heap-gcdump"
            ? "This induces a blocking Gen2 GC and may pause the process."
            : "This attaches with ptrace and briefly suspends the process.";
        const confirmation = await vscode.window.showWarningMessage(
            `Capture and save a NEW snapshot of PID ${process.processId}? ${warning} `
            + "The saved evidence can contain sensitive application data and remains on disk until CLI-managed deletion.",
            { modal: true }, "Acknowledge and Save", "Cancel");
        if (confirmation !== "Acknowledge and Save") return;
        const inventory = parseProcessInventory(await runHistoryCli(this.cliPath(), ["processes", "--json"]));
        validateTarget(process, inventory.processes);
        const stdout = await runHistoryCli(this.cliPath(), persistCaptureArgs(selected.captureKind, process.processId, true));
        const saved = parseCaptureShow(stdout);
        await this.refresh();
        await this.open({ capture: saved });
    }
}
