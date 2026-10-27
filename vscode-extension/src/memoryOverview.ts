export interface MemoryOverviewPoint {
    value: number;
    receivedAtMs: number;
    session: number;
    segment: number;
}

export interface MemoryOverviewGcObservation {
    receivedAtMs: number;
    generation: number;
    session: number;
    segment: number;
}

export interface MemoryOverviewInput {
    heapPoints: MemoryOverviewPoint[];
    allocationPoints: MemoryOverviewPoint[];
    gen2Collections: MemoryOverviewGcObservation[];
    sequenceGaps: number;
    retainedSessionCount: number;
    committedAvailable: boolean;
    allocationAvailable: boolean;
    seriesLimitReached: boolean;
    heapEvidenceAvailable: boolean;
}

export interface MemoryOverviewResult {
    state: "stable" | "rising" | "unavailable";
    headline: string;
    quality: string[];
    floorValues: number[];
    action: "capture-heap" | "open-allocation" | "inspect-heap";
}

function deriveMemoryOverview(input: MemoryOverviewInput): MemoryOverviewResult {
    const heapPoints = [...input.heapPoints].sort((left, right) => left.receivedAtMs - right.receivedAtMs);
    const gen2Collections = [...input.gen2Collections].sort((left, right) => left.receivedAtMs - right.receivedAtMs);
    const floorValues: number[] = [];
    let heapIndex = 0;
    for (let index = 0; index < gen2Collections.length; index++) {
        const collection = gen2Collections[index];
        const next = gen2Collections[index + 1];
        while (heapIndex < heapPoints.length && heapPoints[heapIndex].receivedAtMs < collection.receivedAtMs)
            heapIndex++;
        let floor = Number.POSITIVE_INFINITY;
        while (heapIndex < heapPoints.length && (!next || heapPoints[heapIndex].receivedAtMs < next.receivedAtMs)) {
            const point = heapPoints[heapIndex++];
            if (point.segment === collection.segment) floor = Math.min(floor, point.value);
        }
        if (Number.isFinite(floor)) floorValues.push(floor);
    }

    const quality: string[] = [];
    if (input.sequenceGaps > 0)
        quality.push(`${input.sequenceGaps} sequence gap(s) can hide counter or GC observations.`);
    if (input.retainedSessionCount > 1)
        quality.push("Earlier counter sessions are retained but are not joined to the current session.");
    if (heapPoints.length === 0)
        quality.push("Managed heap observations are unavailable in the selected retained receipt-time range.");
    if (!input.committedAvailable)
        quality.push("Committed-memory observations are unavailable; unavailable is not zero.");
    if (!input.allocationAvailable)
        quality.push("Allocation-rate observations are unavailable; unavailable is not zero.");
    if (floorValues.length < 2)
        quality.push("At least two observed Gen2 collections with following managed-heap observations are required.");
    if (input.seriesLimitReached)
        quality.push("The panel series limit was reached, so a required counter may have been omitted.");

    let state: MemoryOverviewResult["state"] = "unavailable";
    let headline = "Cannot assess the post-Gen2 managed heap floor from retained observations.";
    if (input.sequenceGaps === 0 && floorValues.length >= 2) {
        const first = floorValues[0];
        const last = floorValues[floorValues.length - 1];
        const tolerance = Math.max(Math.abs(first) * 0.05, Number.EPSILON);
        state = last - first > tolerance ? "rising" : "stable";
        headline = state === "rising"
            ? `Post-Gen2 managed heap floor rose from ${first} to ${last} across ${floorValues.length} observed Gen2 collections.`
            : `Post-Gen2 managed heap floor stayed stable or lower (${first} to ${last}) across ${floorValues.length} observed Gen2 collections.`;
    }

    const action = state === "rising"
        ? input.heapEvidenceAvailable ? "inspect-heap" : "capture-heap"
        : state === "stable" && input.allocationPoints.length > 0
            ? "open-allocation"
            : input.heapEvidenceAvailable ? "inspect-heap" : "capture-heap";
    return { state, headline, quality, floorValues, action };
}

interface MemoryOverviewSeries {
    label: string;
    unit: string;
    points: MemoryOverviewPoint[];
}

