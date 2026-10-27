export interface CpuHandleComparisonCapability {
    version: 1;
    queryView: "diff";
    baselineHandleField: "baselineHandle";
    retainedHandleLimit: number;
    maxTopMovers: number;
}

export interface CpuComparisonQueryResult {
    handle: string;
    baselineHandle: string;
    view: "diff";
    summary?: string;
    data: {
        schema: "dotnet-diagnostics/cpu-sample-handle-comparison/v1";
        baseline: CpuComparisonCapture;
        current: CpuComparisonCapture;
        compatibility: {
            status: string;
            reasons: Array<{ code: string; field: string; message: string }>;
        };
        metrics: CpuComparisonMetric[];
        topMovers: CpuComparisonMover[];
        notes: string[];
    };
}

interface CpuComparisonCapture {
    handle: string;
    processId: number;
    startedAt: string;
    durationSeconds: number;
    totalSamples: number;
    backend?: string | null;
    evidenceKind?: string | null;
    origin: string;
    producingTool?: string | null;
    expiresAt: string;
    unresolvedExclusiveSamples: number;
    unresolvedExclusivePercent?: number | null;
    quality: {
        schema: string;
        limitations: Array<{ category?: string; scope?: string; message?: string }>;
        conclusions?: unknown;
        observed?: unknown;
    };
}

interface CpuComparisonDelta {
    baselineValue?: number | null;
    currentValue?: number | null;
    unit?: string | null;
    deltaAbs?: number | null;
    relativeDelta?: number | null;
    betterDirection: string;
    outcome: string;
    unavailableReason?: string | null;
    baselineDenominator?: number | null;
    currentDenominator?: number | null;
    deltaUnit?: string | null;
    relativeDeltaUnavailableReason?: string | null;
}

interface CpuComparisonMetric {
    name: string;
    population: string;
    aggregation: string;
    normalization: string;
    delta: CpuComparisonDelta;
}

interface CpuComparisonMover {
    displayName: string;
    baselineExclusiveSamples?: number | null;
    currentExclusiveSamples?: number | null;
    baselineTotalSamples: number;
    currentTotalSamples: number;
    exclusiveShare: CpuComparisonDelta;
    unresolved: boolean;
}

const record = (value: unknown): value is Record<string, unknown> =>
    value !== null && typeof value === "object" && !Array.isArray(value);
const safeNumber = (value: unknown): value is number =>
    typeof value === "number" && Number.isFinite(value)
    && (!Number.isInteger(value) || Number.isSafeInteger(value));
const optionalNumber = (value: unknown): boolean => value === undefined || value === null || safeNumber(value);
const optionalString = (value: unknown): boolean => value === undefined || value === null || typeof value === "string";

export function supportsCpuHandleComparison(value: unknown): value is CpuHandleComparisonCapability {
    return record(value)
        && value.version === 1
        && value.queryView === "diff"
        && value.baselineHandleField === "baselineHandle"
        && Number.isSafeInteger(value.retainedHandleLimit) && Number(value.retainedHandleLimit) >= 2
        && Number.isSafeInteger(value.maxTopMovers) && Number(value.maxTopMovers) >= 1
        && Number(value.maxTopMovers) <= 50;
}

export function isCpuComparisonQueryResult(value: unknown): value is CpuComparisonQueryResult {
    if (!record(value) || typeof value.handle !== "string" || typeof value.baselineHandle !== "string"
        || value.view !== "diff" || !record(value.data)
        || value.data.schema !== "dotnet-diagnostics/cpu-sample-handle-comparison/v1") return false;
    const data = value.data;
    if (!isCapture(data.baseline) || !isCapture(data.current) || !record(data.compatibility)
        || typeof data.compatibility.status !== "string" || !Array.isArray(data.compatibility.reasons)
        || data.compatibility.reasons.length > 20 || !data.compatibility.reasons.every(reason =>
            record(reason) && typeof reason.code === "string" && typeof reason.field === "string"
            && typeof reason.message === "string")
        || !Array.isArray(data.metrics) || data.metrics.length > 20 || !data.metrics.every(isMetric)
        || !Array.isArray(data.topMovers) || data.topMovers.length > 50 || !data.topMovers.every(isMover)
        || !Array.isArray(data.notes) || data.notes.length > 50 || !data.notes.every(note => typeof note === "string")) {
        return false;
    }
    return optionalString(value.summary);
}

