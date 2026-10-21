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
    private cpuCapture?: unknown;
    private currentEvidenceId?: string;
    private cpuCaptureEvidenceId?: string;
    private readonly navigation = new Map<string, unknown>();
    private selectedNavigation?: string;
    private activeHistory?: Set<string>;
    public set(value: unknown): void { this.current = value; }
    public clear(): void {
        this.set(undefined);
        this.cpuCapture = undefined;
        this.currentEvidenceId = undefined;
        this.cpuCaptureEvidenceId = undefined;
        this.navigation.clear();
        this.selectedNavigation = undefined;
        this.activeHistory = undefined;
    }
    public observe(message: object): void {
        if (!record(message)) return;
        if (message.type === "capture") {
            this.navigation.clear();
            this.selectedNavigation = undefined;
            this.activeHistory = undefined;
            const value = message.result ?? message.summary;
            const kind = typeof message.kind === "string" ? message.kind : undefined;
            const evidenceId = typeof message.evidenceId === "string" ? message.evidenceId : undefined;
            this.currentEvidenceId = evidenceId;
            if (kind === "cpu") {
                this.cpuCapture = value;
                this.cpuCaptureEvidenceId = evidenceId;
            }
            this.current = value;
        } else if (message.type === "navigation") {
            if (message.sensitiveValues === true) {
                if (this.selectedNavigation === this.navigationKey(message)) {
                    this.current = undefined;
                }
                return;
            }
            const key = this.navigationKey(message);
            if (!key || this.activeHistory && !this.activeHistory.has(key)) return;
            this.navigation.delete(key);
            this.navigation.set(key, message.result);
            while (this.navigation.size > 20) this.navigation.delete(this.navigation.keys().next().value!);
            if (this.selectedNavigation === key) this.set(message.result);
        }
    }
    private navigationKey(message: Record<string, unknown>): string | undefined {
        return typeof message.address === "string" && /^0x[0-9a-f]{1,16}$/.test(message.address)
            && ["object", "gcroot", "static-fields"].includes(String(message.view))
            && (message.navigationId === undefined || typeof message.navigationId === "number"
                && Number.isSafeInteger(message.navigationId) && message.navigationId > 0)
            ? `${message.navigationId ?? ""}:${message.view}:${message.address}` : undefined;
    }
    public async handle(message: unknown): Promise<boolean> {
        if (record(message) && message.type === "navigationSelection") {
            if (Array.isArray(message.history) && message.history.length <= 20) {
                const keys = message.history.map(item => record(item) ? this.navigationKey(item) : undefined);
                if (keys.every((key): key is string => key !== undefined)) {
                    this.activeHistory = new Set(keys);
                    for (const key of this.navigation.keys()) if (!this.activeHistory.has(key)) this.navigation.delete(key);
                }
            }
            this.selectedNavigation = this.navigationKey(message);
            this.current = message.ready === true && this.selectedNavigation
                ? this.navigation.get(this.selectedNavigation) : undefined;
            return true;
        }
        if (!record(message) || message.type !== "exportResult" || !["json", "csv"].includes(String(message.format))) return false;
        try {
            const scoped = Object.prototype.hasOwnProperty.call(message, "captureKind");
            const evidenceId = typeof message.evidenceId === "string" ? message.evidenceId : undefined;
            if (evidenceId !== undefined && evidenceId !== (scoped ? this.cpuCaptureEvidenceId : this.currentEvidenceId)) {
                throw new Error("The displayed evidence was replaced before export. Select the current evidence and try again.");
            }
            const value = scoped
                ? message.captureKind === "cpu" ? this.cpuCapture : undefined
                : this.current;
            if (value === undefined) {
                throw new Error(scoped
                    ? "The displayed capture is no longer available for export. Capture it again."
                    : "Collect a result or select a saved view before exporting.");
            }
            await openShareable(value, message.format === "json" ? "json" : "csv");
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
  const button = document.getElementById(format === "json" ? "exportJson" : "exportCsv");
  button.addEventListener("click", () => {
    const captureKind = button.closest("[data-export-kind]")?.dataset.exportKind;
    const evidenceId = button.closest("[data-evidence-id]")?.dataset.evidenceId;
    vscode.postMessage({
      type: "exportResult", format,
      ...(captureKind ? { captureKind } : {}),
      ...(evidenceId ? { evidenceId } : {}),
    });
  });
}`;