interface MemoryOverviewControllerOptions {
    canvas: HTMLCanvasElement;
    headline: HTMLElement;
    quality: HTMLElement;
    trackSummary: HTMLElement;
    limitations: HTMLElement;
    action: HTMLButtonElement;
    counters: Map<string, MemoryOverviewSeries>;
    gcRows: Array<MemoryOverviewGcObservation & { element: HTMLElement }>;
    currentSession: () => number;
    retainedSessionCount: () => number;
    sequenceGaps: () => number;
    seriesLimitReached: () => boolean;
    additionalQuality: () => string[];
    historyDurationMs: () => number;
    filterPoints: (points: MemoryOverviewPoint[]) => MemoryOverviewPoint[];
    heapEvidenceAvailable: () => boolean;
    captureHeap: () => void;
    openEvidence: (kind: "allocation" | "heap") => void;
}

function installMemoryOverview(
    options: MemoryOverviewControllerOptions,
    derive: typeof deriveMemoryOverview,
): { render: () => void; resize: () => void } {
    const context = options.canvas.getContext("2d");
    let lastAction: MemoryOverviewResult["action"] = "capture-heap";
    let lastLimitations = "";
    const keys = {
        heap: "System.Runtime/gc-heap-size",
        committed: "System.Runtime/gc-committed",
        allocation: "System.Runtime/alloc-rate",
        rss: "System.Runtime/working-set",
    };
    const format = (value: number, unit: string) =>
        new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value) + (unit ? ` ${unit}` : "");
    const visiblePoints = (series: MemoryOverviewSeries | undefined, session: number) =>
        series ? options.filterPoints(series.points.filter(point => point.session === session)) : [];
    const color = (name: string, fallback: string) => {
        const styles = getComputedStyle(document.body);
        if (typeof window.matchMedia === "function" && window.matchMedia("(forced-colors: active)").matches)
            return styles.color || "CanvasText";
        return styles.getPropertyValue(name).trim() || fallback;
    };

    function render(): void {
        const session = options.currentSession();
        const heap = options.counters.get(keys.heap);
        const committed = options.counters.get(keys.committed);
        const allocation = options.counters.get(keys.allocation);
        const rss = options.counters.get(keys.rss);
        const heapPoints = visiblePoints(heap, session);
        const committedPoints = visiblePoints(committed, session);
        const allocationPoints = visiblePoints(allocation, session);
        const rssPoints = visiblePoints(rss, session);
        const gen2 = options.gcRows.filter(row =>
            row.session === session && row.generation === 2 && !row.element.hidden);
        const result = derive({
            heapPoints,
            allocationPoints,
            gen2Collections: gen2,
            sequenceGaps: options.sequenceGaps(),
            retainedSessionCount: options.retainedSessionCount(),
            committedAvailable: committedPoints.length > 0,
            allocationAvailable: allocationPoints.length > 0,
            seriesLimitReached: options.seriesLimitReached(),
            heapEvidenceAvailable: options.heapEvidenceAvailable(),
        });

        const formatFloor = (value: number) => format(value, heap?.unit ?? "");
        const headline = result.state === "rising"
            ? `Post-Gen2 managed heap floor rose from ${formatFloor(result.floorValues[0])} to ${formatFloor(result.floorValues.at(-1) ?? 0)} across ${result.floorValues.length} observed Gen2 collections.`
            : result.state === "stable"
                ? `Post-Gen2 managed heap floor stayed stable or lower (${formatFloor(result.floorValues[0])} to ${formatFloor(result.floorValues.at(-1) ?? 0)}) across ${result.floorValues.length} observed Gen2 collections.`
                : result.headline;
        if (options.headline.textContent !== headline) options.headline.textContent = headline;
        options.headline.dataset.state = result.state;
        const quality = [...result.quality, ...options.additionalQuality()];
        const primaryQuality = quality[0] ?? "";
        if (options.quality.textContent !== primaryQuality) options.quality.textContent = primaryQuality;
        options.quality.hidden = quality.length === 0;
        const provenance = [
            "Timeline positions use webview receipt time; target timestamps are not treated as the same clock.",
            `Only the current counter session is assessed. Live history remains bounded to ${Math.round(options.historyDurationMs() / 1000)} seconds.`,
            "The headline compares the minimum retained managed-heap observation after each observed Gen2 collection. Rising means the latest floor exceeds the first by more than 5%; otherwise it is stable or lower.",
            "The overview does not establish object retention, a leak, or causality.",
        ];
        const limitations = [...quality, ...provenance];
        const limitationsKey = limitations.join("\n");
        if (limitationsKey !== lastLimitations) {
            lastLimitations = limitationsKey;
            options.limitations.replaceChildren();
            for (const text of limitations) {
                const item = document.createElement("li");
                item.textContent = text;
                options.limitations.appendChild(item);
            }
        }

        lastAction = result.action;
        const actionText = result.action === "open-allocation" ? "Open allocation evidence"
            : result.action === "inspect-heap" ? "Inspect heap evidence" : "Capture heap snapshot…";
        if (options.action.textContent !== actionText) options.action.textContent = actionText;
        options.action.title = result.action === "open-allocation"
            ? "Navigate to the allocation workspace without starting a capture."
            : result.action === "inspect-heap"
                ? "Navigate to the current heap evidence without starting a capture."
                : "Choose a heap source and explicitly start a point-in-time capture.";

        const descriptors = [
            { label: "Managed heap", series: heap, points: heapPoints, dash: [] as number[], token: "--vscode-charts-blue", fallback: "#3794ff" },
            { label: "Committed", series: committed, points: committedPoints, dash: [5, 3], token: "--vscode-charts-purple", fallback: "#b180d7" },
            { label: "Allocation rate", series: allocation, points: allocationPoints, dash: [2, 2], token: "--vscode-charts-orange", fallback: "#d18616" },
            { label: "Observed Gen2", series: undefined, points: [] as MemoryOverviewPoint[], dash: [] as number[], token: "--vscode-charts-red", fallback: "#f14c4c" },
            ...(rss ? [{ label: "RSS", series: rss, points: rssPoints, dash: [8, 3], token: "--vscode-charts-green", fallback: "#89d185" }] : []),
        ];
        const trackSummary = descriptors.map(item => {
            if (item.label === "Observed Gen2") return `${item.label}: ${gen2.length > 0 ? gen2.length : "unavailable"}`;
            const latest = item.points.at(-1);
            return `${item.label}: ${latest ? format(latest.value, item.series?.unit ?? "") : "unavailable"}`;
        }).join(" · ");
        if (options.trackSummary.textContent !== trackSummary) options.trackSummary.textContent = trackSummary;
        options.canvas.setAttribute("aria-label", `Memory temporal tracks. ${options.trackSummary.textContent}`);

        if (!context) return;
        const width = options.canvas.clientWidth;
        const rowHeight = 38;
        const height = descriptors.length * rowHeight + 8;
        const scale = window.devicePixelRatio || 1;
        const cssHeight = `${height}px`;
        const pixelWidth = Math.max(1, Math.floor(width * scale));
        const pixelHeight = Math.max(1, Math.floor(height * scale));
        if (options.canvas.style.height !== cssHeight) options.canvas.style.height = cssHeight;
        if (options.canvas.width !== pixelWidth) options.canvas.width = pixelWidth;
        if (options.canvas.height !== pixelHeight) options.canvas.height = pixelHeight;
        context.setTransform(scale, 0, 0, scale, 0, 0);
        context.clearRect(0, 0, width, height);
        context.font = `12px ${getComputedStyle(document.body).fontFamily}`;
        context.textBaseline = "middle";
        const labelWidth = width < 420 ? 94 : 122;
        const left = labelWidth;
        const right = Math.max(left + 1, width - 8);
        const allTimes = [
            ...descriptors.flatMap(item => item.points.map(point => point.receivedAtMs)),
            ...gen2.map(item => item.receivedAtMs),
        ];
        const firstTime = allTimes.length ? Math.min(...allTimes) : 0;
        const lastTime = allTimes.length ? Math.max(...allTimes) : firstTime;
        const timeSpan = Math.max(1, lastTime - firstTime);
        const foreground = color("--vscode-foreground", "#cccccc");
        const border = color("--vscode-panel-border", foreground);
        descriptors.forEach((item, index) => {
            const top = index * rowHeight + 4;
            const middle = top + rowHeight / 2;
            context.fillStyle = foreground;
            context.fillText(item.label, 4, middle, labelWidth - 10);
            context.strokeStyle = border;
            context.lineWidth = 1;
            context.setLineDash([]);
            context.beginPath();
            context.moveTo(left, top + rowHeight - 2);
            context.lineTo(right, top + rowHeight - 2);
            context.stroke();
            context.strokeStyle = color(item.token, item.fallback);
            context.fillStyle = context.strokeStyle;
            context.lineWidth = 2;
            context.setLineDash(item.dash);
            if (item.label === "Observed Gen2") {
                for (const collection of gen2) {
                    const x = left + (right - left) * (collection.receivedAtMs - firstTime) / timeSpan;
                    context.beginPath();
                    context.moveTo(x, top + 5);
                    context.lineTo(x, top + rowHeight - 6);
                    context.stroke();
                    context.fillRect(x - 2, top + 3, 4, 4);
                }
                if (gen2.length === 0) context.fillText("Unavailable", left + 4, middle);
                return;
            }
            if (item.points.length === 0) {
                context.fillText("Unavailable", left + 4, middle);
                return;
            }
            const values = item.points.map(point => point.value);
            const min = Math.min(...values);
            const max = Math.max(...values);
            const range = Math.max(max - min, Math.abs(max) * 0.01, Number.EPSILON);
            context.beginPath();
            item.points.forEach((point, pointIndex) => {
                const x = left + (right - left) * (point.receivedAtMs - firstTime) / timeSpan;
                const y = top + rowHeight - 7 - (point.value - min) / range * (rowHeight - 14);
                if (pointIndex === 0 || point.segment !== item.points[pointIndex - 1].segment)
                    context.moveTo(x, y);
                else context.lineTo(x, y);
            });
            context.stroke();
        });
        context.setLineDash([]);
    }

    options.action.addEventListener("click", () => {
        if (lastAction === "capture-heap") options.captureHeap();
        else options.openEvidence(lastAction === "open-allocation" ? "allocation" : "heap");
    });
    return { render, resize: render };
}

