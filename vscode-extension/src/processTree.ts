import * as vscode from "vscode";
import {
    filterProcesses, parseProcessInventory, runHistoryCli, validateTarget,
    type ProcessEntry,
} from "./processHistory";

export type ProcessRow = { process: ProcessEntry } | {
    message: string; clearFilter?: boolean; setupCommand?: "checkEnvironment" | "openGettingStarted";
};
export type ProcessAction = "select" | "counters" | "cpu" | "heap" | "threads" | "save";
const STATE_KEY = "dotnetDiagnostics.lastProcess";

export class ProcessTree implements vscode.TreeDataProvider<ProcessRow>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    public readonly onDidChangeTreeData = this.changed.event;
    private rows: ProcessRow[] = [];
    private processes: ProcessEntry[] = [];
    private filter = "";
    private loaded = false;
    private refreshTask?: Promise<void>;
    private selected?: ProcessEntry;
    private invoking = false;
    private active?: ProcessEntry;
    public message?: string;

    public constructor(
        private readonly state: vscode.Memento,
        private readonly cliPath: () => string,
        private readonly action: (process: ProcessEntry, action: ProcessAction) => Promise<void>,
        private readonly output: vscode.OutputChannel,
    ) {}

    public dispose(): void { this.changed.dispose(); }

    public getTreeItem(row: ProcessRow): vscode.TreeItem {
        if ("message" in row) {
            const item = new vscode.TreeItem(row.message, vscode.TreeItemCollapsibleState.None);
            if (row.clearFilter)
                item.command = { command: "dotnetDiagnostics.clearProcessFilter", title: "Clear Filter" };
            else if (row.setupCommand)
                item.command = { command: `dotnetDiagnostics.${row.setupCommand}`, title: row.message };
            return item;
        }
        const p = row.process;
        const item = new vscode.TreeItem(`${p.entrypoint || ".NET process"} (${p.processId})`, vscode.TreeItemCollapsibleState.None);
        const remembered = this.state.get<ProcessEntry>(STATE_KEY);
        const last = remembered?.processId === p.processId && remembered.identity === p.identity;
        const active = this.active?.processId === p.processId && this.active.identity === p.identity;
        item.description = `${p.runtimeVersion}${active ? " - active target" : last ? " - remembered hint" : ""}`;
        item.tooltip = `${p.platform}; PID ${p.processId}. ${active
            ? "Active panel target; collection starts only when requested."
            : "Not the active target. A remembered PID is only a selection hint."}`;
        item.contextValue = "dotnetDiagnostics.process";
        item.iconPath = new vscode.ThemeIcon("server-process");
        item.command = { command: "dotnetDiagnostics.selectProcess", title: "Select process", arguments: [row] };
        return item;
    }

    public async getChildren(): Promise<ProcessRow[]> {
        if (!this.loaded) {
            try { await this.refresh(); }
            catch (error) {
                // Discovery failures are rendered in the native view message alongside setup actions.
                if (!this.message) throw error;
            }
        }
        return this.rows;
    }

    public refresh(): Promise<void> {
        if (!this.refreshTask) {
            this.refreshTask = this.refreshCore().finally(() => { this.refreshTask = undefined; });
        }
        return this.refreshTask;
    }

    public async setActiveTarget(process?: ProcessEntry): Promise<void> {
        this.active = process;
        this.selected = process;
        this.changed.fire();
        if (process) await this.state.update(STATE_KEY, process);
    }

    private async refreshCore(): Promise<void> {
        try {
            const inventory = parseProcessInventory(await runHistoryCli(this.cliPath(), ["processes", "--json"]));
            this.processes = inventory.processes;
            this.message = undefined;
            this.rows = filterProcesses(this.processes, this.filter).map(process => ({ process }));
            if (this.processes.length && !this.rows.length)
                this.rows.push({ message: `No processes match "${this.filter}". Clear Filter`, clearFilter: true });
            if (inventory.truncated)
                this.rows.push({ message: `Showing first 500 of ${inventory.total} processes. Use the CLI for the full inventory.` });
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            this.processes = [];
            this.message = `Cannot list processes: ${message}`;
            this.rows = [{ message: "Check Environment", setupCommand: "checkEnvironment" },
                { message: "Open Getting Started", setupCommand: "openGettingStarted" }];
            this.output.appendLine(`Process refresh failed: ${message}`);
            throw error;
        } finally {
            this.loaded = true;
            this.changed.fire();
        }
    }

    public async setFilter(): Promise<void> {
        const filter = await vscode.window.showInputBox({
            title: "Filter .NET processes",
            prompt: "Case-insensitive substring across name, PID, runtime, and platform. * and ? are literal characters; this filters the loaded process inventory only.",
            placeHolder: "For example: MyApp, 4242, linux",
            value: this.filter,
        });
        if (filter === undefined) return;
        this.filter = filter;
        await this.refresh();
    }

    public async clearFilter(): Promise<void> {
        this.filter = "";
        await this.refresh();
    }

    public async invoke(row: ProcessRow | undefined, action: ProcessAction): Promise<void> {
        if (this.invoking) throw new Error("Wait for the current process action to finish.");
        this.invoking = true;
        try {
            const saved = row && "process" in row ? row.process : this.selected;
            if (!saved) throw new Error("Select a process in the process tree first.");
            await this.refresh();
            const process = validateTarget(saved, this.processes);
            await this.action(process, action);
            await this.state.update(STATE_KEY, process);
            this.changed.fire();
        } finally {
            this.invoking = false;
        }
    }
}
