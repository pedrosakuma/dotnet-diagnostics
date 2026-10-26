interface CpuOverviewCaptureSummary {
    processId?: number;
    startedAt?: string;
    duration?: string;
    totalSamples?: number;
}

interface CpuOverviewActions {
    openProfileView: (view: string, rootMethodFilter?: string) => void;
    openEvidence: (kind: string) => void;
}

function renderCpuInvestigationOverview(
    container: HTMLElement,
    result: unknown,
    capture: CpuOverviewCaptureSummary | undefined,
    actions: CpuOverviewActions,
): void {
    const isRecord = (value: unknown): value is Record<string, unknown> =>
        value !== null && typeof value === "object" && !Array.isArray(value);
    const validCount = (value: unknown): value is number =>
        typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
    const data = isRecord(result) && isRecord(result.data) ? result.data : {};
    const totalSamples = validCount(data.totalSamples)
        ? data.totalSamples
        : validCount(capture?.totalSamples) ? capture.totalSamples : undefined;
    const topBusy = Array.isArray(data.topBusyMethods)
        ? data.topBusyMethods.filter(isRecord)
        : [];
    const topWaits = Array.isArray(data.topWaitCategories)
        ? data.topWaitCategories.filter(isRecord)
        : [];
    const topMethod = topBusy[0];
    const hotPathLeaf = isRecord(data.hotPathLeaf) ? data.hotPathLeaf : undefined;
    const selfSamples = isRecord(data.selfSamples) ? data.selfSamples : undefined;
    const runningSamples = validCount(selfSamples?.runningSamples) ? selfSamples.runningSamples : undefined;
    const waitingSamples = validCount(selfSamples?.waitingSamples) ? selfSamples.waitingSamples : undefined;
    const unknownSamples = validCount(selfSamples?.unknownSamples) ? selfSamples.unknownSamples : undefined;
    const schedulerTotal = runningSamples !== undefined && waitingSamples !== undefined && unknownSamples !== undefined
        ? runningSamples + waitingSamples + unknownSamples
        : undefined;
    const schedulerBreakdownValid = totalSamples !== undefined && schedulerTotal !== undefined
        && schedulerTotal <= totalSamples;
    const evidenceKind = typeof data.evidenceKind === "string" ? data.evidenceKind : "LegacyUnknown";
    const evidenceBackend = typeof data.evidenceBackend === "string" ? data.evidenceBackend : "LegacyUnknown";
    const onCpu = evidenceKind === "OsOnCpuSamples";
    const heuristic = evidenceKind === "StackFrequencyWithHeuristicWaits";
    const missing = totalSamples === undefined && !topMethod && !hotPathLeaf;
    const empty = totalSamples === 0;
    const degraded = !empty && totalSamples !== undefined && !topMethod && !hotPathLeaf;
    const unknownHeavy = !empty && schedulerBreakdownValid && unknownSamples! > 0
        && unknownSamples! * 2 >= totalSamples!;
    const methodName = typeof topMethod?.method === "string" ? topMethod.method : undefined;
    const methodModule = typeof topMethod?.module === "string" ? topMethod.module : undefined;
    const methodExclusive = validCount(topMethod?.exclusiveSamples) ? topMethod.exclusiveSamples : undefined;
    const methodSelf = isRecord(topMethod?.selfSamples) ? topMethod.selfSamples : undefined;
    const methodRunning = validCount(methodSelf?.runningSamples) ? methodSelf.runningSamples : undefined;
    const hotMethod = typeof hotPathLeaf?.method === "string" ? hotPathLeaf.method : undefined;
    const hotModule = typeof hotPathLeaf?.module === "string" ? hotPathLeaf.module : undefined;
    const hotInclusive = validCount(hotPathLeaf?.inclusiveSamples) ? hotPathLeaf.inclusiveSamples : undefined;
    const hotDepth = validCount(data.hotPathDepth) ? data.hotPathDepth : undefined;

    const percent = (value: number | undefined, total: number | undefined): string =>
        value !== undefined && total !== undefined && total > 0
            ? (100 * value / total).toFixed(1) + "%"
            : "Unavailable";
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
    const item = (list: HTMLElement, value: string): void => {
        const element = document.createElement("li");
        element.textContent = value;
        list.appendChild(element);
    };
    const fact = (label: string, value: string, detail: string): HTMLElement => {
        const card = document.createElement("section");
        card.className = "cpu-overview-fact";
        const heading = document.createElement("h4");
        heading.textContent = label;
        card.appendChild(heading);
        text(card, value, "cpu-overview-fact-value");
        text(card, detail, "query-view-provenance");
        return card;
    };

    const overview = document.createElement("div");
    overview.className = "cpu-overview";
    const headline = document.createElement("section");
    headline.className = "cpu-overview-headline";
    const title = document.createElement("h4");
    if (missing) {
        title.textContent = "Capture sample count and attributable methods are unavailable.";
    } else if (empty) {
        title.textContent = "No stack samples were captured.";
    } else if (degraded) {
        title.textContent = String(totalSamples) + " samples were captured, but no attributable method or loaded hot path was returned.";
    } else if (unknownHeavy) {
        title.textContent = "Scheduler state is unknown for " + String(unknownSamples) + " of "
            + String(totalSamples) + " captured samples.";
    } else if (onCpu && methodName) {
        title.textContent = methodName + " is the top observed on-CPU method.";
    } else if (methodName) {
        title.textContent = methodName + " is the most frequent attributable leaf.";
    } else {
        title.textContent = "The capture has stack evidence, but its leading attributable method is unavailable.";
    }
    headline.appendChild(title);
    text(headline, "Evidence status: " + (missing || empty || degraded ? "attribution unavailable"
        : unknownHeavy ? "limited by unknown scheduler state"
            : onCpu ? "observed on-CPU samples"
                : heuristic ? "observed stacks with inferred waits"
                    : "observed stacks with scheduler state unavailable"), "cpu-overview-status");
    text(headline, onCpu
        ? "Counts estimate where on-CPU observations occurred; they are not elapsed CPU time or utilization."
        : heuristic
            ? "Stack frequency is observed. Wait labels are name-based heuristics; scheduler state is not established."
            : "The backend did not report authoritative scheduler-state semantics.", "query-view-provenance");
    overview.appendChild(headline);

    const metadata = document.createElement("dl");
    metadata.className = "cpu-overview-metadata";
    const metadataItem = (label: string, value: string, titleText?: string): void => {
        const term = document.createElement("dt");
        term.textContent = label;
        const description = document.createElement("dd");
        description.textContent = value;
        if (titleText) description.title = titleText;
        metadata.append(term, description);
    };
    metadataItem("Samples", totalSamples === undefined ? "Unavailable" : String(totalSamples));
    metadataItem("Backend", evidenceBackend === "LegacyUnknown" ? "Unavailable" : evidenceBackend);
    const startedAt = typeof capture?.startedAt === "string" ? capture.startedAt : undefined;
    const startedDate = startedAt ? new Date(startedAt) : undefined;
    metadataItem("Started", startedDate && Number.isFinite(startedDate.getTime())
        ? startedDate.toLocaleString() : "Unavailable", startedAt);
    metadataItem("Window", typeof capture?.duration === "string" ? capture.duration : "Unavailable");
    overview.appendChild(metadata);

    if (schedulerBreakdownValid && totalSamples! > 0) {
        const scheduler = document.createElement("section");
        scheduler.className = "cpu-overview-scheduler";
        const schedulerHeading = document.createElement("h4");
        schedulerHeading.textContent = "Scheduler-state evidence";
        scheduler.appendChild(schedulerHeading);
        const bar = document.createElement("div");
        bar.className = "cpu-overview-bar";
        bar.setAttribute("role", "img");
        const unavailableSamples = totalSamples! - schedulerTotal!;
        bar.setAttribute("aria-label", (onCpu
            ? String(runningSamples) + " on-CPU"
            : "on-CPU state not established" + (runningSamples! > 0
                ? "; " + String(runningSamples) + " returned running-category samples without OS-backed semantics" : ""))
            + ", " + String(waitingSamples) + " heuristic-wait, " + String(unknownSamples) + " unknown, "
            + String(unavailableSamples) + " unclassified samples.");
        for (const [className, count] of [
            [onCpu ? "running" : "unavailable", runningSamples!],
            ["waiting", waitingSamples!],
            ["unknown", unknownSamples!],
            ["unavailable", unavailableSamples],
        ] as const) {
            if (count <= 0) continue;
            const segment = document.createElement("span");
            segment.className = "cpu-overview-segment cpu-overview-" + className;
            segment.style.width = (100 * count / totalSamples!).toFixed(4) + "%";
            bar.appendChild(segment);
        }
        scheduler.appendChild(bar);
        const legend = document.createElement("ul");
        legend.className = "cpu-overview-legend";
        item(legend, onCpu ? "Observed on-CPU: " + String(runningSamples)
            : "On-CPU state: not established by this backend");
        if (!onCpu && runningSamples! > 0) {
            item(legend, "Returned running category without OS-backed semantics: " + String(runningSamples));
        }
        item(legend, "Inferred heuristic wait: " + String(waitingSamples));
        item(legend, "Unknown scheduler state: " + String(unknownSamples));
        if (unavailableSamples > 0) item(legend, "Not classified by the returned breakdown: " + String(unavailableSamples));
        scheduler.appendChild(legend);
        overview.appendChild(scheduler);
    }

    const facts = document.createElement("div");
    facts.className = "cpu-overview-facts";
    facts.appendChild(fact(
        onCpu ? "Observed busy method" : "Top attributable method",
        methodName ?? "Unavailable",
        methodName
            ? (methodModule ? methodModule + " - " : "")
                + (onCpu && methodRunning !== undefined
                    ? String(methodRunning) + " running self samples"
                    : String(methodExclusive ?? "Unavailable") + " exclusive samples")
                + " (" + percent(onCpu ? methodRunning ?? methodExclusive : methodExclusive, totalSamples) + " of capture)."
            : "The triage projection returned no attributable method.",
    ));
    facts.appendChild(fact(
        "Dominant loaded hot path",
        hotMethod ?? "Unavailable",
        hotMethod
            ? (hotModule ? hotModule + " - " : "")
                + String(hotInclusive ?? "Unavailable") + " inclusive samples"
                + (hotDepth === undefined ? "." : " at loaded depth " + String(hotDepth) + ".")
            : "No dominant loaded path was returned. This does not establish that no hot path exists.",
    ));
    overview.appendChild(facts);

    const primaryActions = document.createElement("div");
    primaryActions.className = "cpu-overview-actions";
    if (methodName) {
        primaryActions.appendChild(button("Inspect callers and callees", () =>
            actions.openProfileView("caller-callee", methodName)));
    }
    primaryActions.append(
        button("Open call tree", () => actions.openProfileView("call-tree")),
        button("Open hot path", () => actions.openProfileView("hot-path")),
        button("Open top methods", () => actions.openProfileView("top-methods")),
    );
    overview.appendChild(primaryActions);

    const related = document.createElement("section");
    related.className = "cpu-overview-related";
    const relatedHeading = document.createElement("h4");
    relatedHeading.textContent = "Related evidence is not part of this CPU capture";
    related.appendChild(relatedHeading);
    text(related, "Navigate without collecting. Capture only if that diagnostic question is relevant.", "query-view-provenance");
    const relatedActions = document.createElement("div");
    relatedActions.className = "cpu-overview-actions";
    relatedActions.append(
        button("Allocations", () => actions.openEvidence("allocation")),
        button("Threads & waits", () => actions.openEvidence("thread-snapshot")),
        button("Requests & throughput", () => actions.openEvidence("requests")),
    );
    related.appendChild(relatedActions);
    overview.appendChild(related);

    const quality = document.createElement("details");
    quality.className = "cpu-overview-quality";
    const qualitySummary = document.createElement("summary");
    qualitySummary.textContent = "Evidence quality and limits";
    quality.appendChild(qualitySummary);
    const qualityGrid = document.createElement("div");
    qualityGrid.className = "cpu-overview-quality-grid";
    const qualityGroup = (headingText: string): HTMLElement => {
        const group = document.createElement("section");
        const heading = document.createElement("h4");
        heading.textContent = headingText;
        const list = document.createElement("ul");
        group.append(heading, list);
        qualityGrid.appendChild(group);
        return list;
    };
    const observed = qualityGroup("Observed");
    item(observed, totalSamples === undefined ? "Captured sample count was not returned."
        : String(totalSamples) + " stack samples were returned.");
    item(observed, evidenceBackend === "LegacyUnknown" ? "Capture backend was not reported."
        : "Capture backend: " + evidenceBackend + ".");
    if (methodName) item(observed, "Top attributable leaf: " + methodName + ".");
    if (hotMethod) item(observed, "Dominant loaded hot-path leaf: " + hotMethod + ".");
    const inferred = qualityGroup("Inferred");
    if (topWaits.length > 0) {
        const wait = topWaits[0];
        item(inferred, "Top name-based heuristic wait: "
            + (typeof wait.waitReason === "string" ? wait.waitReason : "Unavailable") + ".");
    } else {
        item(inferred, "No heuristic wait category was returned; this is not evidence that waiting was zero.");
    }
    item(inferred, "The hot path is the dominant loaded branch of a bounded projection, not a global diagnosis.");
    const unavailable = qualityGroup("Unavailable");
    if (!onCpu) item(unavailable, "OS scheduler-confirmed on-CPU attribution and measured CPU utilization.");
    if (!schedulerBreakdownValid) {
        item(unavailable, "A running / heuristic-wait / unknown breakdown was not returned or did not reconcile with the capture total.");
    }
    if (unknownSamples !== undefined && unknownSamples > 0) {
        item(unavailable, "The triage contract does not distinguish whether unknown scheduler state came from unmatched, unresolved, or native leaves.");
    }
    item(unavailable, "Per-sample timing inside the capture window; the returned view contains capture-wide aggregates.");
    item(unavailable, "Throughput, contention, allocation pressure, and causal correlation without separate evidence.");
    item(unavailable, "Application/framework/runtime/GC ownership composition; the current evidence does not support authoritative classification.");
    quality.appendChild(qualityGrid);
    if (isRecord(result) && typeof result.summary === "string" && result.summary.length > 0) {
        text(quality, result.summary, "query-view-provenance");
    }
    overview.appendChild(quality);
    container.appendChild(overview);
}