export const MEMORY_OVERVIEW_SCRIPT = `${deriveMemoryOverview.toString()}\n${installMemoryOverview.toString()}`;

export const MEMORY_OVERVIEW_HTML = `
    <details id="memoryOverview" class="memory-overview" open>
      <summary id="memoryOverviewTitle">Memory &amp; GC overview</summary>
      <div class="memory-overview-content" role="region" aria-labelledby="memoryOverviewTitle">
      <p id="memoryOverviewHeadline" class="memory-overview-headline" role="status">Cannot assess the post-Gen2 managed heap floor from retained observations.</p>
      <p id="memoryOverviewQuality" class="diagnostic-active-quality" role="status">Waiting for retained managed-heap and Gen2 observations.</p>
      <canvas id="memoryOverviewChart" aria-label="Memory temporal tracks"></canvas>
      <p id="memoryOverviewTrackSummary" class="memory-overview-track-summary">Managed heap: unavailable · Committed: unavailable · Allocation rate: unavailable · Observed Gen2: unavailable</p>
      <div class="panel-actions"><button id="memoryOverviewAction" type="button">Capture heap snapshot…</button></div>
      <details class="memory-overview-details">
        <summary>Quality, provenance and limits</summary>
        <ul id="memoryOverviewLimitations"></ul>
      </details>
      </div>
    </details>`;

