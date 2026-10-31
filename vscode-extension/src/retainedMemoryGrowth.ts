import type { HeapCaptureResult, HeapTypeIdentity, ProtocolFrame, QueryResult } from "./protocol";

export const RETAINED_HEAP_EVIDENCE_LIMIT = 8;

export interface HeapGrowthCapability {
    version: 1;
    available: boolean;
    storeCapacity: number;
    minimumStoreCapacity: number;
    queryView: "growth";
    handleKind: "heap-snapshot";
    baselineField: "baselineHandle";
    objectiveTargetField: "typeFilter";
    comparisonPairsField: "comparisonPairs";
    objectiveSchema: "dotnet-diagnostics/retained-growth-evidence/v1";
    maxAttempts: number;
    minimumRobustPairs: number;
    rankBy: Array<"bytes" | "instances">;
    maxTopN: number;
    storage: "in-memory";
    boundedLifetime: true;
}

export interface RetainedHeapEvidenceDescriptor {
    evidenceId: string;
    source: "live" | "gcdump";
    processId: number;
    capturedAt: string;
    quality: string;
    available: boolean;
    unavailableReason?: string;
    detailsAvailable: boolean;
    identity: "ephemeral";
}

export interface HeapGrowthTypeRow {
    typeFullName: string;
    moduleName?: string | null;
    baselineBytes: number;
    currentBytes: number;
    bytesDelta: number;
    baselineInstances: number;
    currentInstances: number;
    instancesDelta: number;
    identity?: HeapTypeIdentity | null;
    retentionPaths?: unknown[] | null;
    totalRetentionPaths?: number | null;
    omittedRetentionPaths?: number | null;
}

export interface HeapGrowthQueryResult extends QueryResult {
    baselineHandle: string;
    view: "growth";
    baselineOrigin: string;
    currentOrigin: string;
    processId: number;
    baselineCapturedAt: string;
    currentCapturedAt: string;
    elapsed: string;
    rankBy: "bytes" | "instances";
    baselineHeapBytes: number;
    currentHeapBytes: number;
    totalHeapGrowthBytes: number;
    totalGrowers: number;
    growers: HeapGrowthTypeRow[];
    notes?: string[] | null;
    baselineQuality?: unknown;
    currentQuality?: unknown;
}

export interface RetainedGrowthObjectiveResult extends QueryResult {
    baselineHandle: string;
    view: "growth";
    objectiveRetainedGrowth: {
        schema: "dotnet-diagnostics/retained-growth-evidence/v1";
        targetType: HeapTypeIdentity;
        pairMethod: { id: string; version: number };
        counts: Record<string, number>;
        pairs: Array<Record<string, unknown>>;
        repeated: Record<string, unknown>;
        allocationEvidence: Record<string, unknown>;
        rootPathEvidence: Record<string, unknown>;
        observerEffects: Record<string, unknown>;
        assumptions: string[];
        limitations: string[];
    };
}

export interface RetainedHeapEvidence extends RetainedHeapEvidenceDescriptor {
    handle: string;
    result: HeapCaptureResult;
}

const growthRecord = (value: unknown): value is Record<string, unknown> =>
    value !== null && typeof value === "object" && !Array.isArray(value);
const safeNumber = (value: unknown): value is number =>
    typeof value === "number" && Number.isFinite(value)
    && (!Number.isInteger(value) || Number.isSafeInteger(value));
const stringArray = (value: unknown, limit: number): value is string[] =>
    Array.isArray(value) && value.length <= limit && value.every(item => typeof item === "string");

export function supportsHeapGrowth(value: unknown): value is HeapGrowthCapability {
    return growthRecord(value)
        && value.version === 1
        && typeof value.available === "boolean"
        && Number.isSafeInteger(value.storeCapacity) && Number(value.storeCapacity) >= 1
        && Number.isSafeInteger(value.minimumStoreCapacity) && Number(value.minimumStoreCapacity) >= 2
        && value.queryView === "growth"
        && value.handleKind === "heap-snapshot"
        && value.baselineField === "baselineHandle"
        && value.objectiveTargetField === "typeFilter"
        && value.comparisonPairsField === "comparisonPairs"
        && value.objectiveSchema === "dotnet-diagnostics/retained-growth-evidence/v1"
        && Number.isSafeInteger(value.maxAttempts) && Number(value.maxAttempts) >= 1 && Number(value.maxAttempts) <= 8
        && Number.isSafeInteger(value.minimumRobustPairs) && Number(value.minimumRobustPairs) >= 2
        && Array.isArray(value.rankBy) && value.rankBy.length === 2
        && value.rankBy.includes("bytes") && value.rankBy.includes("instances")
        && Number.isSafeInteger(value.maxTopN) && Number(value.maxTopN) >= 1 && Number(value.maxTopN) <= 500
        && value.storage === "in-memory"
        && value.boundedLifetime === true;
}

