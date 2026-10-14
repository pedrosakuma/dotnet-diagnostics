import * as vscode from "vscode";
import { captureListArgs, captureShowArgs, parseCaptureList, parseCaptureShow, parseEnvelope, runHistoryCli,
    type CaptureSummary } from "./processHistory";

export interface ComparisonMetric {
    key: string; leftValue: number | null; rightValue: number | null;
    absoluteDelta: number | null; relativeDelta: number | null; unavailableReason: string | null;
    unit: string | null; aggregation: string; normalization: string; population: string;
}
export interface ComparisonResult { status: string; reasons: string[]; metrics: ComparisonMetric[] }
const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
const id = (s: string): string => {
    if (!/^[a-f0-9]{32}$/.test(s)) throw new Error("Comparison requires CLI catalog capture and artifact IDs.");
    return s;
};
export function compareArgs(baseline: CaptureSummary, baselineArtifact: string, candidate: CaptureSummary, candidateArtifact: string): string[] {
    for (const [capture, artifact] of [[baseline, baselineArtifact], [candidate, candidateArtifact]] as const) {
        if (capture.state !== "sealed" || !capture.artifacts.some(a => a.artifactId === artifact))
            throw new Error("Choose a sealed capture and an artifact from its CLI catalog.");
    }
    return ["compare", "--baseline-capture-id", id(baseline.captureId), "--baseline-artifact-id", id(baselineArtifact),
        "--candidate-capture-id", id(candidate.captureId), "--candidate-artifact-id", id(candidateArtifact), "--json"];
}
export function parseComparison(stdout: string): ComparisonResult {
    const value = parseEnvelope(stdout);
    if (value.schema !== "dotnet-diagnostics/historical-comparison/v1" || !record(value.compatibility)
        || typeof value.compatibility.status !== "string" || !Array.isArray(value.compatibility.reasons)
        || !Array.isArray(value.metrics) || value.metrics.length > 2_000)
        throw new Error("The CLI returned an unsupported historical comparison.");
    const metrics = value.metrics.map((row): ComparisonMetric => {
        if (!record(row) || typeof row.key !== "string") throw new Error("Invalid CLI comparison metric.");
        const number = (key: string): number | null => {
            if (row[key] === null) return null;
            if (typeof row[key] !== "number" || !Number.isFinite(row[key])
                || Number.isInteger(row[key]) && !Number.isSafeInteger(row[key]))
                throw new Error("Invalid or unsafe-precision CLI comparison measurement. Use the CLI for full precision.");
            return row[key];
        };
        if (row.unavailableReason !== null && typeof row.unavailableReason !== "string") throw new Error("Invalid comparison limitation.");
        if (row.unit !== null && typeof row.unit !== "string") throw new Error("Invalid comparison unit.");
        if (typeof row.aggregation !== "string" || typeof row.normalization !== "string" || typeof row.population !== "string")
            throw new Error("Invalid comparison metric semantics.");
        return { key: row.key, leftValue: number("leftValue"), rightValue: number("rightValue"),
            absoluteDelta: number("absoluteDelta"), relativeDelta: number("relativeDelta"), unavailableReason: row.unavailableReason,
            unit: row.unit, aggregation: row.aggregation, normalization: row.normalization, population: row.population };
    });
    const reasons = value.compatibility.reasons.map(reason => {
        if (!record(reason) || typeof reason.message !== "string") throw new Error("Invalid comparison compatibility reason.");
        return reason.message;
    });
    return { status: value.compatibility.status, reasons, metrics };
}
export async function compareSaved(cli: string, candidate: CaptureSummary, artifactId: string): Promise<ComparisonResult | undefined> {
    const captures = parseCaptureList(await runHistoryCli(cli, captureListArgs())).captures.filter(c => c.state === "sealed");
    const baseline = await vscode.window.showQuickPick(captures.map(c => ({
        label: c.name, description: `${c.createdUtc} - ${c.captureId}`, capture: c,
    })), { placeHolder: "Choose baseline from the latest 50 saved captures" });
    if (!baseline) return;
    const detail = parseCaptureShow(await runHistoryCli(cli, captureShowArgs(baseline.capture.captureId)));
    const artifact = await vscode.window.showQuickPick(detail.artifacts.map(a => ({
        label: a.name, description: a.kind, artifactId: a.artifactId,
    })), { placeHolder: "Choose baseline artifact (the CLI verifies compatibility)" });
    if (!artifact) return;
    return parseComparison(await runHistoryCli(cli, compareArgs(detail, artifact.artifactId, candidate, artifactId)));
}

function renderComparison(containerId: string, result: ComparisonResult): void {
    const root = document.getElementById(containerId);
    if (!root) return;
    root.replaceChildren();
    const text = (tag: string, value: unknown): HTMLElement => {
        const el = document.createElement(tag); el.textContent = value == null ? "Unavailable" : String(value); return el;
    };
    root.appendChild(text("h3", `Baseline comparison: ${result.status}`));
    root.appendChild(text("p", "Left = baseline; right = candidate. Relative delta is a ratio, not percent. Unavailable values are not zero. Retained observations do not establish workload equivalence, causation or performance gains."));
    for (const reason of result.reasons) root.appendChild(text("p", reason));
    const table = document.createElement("table"), heading = document.createElement("tr");
    for (const label of ["Metric", "Unit", "Aggregation / normalization / population", "Baseline", "Candidate", "Absolute delta", "Relative ratio", "Limitation"]) heading.appendChild(text("th", label));
    table.appendChild(heading);
    for (const row of result.metrics.slice(0, 200)) {
        const tr = document.createElement("tr");
        for (const value of [row.key, row.unit, `${row.aggregation} / ${row.normalization} / ${row.population}`,
            row.leftValue, row.rightValue, row.absoluteDelta, row.relativeDelta, row.unavailableReason]) tr.appendChild(text("td", value));
        table.appendChild(tr);
    }
    root.appendChild(table);
    if (result.metrics.length > 200) root.appendChild(text("p", `${result.metrics.length - 200} metric(s) omitted by the panel.`));
}
export const COMPARISON_RENDERER_SCRIPT = renderComparison.toString();
