import * as vscode from "vscode";
import { captureListArgs, parseCaptureList, parseEnvelope, runHistoryCli,
    type CaptureArtifact, type CaptureSummary } from "./processHistory";

export interface ComparisonMetric {
    key: string; leftValue: number | null; rightValue: number | null;
    absoluteDelta: number | null; relativeDelta: number | null; unavailableReason: string | null;
    unit: string | null; aggregation: string; normalization: string; population: string;
}
export interface ComparisonIdentity {
    captureId: string; artifactId: string; kind: string;
}
export interface ComparisonReason {
    code: string; side: string; field: string; message: string;
}
export interface ComparisonSideQuality {
    status: "complete" | "incomplete" | "unknown";
    details: string[];
}
export interface ComparisonResult {
    status: string; reasons: ComparisonReason[]; metrics: ComparisonMetric[];
    baseline: ComparisonIdentity; candidate: ComparisonIdentity;
    quality: { baseline: ComparisonSideQuality; candidate: ComparisonSideQuality };
}
export interface ComparisonCatalog {
    captures: CaptureSummary[];
    truncated: boolean;
}
export type ComparisonDirection = "current-candidate" | "current-baseline";
export type ComparisonChoiceCompatibility = "matching" | "unknown" | "incompatible";

interface ComparisonArtifactChoice extends vscode.QuickPickItem {
    capture?: CaptureSummary;
    artifactId?: string;
    compatibility?: ComparisonChoiceCompatibility;
}

const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
const id = (s: string): string => {
    if (!/^[a-f0-9]{32}$/.test(s)) throw new Error("Comparison requires CLI catalog capture and artifact IDs.");
    return s;
};
const findArtifact = (capture: CaptureSummary, artifactId: string): CaptureArtifact => {
    const artifact = capture.artifacts.find(a => a.artifactId === artifactId);
    if (capture.state !== "sealed" || !artifact)
        throw new Error("Choose a successfully loaded artifact from a sealed CLI catalog capture.");
    return artifact;
};

export function compareArgs(baseline: CaptureSummary, baselineArtifact: string, candidate: CaptureSummary, candidateArtifact: string): string[] {
    for (const [capture, artifact] of [[baseline, baselineArtifact], [candidate, candidateArtifact]] as const) {
        if (capture.state !== "sealed" || !capture.artifacts.some(a => a.artifactId === artifact))
            throw new Error("Choose a sealed capture and an artifact from its CLI catalog.");
    }
    return ["compare", "--baseline-capture-id", id(baseline.captureId), "--baseline-artifact-id", id(baselineArtifact),
        "--candidate-capture-id", id(candidate.captureId), "--candidate-artifact-id", id(candidateArtifact), "--json"];
}

function parseIdentity(value: unknown): ComparisonIdentity {
    if (!record(value) || !record(value.reference) || typeof value.kind !== "string"
        || typeof value.reference.captureId !== "string" || typeof value.reference.artifactId !== "string")
        throw new Error("Invalid CLI comparison identity.");
    return { captureId: id(value.reference.captureId), artifactId: id(value.reference.artifactId), kind: value.kind };
}

function parseQuality(value: unknown): ComparisonSideQuality {
    if (!record(value) || !record(value.capture)
        || typeof value.capture.isComplete !== "boolean" || typeof value.capture.isIncomplete !== "boolean")
        throw new Error("Invalid CLI comparison quality.");
    const status = value.capture.isIncomplete ? "incomplete" : value.capture.isComplete ? "complete" : "unknown";
    const details: string[] = [];
    if (value.notes != null) {
        if (!Array.isArray(value.notes) || value.notes.length > 2_000 || !value.notes.every(note => typeof note === "string"))
            throw new Error("Invalid CLI comparison quality notes.");
        details.push(...value.notes);
    }
    if (value.evidence != null) {
        if (!record(value.evidence) || !Array.isArray(value.evidence.limitations) || value.evidence.limitations.length > 2_000)
            throw new Error("Invalid CLI comparison evidence quality.");
        for (const limitation of value.evidence.limitations) {
            if (!record(limitation) || typeof limitation.detail !== "string")
                throw new Error("Invalid CLI comparison evidence limitation.");
            details.push(limitation.detail);
        }
    }
    return { status, details };
}