export function buildHeapGrowthFrame(
    requestId: string,
    currentHandle: string,
    baselineHandle: string,
    rankBy: "bytes" | "instances",
    typeFilter?: string,
): ProtocolFrame {
    const frame: ProtocolFrame = {
        type: "query",
        requestId,
        handle: currentHandle,
        baselineHandle,
        view: "growth",
        rankBy,
        minDeltaPct: 0,
        topN: 25,
    };
    if (typeFilter !== undefined) frame.typeFilter = typeFilter;
    return frame;
}

function isTypeIdentity(value: unknown): value is HeapTypeIdentity {
    return growthRecord(value)
        && typeof value.typeFullName === "string" && value.typeFullName.length > 0
        && (value.moduleName === undefined || value.moduleName === null || typeof value.moduleName === "string")
        && (value.modulePath === undefined || value.modulePath === null || typeof value.modulePath === "string")
        && (value.moduleVersionId === undefined || value.moduleVersionId === null || typeof value.moduleVersionId === "string")
        && (value.metadataToken === undefined || value.metadataToken === null || Number.isSafeInteger(value.metadataToken));
}

function isGrowthTypeRow(value: unknown): value is HeapGrowthTypeRow {
    if (!growthRecord(value) || typeof value.typeFullName !== "string"
        || !safeNumber(value.baselineBytes) || !safeNumber(value.currentBytes) || !safeNumber(value.bytesDelta)
        || !safeNumber(value.baselineInstances) || !safeNumber(value.currentInstances)
        || !safeNumber(value.instancesDelta)) return false;
    return (value.moduleName === undefined || value.moduleName === null || typeof value.moduleName === "string")
        && (value.identity === undefined || value.identity === null || isTypeIdentity(value.identity))
        && (value.retentionPaths === undefined || value.retentionPaths === null
            || Array.isArray(value.retentionPaths) && value.retentionPaths.length <= 8)
        && (value.totalRetentionPaths === undefined || value.totalRetentionPaths === null
            || Number.isSafeInteger(value.totalRetentionPaths) && Number(value.totalRetentionPaths) >= 0)
        && (value.omittedRetentionPaths === undefined || value.omittedRetentionPaths === null
            || Number.isSafeInteger(value.omittedRetentionPaths) && Number(value.omittedRetentionPaths) >= 0);
}

export function isHeapGrowthQueryResult(value: unknown): value is HeapGrowthQueryResult {
    return growthRecord(value)
        && typeof value.handle === "string" && typeof value.baselineHandle === "string" && value.view === "growth"
        && typeof value.baselineOrigin === "string" && typeof value.currentOrigin === "string"
        && Number.isSafeInteger(value.processId) && Number(value.processId) > 0
        && typeof value.baselineCapturedAt === "string" && typeof value.currentCapturedAt === "string"
        && typeof value.elapsed === "string" && (value.rankBy === "bytes" || value.rankBy === "instances")
        && safeNumber(value.baselineHeapBytes) && safeNumber(value.currentHeapBytes)
        && safeNumber(value.totalHeapGrowthBytes)
        && Number.isSafeInteger(value.totalGrowers) && Number(value.totalGrowers) >= 0
        && Array.isArray(value.growers) && value.growers.length <= 25 && value.growers.every(isGrowthTypeRow)
        && (value.notes === undefined || value.notes === null || stringArray(value.notes, 100));
}

export function isRetainedGrowthObjectiveResult(value: unknown): value is RetainedGrowthObjectiveResult {
    if (!growthRecord(value) || typeof value.handle !== "string" || typeof value.baselineHandle !== "string"
        || value.view !== "growth" || !growthRecord(value.objectiveRetainedGrowth)) return false;
    const evidence = value.objectiveRetainedGrowth;
    return evidence.schema === "dotnet-diagnostics/retained-growth-evidence/v1"
        && isTypeIdentity(evidence.targetType)
        && growthRecord(evidence.pairMethod)
        && growthRecord(evidence.counts)
        && Array.isArray(evidence.pairs) && evidence.pairs.length <= RETAINED_HEAP_EVIDENCE_LIMIT
        && evidence.pairs.every(growthRecord)
        && growthRecord(evidence.repeated)
        && growthRecord(evidence.allocationEvidence)
        && growthRecord(evidence.rootPathEvidence)
        && growthRecord(evidence.observerEffects)
        && stringArray(evidence.assumptions, 100)
        && stringArray(evidence.limitations, 100);
}