export const MEMORY_OVERVIEW_STYLES = `
    .memory-overview { margin:.8rem 0 1rem; padding:.75rem 0; border-top:1px solid var(--vscode-panel-border); border-bottom:1px solid var(--vscode-panel-border); }
    .memory-overview > summary { cursor:pointer; font-size:1.05rem; font-weight:600; margin:.1rem 0 .35rem; }
    .memory-overview-content { margin-top:.4rem; }
    .memory-overview-headline { margin:.25rem 0; font-weight:600; max-width:72ch; }
    .memory-overview #memoryOverviewQuality { margin:.35rem 0 .5rem; }
    .memory-overview canvas { display:block; width:100%; height:160px; border:1px solid var(--vscode-panel-border); }
    .memory-overview-track-summary { margin:.4rem 0; color:var(--vscode-descriptionForeground); font-size:.85rem; overflow-wrap:anywhere; }
    .memory-overview-details { margin-top:.45rem; }
    .memory-overview-details summary { cursor:pointer; }
    .memory-overview-details li { margin:.25rem 0; overflow-wrap:anywhere; }
    @media (max-width:480px) {
      .memory-overview .panel-actions { align-items:stretch; }
      .memory-overview .panel-actions button { width:100%; }
    }
    @media (forced-colors:active) {
      .memory-overview, .memory-overview canvas { border-color:CanvasText; }
      .memory-overview #memoryOverviewQuality { color:CanvasText; }
    }
`;

export { deriveMemoryOverview };