export function parseComparison(stdout: string): ComparisonResult {
    const value = parseEnvelope(stdout);
    if (value.schema !== "dotnet-diagnostics/historical-comparison/v1" || !record(value.compatibility)
        || typeof value.compatibility.status !== "string" || !Array.isArray(value.compatibility.reasons)
        || !Array.isArray(value.metrics) || value.metrics.length > 2_000 || !record(value.quality))
        throw new Error("The CLI returned an unsupported historical comparison.");
    const metrics = value.metrics.map((row): ComparisonMetric => {
        if (!record(row) || typeof row.key !== "string") throw new Error("Invalid CLI comparison metric.");
        const number = (key: string): number | null => {
            if (row[key] === null || row[key] === undefined) return null;
            if (typeof row[key] !== "number" || !Number.isFinite(row[key])
                || Number.isInteger(row[key]) && !Number.isSafeInteger(row[key]))
                throw new Error("Invalid or unsafe-precision CLI comparison measurement. Use the CLI for full precision.");
            return row[key];
        };
        if (row.unavailableReason != null && typeof row.unavailableReason !== "string") throw new Error("Invalid comparison limitation.");
        if (row.unit != null && typeof row.unit !== "string") throw new Error("Invalid comparison unit.");
        if (typeof row.aggregation !== "string" || typeof row.normalization !== "string" || typeof row.population !== "string")
            throw new Error("Invalid comparison metric semantics.");
        return { key: row.key, leftValue: number("leftValue"), rightValue: number("rightValue"),
            absoluteDelta: number("absoluteDelta"), relativeDelta: number("relativeDelta"), unavailableReason: row.unavailableReason ?? null,
            unit: row.unit ?? null, aggregation: row.aggregation, normalization: row.normalization, population: row.population };
    });
    const reasons = value.compatibility.reasons.map(reason => {
        if (!record(reason) || typeof reason.code !== "string" || typeof reason.side !== "string"
            || typeof reason.field !== "string" || typeof reason.message !== "string")
            throw new Error("Invalid comparison compatibility reason.");
        return { code: reason.code, side: reason.side, field: reason.field, message: reason.message };
    });
    return {
        status: value.compatibility.status, reasons, metrics,
        baseline: parseIdentity(value.left), candidate: parseIdentity(value.right),
        quality: { baseline: parseQuality(value.quality.left), candidate: parseQuality(value.quality.right) },
    };
}

export async function loadComparisonCatalog(
    cli: string,
    run: (cli: string, args: string[]) => Promise<string> = runHistoryCli,
): Promise<ComparisonCatalog> {
    const captures: CaptureSummary[] = [];
    const seen = new Set<string>();
    let after: string | undefined;
    let truncated = false;
    let examined = 0;
    let pages = 0;
    while (examined < 500 && pages < 10) {
        const page = parseCaptureList(await run(cli, captureListArgs(after)));
        pages++;
        for (const capture of page.captures) {
            if (examined === 500) break;
            examined++;
            if (capture.state === "sealed" && !seen.has(capture.captureId)) {
                captures.push(capture);
                seen.add(capture.captureId);
            }
        }
        if (!page.nextAfterCaptureId) break;
        if (examined === 500 || pages === 10) { truncated = true; break; }
        if (page.nextAfterCaptureId === after) throw new Error("The CLI repeated a saved-capture catalog cursor.");
        after = page.nextAfterCaptureId;
    }
    return { captures, truncated };
}