function isCapture(value: unknown): value is CpuComparisonCapture {
    return record(value)
        && typeof value.handle === "string"
        && Number.isSafeInteger(value.processId) && Number(value.processId) > 0
        && typeof value.startedAt === "string" && safeNumber(value.durationSeconds)
        && Number(value.durationSeconds) >= 0
        && Number.isSafeInteger(value.totalSamples) && Number(value.totalSamples) >= 0
        && optionalString(value.backend) && optionalString(value.evidenceKind)
        && typeof value.origin === "string" && optionalString(value.producingTool)
        && typeof value.expiresAt === "string"
        && Number.isSafeInteger(value.unresolvedExclusiveSamples) && Number(value.unresolvedExclusiveSamples) >= 0
        && optionalNumber(value.unresolvedExclusivePercent)
        && record(value.quality) && typeof value.quality.schema === "string"
        && Array.isArray(value.quality.limitations) && value.quality.limitations.length <= 50
        && value.quality.limitations.every(item => record(item)
            && optionalString(item.category) && optionalString(item.scope) && optionalString(item.message));
}

function isDelta(value: unknown): value is CpuComparisonDelta {
    return record(value)
        && optionalNumber(value.baselineValue) && optionalNumber(value.currentValue)
        && optionalString(value.unit) && optionalNumber(value.deltaAbs) && optionalNumber(value.relativeDelta)
        && typeof value.betterDirection === "string" && typeof value.outcome === "string"
        && optionalString(value.unavailableReason)
        && optionalNumber(value.baselineDenominator) && optionalNumber(value.currentDenominator)
        && optionalString(value.deltaUnit) && optionalString(value.relativeDeltaUnavailableReason);
}

function isMetric(value: unknown): value is CpuComparisonMetric {
    return record(value) && typeof value.name === "string" && typeof value.population === "string"
        && typeof value.aggregation === "string" && typeof value.normalization === "string"
        && isDelta(value.delta);
}

function isMover(value: unknown): value is CpuComparisonMover {
    return record(value) && typeof value.displayName === "string"
        && optionalNumber(value.baselineExclusiveSamples) && optionalNumber(value.currentExclusiveSamples)
        && Number.isSafeInteger(value.baselineTotalSamples) && Number(value.baselineTotalSamples) >= 0
        && Number.isSafeInteger(value.currentTotalSamples) && Number(value.currentTotalSamples) >= 0
        && isDelta(value.exclusiveShare) && typeof value.unresolved === "boolean";
}

interface CpuComparisonActions {
    openProfile: (role: "baseline" | "current", view: string, method?: string) => void;
    useCurrentAsNextBaseline: () => void;
}