export const CPU_INVESTIGATION_OVERVIEW_SCRIPT = renderCpuInvestigationOverview.toString();

export const CPU_INVESTIGATION_OVERVIEW_STYLES = `
    .cpu-overview { display:grid; gap:.8rem; }
    .cpu-overview-headline { padding-bottom:.65rem; border-bottom:1px solid var(--vscode-panel-border); }
    .cpu-overview-headline h4 { margin:.1rem 0 .35rem; font-size:1.05rem; overflow-wrap:anywhere; }
    .cpu-overview-status { margin:.2rem 0; font-weight:600; }
    .cpu-overview-metadata { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:.35rem .8rem; margin:0; }
    .cpu-overview-metadata dt { color:var(--vscode-descriptionForeground); font-size:.8rem; }
    .cpu-overview-metadata dd { margin:0; overflow-wrap:anywhere; font-variant-numeric:tabular-nums; }
    .cpu-overview-scheduler, .cpu-overview-related, .cpu-overview-fact {
        border:1px solid var(--vscode-panel-border); padding:.65rem;
    }
    .cpu-overview-scheduler h4, .cpu-overview-related h4, .cpu-overview-fact h4,
    .cpu-overview-quality h4 { margin:.1rem 0 .4rem; }
    .cpu-overview-bar { display:flex; width:100%; height:.85rem; overflow:hidden; border:1px solid var(--vscode-panel-border); }
    .cpu-overview-segment { display:block; min-width:1px; }
    .cpu-overview-running { background:var(--vscode-charts-blue); }
    .cpu-overview-waiting { background:var(--vscode-charts-yellow); }
    .cpu-overview-unknown { background:var(--vscode-charts-purple); }
    .cpu-overview-unavailable { background:var(--vscode-disabledForeground); }
    .cpu-overview-legend { display:flex; flex-wrap:wrap; gap:.3rem 1rem; margin:.5rem 0 0; padding:0; list-style:none; font-size:.85rem; }
    .cpu-overview-facts, .cpu-overview-quality-grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:.6rem; }
    .cpu-overview-fact-value { margin:.2rem 0; font-weight:600; overflow-wrap:anywhere; }
    .cpu-overview-actions { display:flex; flex-wrap:wrap; gap:.4rem; }
    .cpu-overview-quality > summary { font-weight:600; }
    .cpu-overview-quality-grid { grid-template-columns:repeat(3,minmax(0,1fr)); margin-top:.6rem; }
    .cpu-overview-quality-grid ul { margin:.2rem 0; padding-inline-start:1.2rem; }
    @media (max-width:650px) {
        .cpu-overview-metadata, .cpu-overview-facts, .cpu-overview-quality-grid { grid-template-columns:1fr; }
        .cpu-overview-actions button { flex:1 1 12rem; min-width:0; }
    }
    @media (forced-colors: active) {
        .cpu-overview-headline, .cpu-overview-scheduler, .cpu-overview-related, .cpu-overview-fact,
        .cpu-overview-bar { border-color:CanvasText; }
        .cpu-overview-segment { forced-color-adjust:none; }
        .cpu-overview-running { background:Highlight; }
        .cpu-overview-waiting { background:Mark; }
        .cpu-overview-unknown { background:GrayText; }
        .cpu-overview-unavailable { background:Canvas; }
    }
`;