export function heapEvidenceCompatibility(
    baseline: RetainedHeapEvidenceDescriptor,
    current: RetainedHeapEvidenceDescriptor,
): { compatible: boolean; reason: string } {
    if (!baseline.available || !current.available)
        return { compatible: false, reason: baseline.unavailableReason ?? current.unavailableReason ?? "One retained handle is unavailable." };
    if (baseline.evidenceId === current.evidenceId)
        return { compatible: false, reason: "Baseline and current must be different captures." };
    if (baseline.processId !== current.processId)
        return { compatible: false, reason: "The captures belong to different process IDs." };
    if (baseline.source !== current.source)
        return { compatible: false, reason: "The captures use different heap sources." };
    const baselineTime = Date.parse(baseline.capturedAt);
    const currentTime = Date.parse(current.capturedAt);
    if (!Number.isFinite(baselineTime) || !Number.isFinite(currentTime))
        return { compatible: false, reason: "Capture chronology is unavailable." };
    if (baselineTime > currentTime)
        return { compatible: false, reason: "The baseline was captured after the current evidence." };
    return {
        compatible: true,
        reason: "Same process, source and chronological order. The CLI revalidates handles, target lifetime and evidence compatibility.",
    };
}

function qualitySupportsRanking(value: unknown): boolean {
    if (value === undefined || value === null) return true;
    if (!growthRecord(value) || !growthRecord(value.conclusions)) return false;
    return value.conclusions.regressionOrHealthyControl === "supported";
}

export function growthRankingSupported(result: HeapGrowthQueryResult): boolean {
    return qualitySupportsRanking(result.baselineQuality) && qualitySupportsRanking(result.currentQuality);
}