export function classifyComparisonChoice(
    current: CaptureArtifact, other: CaptureArtifact,
): { compatibility: ComparisonChoiceCompatibility; reason: string } {
    const currentKind = current.kind.trim(), otherKind = other.kind.trim();
    if (!currentKind || !otherKind)
        return { compatibility: "unknown",
            reason: "Artifact-family metadata is unavailable; the CLI must determine whether comparison is supported." };
    if (currentKind !== otherKind)
        return { compatibility: "incompatible",
            reason: `Known family mismatch: current is ${currentKind}; choice is ${otherKind}. The CLI remains authoritative.` };
    return { compatibility: "matching",
        reason: `Matching ${currentKind} family. Backend, origin, architecture, units and metric definitions still require CLI validation.` };
}

function mergeCurrentCapture(catalog: ComparisonCatalog, current: CaptureSummary): CaptureSummary[] {
    return [current, ...catalog.captures.filter(capture => capture.captureId !== current.captureId)];
}

export function comparisonChoiceItems(
    catalog: ComparisonCatalog, currentCapture: CaptureSummary, currentArtifactId: string,
): ComparisonArtifactChoice[] {
    const currentArtifact = findArtifact(currentCapture, currentArtifactId);
    const grouped: Record<ComparisonChoiceCompatibility, ComparisonArtifactChoice[]> = {
        matching: [], unknown: [], incompatible: [],
    };
    for (const capture of mergeCurrentCapture(catalog, currentCapture)) {
        for (const artifact of capture.artifacts) {
            const classification = classifyComparisonChoice(currentArtifact, artifact);
            const isCurrent = capture.captureId === currentCapture.captureId && artifact.artifactId === currentArtifactId;
            grouped[classification.compatibility].push({
                label: `${isCurrent ? "$(arrow-swap) Current artifact: " : ""}${artifact.name || artifact.kind || "Unnamed artifact"}`,
                description: `${capture.name} • ${artifact.kind || "family unreported"} • quality ${capture.qualityStatus ?? "unknown"}`,
                detail: `${classification.reason} Capture ${capture.captureId}; artifact ${artifact.artifactId}.`
                    + (isCurrent ? " Comparing an artifact with itself is allowed and keeps direction explicit." : ""),
                alwaysShow: isCurrent,
                capture, artifactId: artifact.artifactId, compatibility: classification.compatibility,
            });
        }
    }
    const items: ComparisonArtifactChoice[] = [];
    const section = (label: string, choices: ComparisonArtifactChoice[]): void => {
        if (!choices.length) return;
        items.push({ label, kind: vscode.QuickPickItemKind.Separator });
        items.push(...choices);
    };
    section("Matching artifact family — CLI verifies detailed compatibility", grouped.matching);
    section("Compatibility unknown — preserved for CLI validation", grouped.unknown);
    section("Known family mismatch — selectable to view the CLI compatibility result", grouped.incompatible);
    if (grouped.matching.length === 1 && grouped.matching[0].artifactId === currentArtifactId) {
        items.push({
            label: `No other matching-family artifact in the ${catalog.truncated ? "first 500 catalog entries" : "loaded catalog"}. `
                + `Save another ${currentArtifact.kind || "same-family"} capture or use the CLI for older captures.`,
            kind: vscode.QuickPickItemKind.Separator,
        });
    }
    return items;
}

export function orientComparison(
    direction: ComparisonDirection, currentCapture: CaptureSummary, currentArtifactId: string,
    otherCapture: CaptureSummary, otherArtifactId: string,
): { baseline: CaptureSummary; baselineArtifactId: string; candidate: CaptureSummary; candidateArtifactId: string } {
    return direction === "current-baseline"
        ? { baseline: currentCapture, baselineArtifactId: currentArtifactId,
            candidate: otherCapture, candidateArtifactId: otherArtifactId }
        : { baseline: otherCapture, baselineArtifactId: otherArtifactId,
            candidate: currentCapture, candidateArtifactId: currentArtifactId };
}

