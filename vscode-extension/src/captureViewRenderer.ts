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
    container.appendChild(el("h4", view.title));
    container.appendChild(el("p", view.summary));
    container.appendChild(el("p", "Search, sorting and row details operate on returned rows only. "
        + "These tables do not establish a complete event timeline or request parent/child relationships. "
        + (view.kind === "allocation" ? "Allocation figures are sampled estimates, not a retained-heap inventory."
            : view.kind === "off_cpu" ? "Off-CPU evidence describes blocking, not CPU execution time."
                : view.kind === "contention" ? "Contention observations are not proof of a deadlock." : "")));
    const p = view.provenance;
    container.appendChild(el("p", `Live capture, not saved - pid ${p.processId} - requested ${p.requestedDurationSeconds}s`));
    const table = (title: string, columns: string[], rows: unknown[][], parent: HTMLElement = container): void => {
        const node = el("table");
        node.className = "query-view-table";
        node.appendChild(el("caption", title));
        const head = el("tr");
        for (const column of columns) head.appendChild(el("th", column));
        const thead = el("thead");
        thead.appendChild(head);
        node.appendChild(thead);
        const body = el("tbody");
        for (const values of rows) {
            const row = el("tr");
            for (const value of values) row.appendChild(el("td", cell(value)));
            body.appendChild(row);
        }
        node.appendChild(body);
        const wrapper = el("div");
        wrapper.className = "query-view-table-wrap";
        wrapper.appendChild(node);
        parent.appendChild(wrapper);
    };
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
    const e = view.evidence;
    container.appendChild(el("p", e.source === "not-reported"
        ? "Evidence quality: not reported by this collector." : `Evidence quality (${e.source})`));
    if (e.conclusions) {
        const c = e.conclusions;
        container.appendChild(el("p", `Positive evidence: ${c.retainedPositive}; absence/exhaustive counts: ${c.absenceOrExhaustive}; healthy control: ${c.healthyControl}`));
    }
    for (const l of e.limitations)
        container.appendChild(el("p", `${l.category} (${l.scope}${l.affectedCount != null ? `, ${l.affectedCount}` : ""}): ${l.detail}`));
    const notes = el("ul");
    notes.className = "diagnostic-capture-notes";
    for (const note of view.notes) notes.appendChild(el("li", note));
    container.appendChild(notes);
    enhanceEvidenceTables(container);
}

export const CAPTURE_VIEW_RENDERER_SCRIPT = EVIDENCE_NAVIGATION_SCRIPT + "\n" + renderCaptureView.toString();