function renderRetainedGrowth(
    container: HTMLElement,
    result: HeapGrowthQueryResult | RetainedGrowthObjectiveResult,
    selectType: (typeIdentity: HeapTypeIdentity) => void,
): void {
    container.replaceChildren();
    const paragraph = (value: string, className?: string): HTMLElement => {
        const element = document.createElement("p");
        element.textContent = value;
        if (className) element.className = className;
        container.appendChild(element);
        return element;
    };
    const value = (input: unknown): string => input === undefined || input === null ? "Unavailable" : String(input);
    const list = (title: string, items: unknown): void => {
        const details = document.createElement("details");
        const summary = document.createElement("summary");
        summary.textContent = title;
        details.appendChild(summary);
        const values = Array.isArray(items) ? items : [];
        if (values.length === 0) {
            const empty = document.createElement("p");
            empty.textContent = "None reported. This is not proof of complete evidence.";
            details.appendChild(empty);
        } else {
            const node = document.createElement("ul");
            for (const item of values.slice(0, 100)) {
                const row = document.createElement("li");
                row.textContent = typeof item === "string" ? item : JSON.stringify(item);
                node.appendChild(row);
            }
            details.appendChild(node);
        }
        container.appendChild(details);
    };
    if ("objectiveRetainedGrowth" in result) {
        const evidence = (result as RetainedGrowthObjectiveResult).objectiveRetainedGrowth;
        const heading = document.createElement("h4");
        heading.textContent = "Objective retained evidence — " + evidence.targetType.typeFullName;
        heading.tabIndex = -1;
        container.appendChild(heading);
        paragraph("Retained heap is separate from sampled allocation traffic. Signed deltas describe observed shallow bytes and instances for the selected type; they are not a leak, regression, health or causality verdict.",
            "query-view-provenance");
        const primary = evidence.pairs.find(pair => pair.attemptId === "primary") ?? evidence.pairs[0] ?? {};
        const metrics = document.createElement("dl");
        metrics.className = "retained-growth-metrics";
        for (const [label, metric] of [
            ["Retained bytes", [primary.baselineRetainedBytes, primary.currentRetainedBytes, primary.retainedBytesDelta]],
            ["Retained instances", [primary.baselineRetainedInstances, primary.currentRetainedInstances, primary.retainedInstancesDelta]],
        ] as const) {
            const term = document.createElement("dt");
            term.textContent = label;
            const description = document.createElement("dd");
            description.textContent = `${value(metric[0])} → ${value(metric[1])} · delta ${value(metric[2])}`;
            metrics.append(term, description);
        }
        container.appendChild(metrics);
        const repeated = evidence.repeated;
        paragraph(`Repeated evidence: ${value(repeated.availability)} · accepted pairs ${value(repeated.acceptedPairs)}. `
            + `Median bytes delta ${value(repeated.medianRetainedBytesDelta)}; MAD ${value(repeated.retainedBytesDeltaMad)}. `
            + `Median instances delta ${value(repeated.medianRetainedInstancesDelta)}; MAD ${value(repeated.retainedInstancesDeltaMad)}.`);
        const bytesInterval = growthRecord(repeated.retainedBytesDeltaInterval) ? repeated.retainedBytesDeltaInterval : undefined;
        const instancesInterval = growthRecord(repeated.retainedInstancesDeltaInterval) ? repeated.retainedInstancesDeltaInterval : undefined;
        paragraph("Bootstrap intervals — bytes: "
            + (bytesInterval ? `${value(bytesInterval.lower)} to ${value(bytesInterval.upper)}` : "Unavailable")
            + "; instances: "
            + (instancesInterval ? `${value(instancesInterval.lower)} to ${value(instancesInterval.upper)}` : "Unavailable")
            + ". Three compatible independent process-run pairs are required.");
        const allocation = evidence.allocationEvidence;
        paragraph(`Allocation traffic (separate): ${value(allocation.availability)} · median sampled bytes ${value(allocation.medianSampledBytes)} · `
            + `sample events ${value(allocation.sampleEvents)} · lost/dropped ${value(allocation.lostOrDropped)}.`);
        const roots = evidence.rootPathEvidence;
        paragraph(`Root/path evidence: ${value(roots.availability)} · captures with paths ${value(roots.capturesWithPaths)}/${value(roots.totalCaptures)} · `
            + `matching paths ${value(roots.matchingPathCount)} · reason ${value(roots.unavailableReason)}.`);
        const observer = evidence.observerEffects;
        paragraph(`Observer effects: ${value(observer.gcDumpCaptures)} GC dump capture(s); observed Gen2 collections ${value(observer.observedGen2Collections)}. `
            + `Provenance: ${value(observer.provenance)}.`);
        list("Counts, method, identity and unavailable fields", [
            evidence.counts,
            evidence.pairMethod,
            primary.compatibilityIdentity,
            ...(Array.isArray(primary.compatibilityMismatches) ? primary.compatibilityMismatches : []),
            ...(Array.isArray(primary.unavailableFields) ? primary.unavailableFields : []),
            ...(Array.isArray(repeated.unavailableFields) ? repeated.unavailableFields : []),
        ]);
        list("Assumptions and limitations", [
            ...evidence.assumptions,
            ...evidence.limitations,
            ...(Array.isArray(primary.assumptions) ? primary.assumptions : []),
            ...(Array.isArray(primary.limitations) ? primary.limitations : []),
            ...(Array.isArray(repeated.assumptions) ? repeated.assumptions : []),
            ...(Array.isArray(repeated.limitations) ? repeated.limitations : []),
            ...(Array.isArray(allocation.assumptions) ? allocation.assumptions : []),
            ...(Array.isArray(allocation.limitations) ? allocation.limitations : []),
            ...(Array.isArray(observer.limitations) ? observer.limitations : []),
        ]);
        return;
    }

    const heading = document.createElement("h4");
    heading.textContent = "Retained-memory growth";
    heading.tabIndex = -1;
    container.appendChild(heading);
    paragraph(`Baseline ${new Date(result.baselineCapturedAt).toLocaleString()} → current ${new Date(result.currentCapturedAt).toLocaleString()} · `
        + `${result.baselineOrigin} · PID ${result.processId} · ranked by absolute retained ${result.rankBy}.`,
        "query-view-provenance");
    paragraph(`Observed heap bytes ${result.baselineHeapBytes} → ${result.currentHeapBytes} · delta ${result.totalHeapGrowthBytes}. `
        + "These values are retained heap observations, not allocation traffic.");
    if (!growthRankingSupported(result)) {
        paragraph("Growing-type ranking is unavailable because one or both captures do not support the required comparison conclusion. Unavailable is not zero.",
            "diagnostic-active-quality");
    } else if (result.growers.length === 0) {
        paragraph("No supported positive retained-growth rows were returned for this bounded pair. This does not establish a stable or healthy heap.");
    } else {
        const wrapper = document.createElement("div");
        wrapper.className = "query-view-table-wrap";
        const table = document.createElement("table");
        const head = document.createElement("tr");
        for (const title of ["Type", "Bytes", "Byte delta", "Instances", "Instance delta", "Root/path"]) {
            const cell = document.createElement("th");
            cell.scope = "col";
            cell.textContent = title;
            head.appendChild(cell);
        }
        table.appendChild(head);
        for (const row of result.growers) {
            const item = document.createElement("tr");
            const typeCell = document.createElement("td");
            const button = document.createElement("button");
            button.type = "button";
            button.className = "heap-type-link";
            button.textContent = row.typeFullName;
            button.addEventListener("click", () => selectType(row.identity ?? {
                typeFullName: row.typeFullName,
                moduleName: row.moduleName,
            }));
            typeCell.appendChild(button);
            const values = [
                `${row.baselineBytes} → ${row.currentBytes}`,
                row.bytesDelta,
                `${row.baselineInstances} → ${row.currentInstances}`,
                row.instancesDelta,
                row.retentionPaths?.length
                    ? `Representative path captured; ${row.omittedRetentionPaths ?? 0} omitted`
                    : "Unavailable or not collected",
            ];
            item.appendChild(typeCell);
            for (const cellValue of values) {
                const cell = document.createElement("td");
                cell.textContent = String(cellValue);
                item.appendChild(cell);
            }
            table.appendChild(item);
        }
        wrapper.appendChild(table);
        container.appendChild(wrapper);
        paragraph(`Showing ${result.growers.length} of ${result.totalGrowers} supported positive row(s); the list is bounded and is not an exhaustive heap inventory.`);
        paragraph("Selecting a type requests its objective evidence, then returns to the canonical heap explorer. Instance pages and object-specific GC-root paths remain separate live/dump reads and are available only when the chosen current snapshot supports them.");
    }
    list("Quality, identity, notes and limits", [
        { baselineHandle: result.baselineHandle, currentHandle: result.handle },
        result.baselineQuality,
        result.currentQuality,
        ...(result.notes ?? []),
        "The extension does not calculate retained-growth statistics; all measurements come from the CLI JSONL response.",
        "The retained handles are ephemeral bounded-store entries, not saved captures. Expiry, capacity eviction, replacement or connection closure requires choosing available evidence again or capturing explicitly.",
    ]);
}