function renderCpuComparison(
    container: HTMLElement,
    result: CpuComparisonQueryResult,
    actions: CpuComparisonActions,
): void {
    const data = result.data;
    container.className = (container.className + " cpu-comparison-view").trim();
    const text = (parent: HTMLElement, value: string, className?: string): HTMLElement => {
        const element = document.createElement("p");
        element.textContent = value;
        if (className) element.className = className;
        parent.appendChild(element);
        return element;
    };
    const button = (label: string, action: () => void): HTMLButtonElement => {
        const element = document.createElement("button");
        element.type = "button";
        element.textContent = label;
        element.addEventListener("click", action);
        return element;
    };
    const heading = document.createElement("h3");
    heading.textContent = "CPU baseline and current";
    heading.tabIndex = -1;
    container.appendChild(heading);
    text(container,
        "Direction is chronological: baseline is the earlier retained capture and current is the later capture. "
        + "The CLI computes objective deltas; the extension does not reverse or reinterpret them.",
        "query-view-provenance");

    const captures = document.createElement("div");
    captures.className = "cpu-comparison-captures";
    const captureCard = (role: "baseline" | "current", capture: CpuComparisonCapture): HTMLElement => {
        const card = document.createElement("section");
        card.className = "cpu-comparison-capture";
        const title = document.createElement("h4");
        title.textContent = role === "baseline" ? "Baseline (earlier)" : "Current (later)";
        card.appendChild(title);
        const metadata = document.createElement("dl");
        const fact = (label: string, value: string): void => {
            const term = document.createElement("dt");
            term.textContent = label;
            const description = document.createElement("dd");
            description.textContent = value;
            metadata.append(term, description);
        };
        const started = new Date(capture.startedAt);
        fact("Target", "PID " + capture.processId);
        fact("Captured", Number.isFinite(started.getTime()) ? started.toLocaleString() : "Unavailable");
        fact("Duration", capture.durationSeconds + " s");
        fact("Samples", String(capture.totalSamples));
        fact("Backend", capture.backend || "Unavailable");
        fact("Evidence", capture.evidenceKind || "Unavailable");
        fact("Origin", capture.origin || "Unavailable");
        fact("Expires", capture.expiresAt || "Unavailable");
        card.appendChild(metadata);
        const controls = document.createElement("div");
        controls.className = "cpu-overview-actions";
        controls.append(
            button("Open " + role + " overview", () => actions.openProfile(role, "triage")),
            button("Open " + role + " call tree", () => actions.openProfile(role, "call-tree")),
            button("Open " + role + " hot path", () => actions.openProfile(role, "hot-path")),
            button("Open " + role + " top methods", () => actions.openProfile(role, "top-methods")),
        );
        card.appendChild(controls);
        const quality = document.createElement("details");
        const summary = document.createElement("summary");
        summary.textContent = "Quality and unavailable evidence";
        quality.appendChild(summary);
        text(quality, "Unresolved exclusive evidence: " + capture.unresolvedExclusiveSamples + " samples ("
            + format(capture.unresolvedExclusivePercent, "%") + ").", "query-view-provenance");
        if (capture.quality.limitations.length === 0) {
            text(quality, "No structured limitation was returned. This is not proof of complete evidence.");
        } else {
            const list = document.createElement("ul");
            for (const limitation of capture.quality.limitations) {
                const item = document.createElement("li");
                item.textContent = [limitation.category, limitation.scope, limitation.message].filter(Boolean).join(" - ");
                list.appendChild(item);
            }
            quality.appendChild(list);
        }
        card.appendChild(quality);
        return card;
    };
    captures.append(captureCard("baseline", data.baseline), captureCard("current", data.current));
    container.appendChild(captures);

    const compatibility = document.createElement("section");
    compatibility.className = "cpu-comparison-compatibility";
    const compatibilityHeading = document.createElement("h4");
    compatibilityHeading.textContent = "Comparison availability: " + data.compatibility.status;
    compatibility.appendChild(compatibilityHeading);
    if (data.compatibility.reasons.length) {
        const list = document.createElement("ul");
        for (const reason of data.compatibility.reasons) {
            const item = document.createElement("li");
            item.textContent = reason.code + " (" + reason.field + "): " + reason.message;
            list.appendChild(item);
        }
        compatibility.appendChild(list);
    } else {
        text(compatibility, "The CLI found no structural backend/evidence mismatch. Workload equivalence is still not established.");
    }
    container.appendChild(compatibility);

    const metricsHeading = document.createElement("h4");
    metricsHeading.textContent = "Capture-level objective deltas";
    container.appendChild(metricsHeading);
    const metrics = document.createElement("table");
    metrics.className = "query-view-table cpu-comparison-table";
    metrics.setAttribute("aria-label", "Capture-level objective deltas");
    addHeader(metrics, ["Metric", "Baseline", "Current", "Absolute delta", "Relative delta", "Outcome / unavailable"]);
    const metricsBody = document.createElement("tbody");
    for (const metric of data.metrics) {
        const row = document.createElement("tr");
        addCells(row, [
            metric.name + " · " + metric.population + " · " + metric.normalization,
            valueAndDenominator(metric.delta.baselineValue, metric.delta.unit, metric.delta.baselineDenominator),
            valueAndDenominator(metric.delta.currentValue, metric.delta.unit, metric.delta.currentDenominator),
            format(metric.delta.deltaAbs, metric.delta.deltaUnit),
            formatRelative(metric.delta),
            metric.delta.unavailableReason || metric.delta.outcome,
        ], ["Metric", "Baseline", "Current", "Absolute delta", "Relative delta", "Outcome / unavailable"], true);
        metricsBody.appendChild(row);
    }
    metrics.appendChild(metricsBody);
    const metricsWrap = document.createElement("div");
    metricsWrap.className = "query-view-table-wrap";
    metricsWrap.appendChild(metrics);
    container.appendChild(metricsWrap);

    const moversHeading = document.createElement("h4");
    moversHeading.textContent = "Top method movers (bounded to " + data.topMovers.length + " returned rows)";
    container.appendChild(moversHeading);
    text(container,
        "Share delta uses each capture's own retained-sample denominator. A method missing from one projection remains unavailable; it is not coerced to zero.",
        "query-view-provenance");
    const movers = document.createElement("table");
    movers.className = "query-view-table cpu-comparison-table";
    movers.setAttribute("aria-label", "Top method movers");
    addHeader(movers, ["Method", "Presence", "Baseline samples / share", "Current samples / share",
        "Raw sample delta", "Share delta", "Relative share delta", "Inspect evidence"]);
    const moversBody = document.createElement("tbody");
    for (const mover of data.topMovers) {
        const row = document.createElement("tr");
        const presence = mover.baselineExclusiveSamples == null
            ? "Added to current retained projection"
            : mover.currentExclusiveSamples == null
                ? "Removed from current retained projection"
                : "Observed in both retained projections";
        const rawDelta = mover.baselineExclusiveSamples == null || mover.currentExclusiveSamples == null
            ? "Unavailable"
            : format(mover.currentExclusiveSamples - mover.baselineExclusiveSamples, "samples");
        addCells(row, [
            mover.displayName + (mover.unresolved ? " (unresolved)" : ""),
            presence,
            sampleAndShare(mover.baselineExclusiveSamples, mover.exclusiveShare.baselineValue, mover.baselineTotalSamples),
            sampleAndShare(mover.currentExclusiveSamples, mover.exclusiveShare.currentValue, mover.currentTotalSamples),
            rawDelta,
            formatDelta(mover.exclusiveShare),
            formatRelative(mover.exclusiveShare),
        ], ["Method", "Presence", "Baseline samples / share", "Current samples / share",
            "Raw sample delta", "Share delta", "Relative share delta"], true);
        const actionCell = document.createElement("td");
        actionCell.setAttribute("data-label", "Inspect evidence");
        const controls = document.createElement("div");
        controls.className = "cpu-comparison-row-actions";
        if (mover.baselineExclusiveSamples != null) {
            controls.appendChild(button("Baseline callers / callees",
                () => actions.openProfile("baseline", "caller-callee", mover.displayName)));
        }
        if (mover.currentExclusiveSamples != null) {
            controls.appendChild(button("Current callers / callees",
                () => actions.openProfile("current", "caller-callee", mover.displayName)));
        }
        actionCell.appendChild(controls);
        row.appendChild(actionCell);
        moversBody.appendChild(row);
    }
    movers.appendChild(moversBody);
    const moversWrap = document.createElement("div");
    moversWrap.className = "query-view-table-wrap";
    moversWrap.appendChild(movers);
    container.appendChild(moversWrap);

    const limits = document.createElement("details");
    const limitsSummary = document.createElement("summary");
    limitsSummary.textContent = "Evidence limits and lifecycle";
    limits.appendChild(limitsSummary);
    const notes = document.createElement("ul");
    for (const note of data.notes) {
        const item = document.createElement("li");
        item.textContent = note;
        notes.appendChild(item);
    }
    for (const statement of [
        "This live pair exists only in the owning CLI process and is distinct from durable saved-capture comparison.",
        "Redacted export remains scoped to the selected CPU capture; comparison rows are not silently substituted into that export.",
        "No automatic capture, multi-evidence correlation, causal claim, or application/runtime composition is performed.",
    ]) {
        const item = document.createElement("li");
        item.textContent = statement;
        notes.appendChild(item);
    }
    limits.appendChild(notes);
    container.appendChild(limits);

    const lifecycle = document.createElement("div");
    lifecycle.className = "cpu-overview-actions";
    lifecycle.appendChild(button("Use current as baseline for the next capture", actions.useCurrentAsNextBaseline));
    container.appendChild(lifecycle);

    function format(value: number | null | undefined, unit?: string | null): string {
        if (value == null) return "Unavailable";
        const rendered = Number.isInteger(value) ? String(value) : value.toFixed(4).replace(/0+$/, "").replace(/\.$/, "");
        return rendered + (unit ? " " + unit : "");
    }
    function formatRelative(delta: CpuComparisonDelta): string {
        const reason = delta.relativeDeltaUnavailableReason || delta.unavailableReason;
        return delta.relativeDelta == null
            ? "Unavailable" + (reason ? " (" + reason + ")" : "")
            : (delta.relativeDelta * 100).toFixed(2) + "%";
    }
    function formatDelta(delta: CpuComparisonDelta): string {
        return delta.deltaAbs == null
            ? "Unavailable" + (delta.unavailableReason ? " (" + delta.unavailableReason + ")" : "")
            : format(delta.deltaAbs, delta.deltaUnit);
    }
    function valueAndDenominator(
        value: number | null | undefined,
        unit: string | null | undefined,
        denominator: number | null | undefined,
    ): string {
        const rendered = format(value, unit);
        return denominator == null ? rendered : rendered + " · denominator " + denominator + " samples";
    }
    function sampleAndShare(samples: number | null | undefined, share: number | null | undefined, denominator: number): string {
        return samples == null || share == null
            ? "Unavailable (denominator " + denominator + " samples)"
            : samples + " samples / " + format(share, "%") + " of " + denominator;
    }
    function addHeader(table: HTMLElement, labels: string[]): void {
        const head = document.createElement("thead");
        const row = document.createElement("tr");
        for (const label of labels) {
            const cell = document.createElement("th");
            cell.scope = "col";
            cell.textContent = label;
            row.appendChild(cell);
        }
        head.appendChild(row);
        table.appendChild(head);
    }
    function addCells(row: HTMLElement, values: string[], labels: string[], firstCellIsHeader = false): void {
        values.forEach((value, index) => {
            const cell = document.createElement(index === 0 && firstCellIsHeader ? "th" : "td");
            if (index === 0 && firstCellIsHeader) cell.setAttribute("scope", "row");
            cell.setAttribute("data-label", labels[index]);
            cell.textContent = value;
            row.appendChild(cell);
        });
    }
}

