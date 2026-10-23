import type { CaptureViewResult } from "./captureKinds";
import { EVIDENCE_NAVIGATION_SCRIPT } from "./evidenceNavigation";

declare function enhanceEvidenceTables(parent: HTMLElement): void;

// Injected with Function.toString(): keep this function self-contained.
function renderCaptureView(containerId: string, result: unknown): void {
    const container = document.getElementById(containerId);
    if (!container) return;
    container.replaceChildren();
    const el = (tag: string, text?: string): HTMLElement => {
        const node = document.createElement(tag);
        if (text !== undefined) node.textContent = text;
        return node;
    };
    const cell = (value: unknown): string => value === null || value === undefined ? "Unavailable"
        : typeof value === "number" && !Number.isInteger(value) ? value.toFixed(2) : String(value);
    const view = result as CaptureViewResult;
    const ordinaryRequestCapture = ["requests", "kestrel", "db", "networking"].includes(view.kind);
    const requestTable = view.kind === "requests" && view.tables.length === 1 && view.tables[0].id === "requests"
        ? view.tables[0]
        : undefined;
    const requestStateColumnIndex = requestTable?.columns.findIndex(column => column.toLowerCase() === "long-running") ?? -1;
    const useRequestLayout = !!requestTable && requestStateColumnIndex >= 0
        && requestTable.rows.every(row => typeof row[requestStateColumnIndex] === "boolean");
    const parseRequestTimestamp = (value: unknown): { date: Date; sortValue: string } | undefined => {
        if (typeof value !== "string") return undefined;
        const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|([+-])(\d{2}):(\d{2}))$/i.exec(value);
        if (!match) return undefined;
        const year = Number(match[1]);
        const month = Number(match[2]);
        const day = Number(match[3]);
        const hour = Number(match[4]);
        const minute = Number(match[5]);
        const second = Number(match[6]);
        const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
        const daysInMonth = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
        const offsetHour = Number(match[10] ?? 0);
        const offsetMinute = Number(match[11] ?? 0);
        if (!daysInMonth || day < 1 || day > daysInMonth || hour > 23 || minute > 59 || second > 59
            || offsetHour > 14 || offsetMinute > 59 || (offsetHour === 14 && offsetMinute !== 0)
            || match[8].toUpperCase() === "-00:00") return undefined;
        const milliseconds = Date.parse(value);
        if (!Number.isFinite(milliseconds)) return undefined;
        const subMillisecondTicks = BigInt((match[7] ?? "").slice(3).padEnd(4, "0") || "0");
        return {
            date: new Date(milliseconds),
            sortValue: (BigInt(milliseconds) * 10_000n + subMillisecondTicks).toString(),
        };
    };
    const requestDateFormatter = useRequestLayout ? new Intl.DateTimeFormat("en-GB", {
        timeZone: "UTC", year: "numeric", month: "short", day: "2-digit",
        hour: "2-digit", minute: "2-digit", second: "2-digit", fractionalSecondDigits: 3, hourCycle: "h23",
    }) : undefined;
    const metric = (label: string): unknown => view.metrics.find(item => item.label === label)?.value;
    const number = (value: unknown): number | undefined =>
        typeof value === "number" && Number.isFinite(value) ? value : undefined;
    const formatNumber = (value: number): string => Number.isInteger(value)
        ? value.toLocaleString()
        : value.toLocaleString(undefined, { maximumFractionDigits: 2 });
    const thresholdMs = number(metric("Long-running threshold (ms)"));
    const requestSummaryParts: string[] = [];
    const starts = number(metric("Started"));
    const completed = number(metric("Completed"));
    if (starts !== undefined) requestSummaryParts.push(`${formatNumber(starts)} observed starts`);
    if (completed !== undefined) requestSummaryParts.push(`${formatNumber(completed)} matched completions`);
    const inFlight = number(metric("In flight at end"));
    const longRunning = number(metric("Long-running"));
    const closeSummaryParts: string[] = [];
    if (inFlight !== undefined) closeSummaryParts.push(`${formatNumber(inFlight)} tracked in flight`);
    if (longRunning !== undefined) {
        closeSummaryParts.push(`${formatNumber(longRunning)} tracked long-running`
            + (thresholdMs !== undefined ? ` (≥ ${formatNumber(thresholdMs)} ms)` : ""));
    }
    const requestSummary = [
        requestSummaryParts.length ? `Window: ${requestSummaryParts.join(" · ")}.` : "",
        closeSummaryParts.length ? `At close: ${closeSummaryParts.join(" · ")}.` : "",
    ].filter(Boolean).join(" ");
    container.appendChild(el("h4", useRequestLayout ? "In-flight requests" : view.title));
    container.appendChild(el("p", useRequestLayout && requestSummary ? requestSummary : view.summary));
    const guidance = (): HTMLElement => el("p", "Search, sorting and row details operate on returned rows only. "
            + "These tables do not establish a complete event timeline or request parent/child relationships. "
            + (view.kind === "allocation" ? "Allocation figures are sampled estimates, not a retained-heap inventory."
                : view.kind === "off_cpu" ? "Off-CPU evidence describes blocking, not CPU execution time."
                    : view.kind === "contention" ? "Contention observations are not proof of a deadlock." : ""));
    if (!useRequestLayout && !ordinaryRequestCapture) container.appendChild(guidance());
    const p = view.provenance;
    container.appendChild(el("p", useRequestLayout
        ? `PID ${p.processId} · ${p.requestedDurationSeconds}s window · Live capture, not saved`
        : `Live capture, not saved - pid ${p.processId} - requested ${p.requestedDurationSeconds}s`));
    const quality = (): void => {
        const e = view.evidence;
        container.appendChild(el("p", e.source === "not-reported"
            ? "Evidence quality: not reported by this collector." : `Evidence quality (${e.source})`));
        if (e.conclusions) {
            const c = e.conclusions;
            if (!ordinaryRequestCapture)
                container.appendChild(el("p", `Positive evidence: ${c.retainedPositive}; absence/exhaustive counts: ${c.absenceOrExhaustive}; healthy control: ${c.healthyControl}`));
            else for (const [label, value] of [
                ["Positive evidence", c.retainedPositive], ["Absence/exhaustive counts", c.absenceOrExhaustive],
                ["Healthy control", c.healthyControl],
            ]) container.appendChild(el("p", `${label}: ${value}`));
        }
    };
    if (ordinaryRequestCapture) quality();
    if (useRequestLayout || ordinaryRequestCapture) {
        if (view.notes.length) {
            const notes = el("ul");
            notes.className = "diagnostic-capture-notes";
            for (const note of view.notes) notes.appendChild(el("li", note));
            container.appendChild(notes);
        }
        for (const limitation of view.evidence.limitations)
            container.appendChild(el("p", `${limitation.category} (${limitation.scope}${limitation.affectedCount != null ? `, ${limitation.affectedCount}` : ""}): ${limitation.detail}`));
    }
    const table = (
        title: string,
        columns: string[],
        rows: unknown[][],
        parent: HTMLElement = container,
        rowFilter?: { selectId: string; columnIndex: number },
        dateColumnIndex?: number,
    ): void => {
        const node = el("table");
        node.className = "query-view-table";
        if (rowFilter) {
            node.id = "inFlightRequestsTable";
            node.dataset.rowFilterSelectId = rowFilter.selectId;
        }
        node.appendChild(el("caption", title));
        const head = el("tr");
        for (const column of columns) head.appendChild(el("th", column));
        const thead = el("thead");
        thead.appendChild(head);
        node.appendChild(thead);
        const body = el("tbody");
        for (const values of rows) {
            const row = el("tr");
            if (rowFilter) row.dataset.rowFilterState = values[rowFilter.columnIndex] === true ? "long-running" : "other";
            for (const [index, value] of values.entries()) {
                const cellNode = el("td");
                if (index === dateColumnIndex) {
                    const text = cell(value);
                    const timestamp = parseRequestTimestamp(value);
                    cellNode.dataset.sortValue = timestamp ? `timestamp:${timestamp.sortValue}` : `text:${text}`;
                    if (typeof value === "string") row.dataset.searchText = value;
                    if (timestamp && requestDateFormatter && typeof value === "string") {
                        const formatted = `${requestDateFormatter.format(timestamp.date)} UTC`;
                        const time = el("time", formatted);
                        time.setAttribute("datetime", value);
                        time.setAttribute("title", `Original ISO timestamp: ${value}`);
                        time.setAttribute("aria-label", `${formatted}. Original ISO timestamp: ${value}`);
                        cellNode.appendChild(time);
                    } else {
                        cellNode.textContent = text;
                    }
                } else {
                    cellNode.textContent = cell(value);
                }
                row.appendChild(cellNode);
            }
            body.appendChild(row);
        }
        node.appendChild(body);
        const wrapper = el("div");
        wrapper.className = "query-view-table-wrap";
        wrapper.appendChild(node);
        parent.appendChild(wrapper);
    };
    if (useRequestLayout && requestTable) {
        const longRunningRows = requestTable.rows.filter(row => row[requestStateColumnIndex] === true).length;
        container.appendChild(el("p", "In flight at window close · Filters apply to shown rows."));
        const filterLabel = el("label", "Show requests");
        filterLabel.className = "diagnostic-request-filter";
        const stateFilter = document.createElement("select");
        stateFilter.id = "inFlightRequestsStateFilter";
        stateFilter.setAttribute("aria-label", "Show requests");
        const allOption = document.createElement("option");
        allOption.value = "all";
        allOption.textContent = `All in-flight (${requestTable.rows.length} shown)`;
        stateFilter.appendChild(allOption);
        stateFilter.value = "all";
        const longOption = document.createElement("option");
        longOption.value = "long-running";
        longOption.textContent = thresholdMs !== undefined
            ? `At or above ${formatNumber(thresholdMs)} ms (${longRunningRows} shown)`
            : `Long-running (${longRunningRows} shown)`;
        stateFilter.appendChild(longOption);
        filterLabel.appendChild(stateFilter);
        container.appendChild(filterLabel);
        const panel = el("section");
        panel.className = "diagnostic-table-view";
        table(`Requests at window close (${requestTable.rows.length} shown of ${requestTable.totalRows} retained)`,
            requestTable.columns, requestTable.rows, panel,
            { selectId: stateFilter.id, columnIndex: requestStateColumnIndex },
            requestTable.columns.findIndex(column => column.toLowerCase() === "started"));
        if (requestTable.rows.length === 0) panel.appendChild(el("p", requestTable.emptyText));
        container.appendChild(panel);
        const disclaimer = el("p", "Search, sorting, and filtering apply only to returned rows. "
            + "This snapshot shows requests still tracked at window close, not a complete request timeline or parent/child map. "
            + "Start/stop pairing uses an activity ID or trace/span ID pair. Events without correlatable IDs are skipped, "
            + "and repeated starts replace the tracked entry.");
        disclaimer.className = "diagnostic-request-disclaimer";
        container.appendChild(disclaimer);
    } else {
        table("Metrics", ["Metric", "Value", "Note"], view.metrics.map(m => [m.label, m.value, m.note ?? ""]));
        const label = el("label", "Evidence table ");
        label.className = "diagnostic-evidence-table-label";
        const selector = document.createElement("select");
        selector.setAttribute("aria-label", "Diagnostic evidence table");
        label.appendChild(selector);
        container.appendChild(label);
        const panels: HTMLElement[] = [];
        for (const [index, t] of view.tables.entries()) {
            const panel = el("section");
            panel.className = "diagnostic-table-view";
            panel.hidden = index !== 0;
            panels.push(panel);
            const option = document.createElement("option");
            option.value = String(index);
            option.textContent = `${t.title} (${t.rows.length} returned; ${t.omittedRows} omitted)`;
            selector.appendChild(option);
            table(`${t.title} (${t.rows.length} of ${t.totalRows} retained rows shown)`, t.columns, t.rows, panel);
            if (t.rows.length === 0) panel.appendChild(el("p", t.emptyText));
            if (t.omittedRows > 0) panel.appendChild(el("p", `${t.omittedRows} retained row(s) omitted.`));
            container.appendChild(panel);
        }
        selector.value = "0";
        selector.addEventListener("change", () => panels.forEach((panel, index) => { panel.hidden = String(index) !== selector.value; }));
        if (!view.tables.length) label.hidden = true;
    }
    if (!ordinaryRequestCapture) quality();
    if (!useRequestLayout && !ordinaryRequestCapture) {
        for (const l of view.evidence.limitations)
            container.appendChild(el("p", `${l.category} (${l.scope}${l.affectedCount != null ? `, ${l.affectedCount}` : ""}): ${l.detail}`));
        const notes = el("ul");
        notes.className = "diagnostic-capture-notes";
        for (const note of view.notes) notes.appendChild(el("li", note));
        container.appendChild(notes);
    }
    if (ordinaryRequestCapture && !useRequestLayout) container.appendChild(guidance());
    enhanceEvidenceTables(container);
}

export const CAPTURE_VIEW_RENDERER_SCRIPT = EVIDENCE_NAVIGATION_SCRIPT + "\n" + renderCaptureView.toString();