export const RETAINED_MEMORY_GROWTH_SCRIPT = `const growthRecord = ${growthRecord.toString()};
${qualitySupportsRanking.toString()}
${growthRankingSupported.toString()}
${renderRetainedGrowth.toString()}`;

export const RETAINED_MEMORY_GROWTH_HTML = `
<details id="retainedGrowthLens" class="retained-growth-lens">
  <summary>Incremental retained-memory growth</summary>
  <div class="retained-growth-controls">
    <label>Baseline (earlier)<select id="retainedGrowthBaseline"></select></label>
    <label>Current (later)<select id="retainedGrowthCurrent"></select></label>
    <label>Rank growing types by<select id="retainedGrowthRank"><option value="bytes">Retained bytes</option><option value="instances">Retained instances</option></select></label>
    <button id="runRetainedGrowth" type="button">Compare selected evidence</button>
  </div>
  <p id="retainedGrowthStatus" role="status">Capture at least two compatible heap snapshots, then explicitly choose baseline and current evidence.</p>
  <div id="retainedGrowthResult"></div>
  <details><summary>Evidence ownership and limits</summary>
    <ul>
      <li>The extension sends JSONL queries to dotnet-diagnostics-cli; it does not collect EventPipe data or calculate retained-growth statistics in TypeScript.</li>
      <li>Retained handles are bounded, in-memory and connection-owned. They are not durable saved captures and can expire, be evicted or become unavailable.</li>
      <li>Allocation traffic is separate from retained heap. No result is a leak, regression, health, causality or no-leak verdict.</li>
    </ul>
  </details>
</details>`;

export const RETAINED_MEMORY_GROWTH_STYLES = `
.retained-growth-lens{margin:.75rem 0;padding:.65rem 0;border-top:1px solid var(--vscode-panel-border);border-bottom:1px solid var(--vscode-panel-border)}
.retained-growth-lens>summary{cursor:pointer;font-weight:600}
.retained-growth-controls{display:grid;grid-template-columns:minmax(12rem,1fr) minmax(12rem,1fr) minmax(10rem,.7fr) auto;gap:.55rem;align-items:end;margin:.65rem 0}
.retained-growth-controls label{display:flex;flex-direction:column;gap:.2rem}
.retained-growth-controls select,.retained-growth-controls button{min-height:2rem}
.retained-growth-metrics{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:.25rem .75rem}
.retained-growth-metrics dt{font-weight:600}.retained-growth-metrics dd{margin:0;overflow-wrap:anywhere}
@media(max-width:760px){.retained-growth-controls{grid-template-columns:1fr}.retained-growth-controls button{width:100%}}
@media(forced-colors:active){.retained-growth-lens{border-color:CanvasText}}
`;