export const CPU_COMPARISON_SCRIPT = renderCpuComparison.toString();

export const CPU_COMPARISON_STYLES = `
    .cpu-comparison-view { container-type:inline-size; }
    .cpu-comparison-captures { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:.7rem; }
    .cpu-comparison-capture, .cpu-comparison-compatibility {
        border:1px solid var(--vscode-panel-border); padding:.65rem; min-width:0;
    }
    .cpu-comparison-capture h4, .cpu-comparison-compatibility h4 { margin:.1rem 0 .45rem; }
    .cpu-comparison-capture dl { display:grid; grid-template-columns:max-content minmax(0,1fr); gap:.25rem .6rem; }
    .cpu-comparison-capture dt { color:var(--vscode-descriptionForeground); }
    .cpu-comparison-capture dd { margin:0; overflow-wrap:anywhere; font-variant-numeric:tabular-nums; }
    .cpu-comparison-table { min-width:64rem; }
    .cpu-comparison-table td, .cpu-comparison-table th { vertical-align:top; overflow-wrap:anywhere; }
    .cpu-comparison-row-actions { display:flex; flex-direction:column; gap:.3rem; min-width:10rem; }
    @container (max-width:650px) {
        .cpu-comparison-captures { grid-template-columns:1fr; }
        .cpu-comparison-capture .cpu-overview-actions button { flex:1 1 11rem; min-width:0; }
        .query-view-table-wrap:has(.cpu-comparison-table) { overflow-x:visible; }
        .cpu-comparison-table {
            display:block; min-width:0; width:100%; border:0; background:transparent;
        }
        .cpu-comparison-table thead {
            position:absolute; width:1px; height:1px; padding:0; margin:-1px;
            overflow:hidden; clip:rect(0,0,0,0); white-space:nowrap; border:0;
        }
        .cpu-comparison-table tbody { display:grid; gap:.65rem; }
        .cpu-comparison-table tr {
            display:block; border:1px solid var(--vscode-panel-border);
            background:var(--vscode-editorWidget-background); min-width:0;
        }
        .cpu-comparison-table td, .cpu-comparison-table tbody th {
            display:grid; grid-template-columns:minmax(8.5rem,38%) minmax(0,1fr);
            gap:.65rem; width:auto; padding:.45rem .55rem; border:0;
            border-bottom:1px solid var(--vscode-panel-border); text-align:left;
            min-width:0; max-width:none; white-space:normal;
        }
        .cpu-comparison-table td:last-child { border-bottom:0; }
        .cpu-comparison-table td::before, .cpu-comparison-table tbody th::before {
            content:attr(data-label); color:var(--vscode-descriptionForeground);
            font-weight:600; overflow-wrap:anywhere;
        }
        .cpu-comparison-table tbody th {
            grid-template-columns:1fr; gap:.2rem; font-weight:600;
        }
        .cpu-comparison-table tbody th::before { font-size:.9em; font-weight:400; }
        .cpu-comparison-row-actions { min-width:0; }
        .cpu-comparison-row-actions button { width:100%; min-height:2.25rem; white-space:normal; }
    }
    @container (max-width:430px) {
        .cpu-comparison-capture .cpu-overview-actions button { flex-basis:100%; }
        .cpu-comparison-table td {
            grid-template-columns:1fr; gap:.2rem;
        }
    }
    @media (forced-colors: active) {
        .cpu-comparison-capture, .cpu-comparison-compatibility,
        .cpu-comparison-table tr, .cpu-comparison-table td, .cpu-comparison-table tbody th {
            border-color:CanvasText;
        }
    }
`;
