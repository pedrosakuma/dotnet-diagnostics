import * as vscode from "vscode";

const METRICS = new Set([
    "totalSamples", "inclusiveSamples", "exclusiveSamples", "instanceCount", "totalBytes", "totalBytesPercent",
    "totalExceptions", "count", "totalEvents", "totalCollections", "totalThreads", "omittedThreads", "totalLocks",
    "omittedLocks", "waitingThreadCount", "generation", "recentCap", "retained", "omitted", "totalRequests",
    "requestsStarted", "requestsStopped", "totalAllocations", "totalAllocatedBytes", "allocationCount",
    "durationSeconds", "reportedDurationSeconds", "requestedDurationSeconds", "leftValue", "rightValue",
    "absoluteDelta", "relativeDelta", "leftDenominator", "rightDenominator",
]);
const CONTAINERS = new Set([
    "data", "payload", "findings", "topHotspots", "topTypes", "topTypesByBytes", "topTypesByInstances",
    "byType", "threads", "locks", "generations", "metrics", "tables", "rows", "evidence", "provenance",
]);
const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);

export interface ShareableResult {
    schema: "dotnet-diagnostics/redacted-measurements/v1";
    notice: string;
    measurements: Array<{ metric: string; value: number | null }>;
    omitted: number;
}

// Export measurements, not application strings, dynamic keys, addresses or arbitrary object values.
export function projectShareable(value: unknown): ShareableResult {
    const measurements: ShareableResult["measurements"] = [];
    let omitted = 0, visited = 0;
    function visit(node: unknown, path: string, depth: number): void {
        if (++visited > 10_000 || depth > 10 || measurements.length >= 2_000) { omitted++; return; }
        if (Array.isArray(node)) {
            for (let i = 0; i < Math.min(node.length, 200); i++) visit(node[i], `${path}[${i}]`, depth + 1);
            omitted += Math.max(0, node.length - 200);
        } else if (record(node)) {
            for (const [rawKey, item] of Object.entries(node)) {
                const key = rawKey.charAt(0).toLowerCase() + rawKey.slice(1);
                const next = path ? `${path}.${key}` : key;
                if ((METRICS.has(key) || key === "value" && /^metrics\[\d+\]$/.test(path))
                    && (item === null || typeof item === "number" && Number.isFinite(item)
                        && (!Number.isInteger(item) || Number.isSafeInteger(item)))) {
                    if (measurements.length < 2_000) measurements.push({ metric: next, value: item });
                    else omitted++;
                } else if (CONTAINERS.has(key)) visit(item, next, depth + 1);
                else omitted++;
            }
        } else omitted++;
    }
    visit(value, "", 0);
    return { schema: "dotnet-diagnostics/redacted-measurements/v1",
        notice: "Lossy measurement-only export. Application text, symbols, paths, identifiers, arbitrary values and diagnostic interpretations are excluded. Missing evidence is not zero. Array positions identify retained rows, not application entities.",
        measurements, omitted };
}

export function renderShareable(value: unknown, format: "json" | "csv"): string {
    const projected = projectShareable(value);
    if (format === "json") return JSON.stringify(projected, null, 2) + "\n";
    const quote = (s: string): string => `"${s.replace(/"/g, '""')}"`;
    return `notice,${quote(projected.notice)}\nomitted,${projected.omitted}\nmetric,value\n`
        + projected.measurements.map(m => `${quote(m.metric)},${m.value ?? ""}`).join("\n") + "\n";
}

export async function openShareable(value: unknown, format: "json" | "csv"): Promise<void> {
    const document = await vscode.workspace.openTextDocument({
        content: renderShareable(value, format), language: format === "json" ? "json" : "plaintext",
    });
    await vscode.window.showTextDocument(document, { preview: false });
}

export class ResultExporter {
    private current?: unknown;
    private readonly navigation = new Map<string, unknown>();
    private selectedNavigation?: string;
    public set(value: unknown): void { this.current = value; }
    public clear(): void { this.current = undefined; this.navigation.clear(); this.selectedNavigation = undefined; }
    public observe(message: object): void {
        if (!record(message)) return;
        if (message.type === "capture") {
            this.clear();
            this.set(message.result ?? message.summary);
        } else if (message.type === "navigation") {
            const key = this.navigationKey(message);
            if (!key) return;
            this.navigation.delete(key);
            this.navigation.set(key, message.result);
            while (this.navigation.size > 20) this.navigation.delete(this.navigation.keys().next().value!);
            if (this.selectedNavigation === key) this.set(message.result);
        }
    }
    private navigationKey(message: Record<string, unknown>): string | undefined {
        return typeof message.address === "string" && /^0x[0-9a-f]{1,16}$/.test(message.address)
            && ["object", "gcroot", "static-fields"].includes(String(message.view))
            ? `${message.view}:${message.address}` : undefined;
    }
    public async handle(message: unknown): Promise<boolean> {
        if (record(message) && message.type === "navigationSelection") {
            this.selectedNavigation = this.navigationKey(message);
            this.current = message.ready === true && this.selectedNavigation
                ? this.navigation.get(this.selectedNavigation) : undefined;
            return true;
        }
        if (!record(message) || message.type !== "exportResult" || !["json", "csv"].includes(String(message.format))) return false;
        try {
            if (this.current === undefined) throw new Error("Collect a result or select a saved view before exporting.");
            await openShareable(this.current, message.format === "json" ? "json" : "csv");
        } catch (error) {
            await vscode.window.showErrorMessage(`Result export failed: ${error instanceof Error ? error.message : String(error)}`);
        }
        return true;
    }
}

export const RESULT_EXPORT_BUTTONS = `<div class="controls"><button id="exportJson">Export redacted JSON</button>
<button id="exportCsv">Export redacted CSV</button></div>
<p>Exports contain measurements only; application text and diagnostic interpretations are excluded. Save the opened document locally.</p>`;
export const RESULT_EXPORT_SCRIPT = `for (const format of ["json", "csv"]) {
  document.getElementById(format === "json" ? "exportJson" : "exportCsv").addEventListener("click",
    () => vscode.postMessage({ type: "exportResult", format }));
}`;