export async function compareSaved(cli: string, currentCapture: CaptureSummary, artifactId: string): Promise<ComparisonResult | undefined> {
    const currentArtifact = findArtifact(currentCapture, artifactId);
    const currentDescription = `${currentArtifact.name || currentArtifact.kind} (${currentArtifact.kind || "family unreported"})`
        + ` in ${currentCapture.name}; quality ${currentCapture.qualityStatus ?? "unknown"}`;
    const direction = await vscode.window.showQuickPick([
        {
            label: "$(arrow-right) Keep current artifact as candidate (right)",
            description: currentDescription,
            detail: "Choose a saved baseline next. Deltas are candidate minus baseline.",
            direction: "current-candidate" as const,
        },
        {
            label: "$(arrow-left) Use current artifact as baseline (left)",
            description: currentDescription,
            detail: "Choose a saved candidate next. Deltas are candidate minus baseline.",
            direction: "current-baseline" as const,
        },
    ], {
        placeHolder: `Successfully loaded current artifact: ${currentDescription}. Choose its explicit comparison role.`,
        matchOnDescription: true, matchOnDetail: true,
    });
    if (!direction) return;
    const catalog = await loadComparisonCatalog(cli);
    const role = direction.direction === "current-candidate" ? "baseline (left)" : "candidate (right)";
    const other = await vscode.window.showQuickPick(comparisonChoiceItems(catalog, currentCapture, artifactId), {
        placeHolder: `Current ${direction.direction === "current-candidate" ? "candidate (right)" : "baseline (left)"}: `
            + `${currentDescription}. Choose ${role}; up to 500 latest captures are searched.`,
        matchOnDescription: true, matchOnDetail: true,
    });
    if (!other?.capture || !other.artifactId) return;
    const oriented = orientComparison(direction.direction, currentCapture, artifactId, other.capture, other.artifactId);
    return parseComparison(await runHistoryCli(cli, compareArgs(
        oriented.baseline, oriented.baselineArtifactId, oriented.candidate, oriented.candidateArtifactId)));
}

function renderComparison(containerId: string, result: ComparisonResult): void {
    const root = document.getElementById(containerId);
    if (!root) return;
    root.replaceChildren();
    const text = (tag: string, value: unknown): HTMLElement => {
        const el = document.createElement(tag); el.textContent = value == null ? "Unavailable" : String(value); return el;
    };
    root.appendChild(text("h3", `Baseline comparison: ${result.status}`));
    root.appendChild(text("p", `Baseline (left): ${result.baseline.kind}; capture ${result.baseline.captureId}; artifact ${result.baseline.artifactId}.`));
    root.appendChild(text("p", `Candidate (right): ${result.candidate.kind}; capture ${result.candidate.captureId}; artifact ${result.candidate.artifactId}.`));
    root.appendChild(text("p", `Collection quality — baseline: ${result.quality.baseline.status}; candidate: ${result.quality.candidate.status}. `
        + "Unknown quality is not clean evidence."));
    for (const detail of result.quality.baseline.details) root.appendChild(text("p", `Baseline quality: ${detail}`));
    for (const detail of result.quality.candidate.details) root.appendChild(text("p", `Candidate quality: ${detail}`));
    const changes = { up: 0, down: 0, flat: 0, unavailable: 0 };
    for (const row of result.metrics) {
        const delta = row.absoluteDelta;
        changes[delta === null ? "unavailable" : delta > 0 ? "up" : delta < 0 ? "down" : "flat"]++;
    }
    root.appendChild(text("p", `Observed deltas: ${changes.up} up, ${changes.down} down, ${changes.flat} flat, ${changes.unavailable} unavailable. Numeric direction is not a performance verdict.`));
    root.appendChild(text("p", "Left = baseline; right = candidate. Relative delta is a ratio, not percent. Unavailable values are not zero. Retained observations do not establish workload equivalence, causation or performance gains."));
    for (const reason of result.reasons)
        root.appendChild(text("p", `${reason.code} (${reason.side}/${reason.field}): ${reason.message}`));
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
